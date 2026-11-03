using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Social;

namespace Kingdom.Net.Server.King
{
    /// <summary>How a <c>/king/prize</c> route answered. The client branches on the lowercase name.</summary>
    public enum PrizeChoiceStatus
    {
        /// <summary>GET read the open choices; the list may be empty.</summary>
        Ok,
        /// <summary>POST granted the back and closed the choice.</summary>
        Granted,
        /// <summary>POST named a reign with no open choice for this account: never opened, another
        /// player's, already chosen, or settled in gold because the pool ran dry.</summary>
        None,
        /// <summary>POST was oversized or malformed, or named a back the open choice does not offer.</summary>
        Invalid,
        Unauthenticated,
        RateLimited,
        Unavailable,
    }

    /// <summary>One open choice: the reign and place that earned it, and the pool backs the player
    /// does not own, in pool order.</summary>
    public sealed record PrizeChoice(int Reign, int Place, IReadOnlyList<int> BackIds);

    public sealed record PrizeChoiceResult(PrizeChoiceStatus Status, IReadOnlyList<PrizeChoice> Choices, int HttpStatus);

    /// <summary>
    /// <c>GET /king/prize</c> and <c>POST /king/prize/choose</c> (#421): a player who takes a top-three
    /// place they have held before picks one back they do not own from the pool. The sweep opens the
    /// choice (<see cref="KingPayoutSweep"/>); this settles it. Nothing expires: an unclaimed choice
    /// waits on the menu for good.
    ///
    /// <para><b>The back id is client-authored</b> (security.md, "Choosing a King's back"). It is
    /// granted only when this account has an open choice for that reign, the id is in the pool, and
    /// the ledger says the account does not own it. The body is capped at
    /// <see cref="MaxBodyBytes"/> and read before authentication.</para>
    ///
    /// <para><b>Race-safe by a conditional close.</b> A grant is keyed on the back as well as the
    /// reign (<see cref="CardBackOwnership"/>), so a key alone would let two POSTs naming different
    /// backs both grant. The close comes first and only one caller can make it
    /// (<see cref="IFavorStore.CloseChoiceAsync"/>); it records the back, and that caller grants,
    /// keyed <c>king-choice:{reign}</c>. <b>A choice never reopens</b>, so no second pick is ever
    /// possible. A grant that throws, times out, or is cut off by a crash leaves the row closed
    /// and undelivered, and <see cref="DeliverAsync"/> grants the recorded back again on the same
    /// key, on the player's next GET and on every sweep, until it is confirmed. The ledger answers
    /// a repeated key as a duplicate, so a grant that landed before its timeout is not paid twice.</para>
    ///
    /// <para><b>Two choices can compete for the last pool back.</b> A choice whose pool has nothing
    /// left the player lacks is settled when it is next read: <see cref="FavorPrizeSettings.PoolEmptyGold"/>,
    /// keyed as the sweep keys it, so it can never pay twice, and the choice closes.</para>
    /// </summary>
    public sealed class PrizeChoiceEndpoint
    {
        /// <summary><c>{"reign":…,"backId":…}</c> fits in a fraction of this.</summary>
        public const int MaxBodyBytes = 256;

        private static readonly IReadOnlyList<PrizeChoice> NoChoices = Array.Empty<PrizeChoice>();

        private readonly IIdentityTokenVerifier? _verifier;
        private readonly IAccountStore? _accounts;
        private readonly IFavorStore _favor;
        private readonly IPrizeBacks? _backs;
        private readonly EconomyService? _economy;
        private readonly FavorPrizeSettings? _prizes;
        private readonly IReadOnlyList<int> _poolIds;
        private readonly FriendRequestLimiter _limiter;

        /// <param name="poolIds">The backs a choice offers, <c>CardBackDefinitions.PoolIds</c> in production.</param>
        /// <param name="limiter">Per account, on POST only.</param>
        public PrizeChoiceEndpoint(IIdentityTokenVerifier? verifier, IAccountStore? accounts, IFavorStore favor,
            IPrizeBacks? backs, EconomyService? economy, FavorPrizeSettings? prizes,
            IReadOnlyList<int> poolIds, FriendRequestLimiter limiter)
        {
            _verifier = verifier;
            _accounts = accounts;
            _favor = favor ?? throw new ArgumentNullException(nameof(favor));
            _backs = backs;
            _economy = economy;
            _prizes = prizes;
            _poolIds = poolIds ?? throw new ArgumentNullException(nameof(poolIds));
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        }

        private bool Configured => _verifier != null && _accounts != null && _backs != null &&
                                   _economy != null && _prizes != null;

        /// <summary>The bearer's open choices, each with the pool backs they lack.</summary>
        public async Task<PrizeChoiceResult> ReadAsync(string? bearer, CancellationToken ct = default)
        {
            if (!Configured) return Answer(PrizeChoiceStatus.Unavailable, 503);
            var (account, failure) = await ResolveAsync(bearer, ct).ConfigureAwait(false);
            if (account is null) return failure!;

            try
            {
                // A back chosen earlier whose grant never confirmed: delivered before anything is listed.
                foreach (FavorPrize chosen in await _favor.UndeliveredChoicesAsync(account.PlayerId, ct).ConfigureAwait(false))
                    await DeliverAsync(_favor, _backs!, account, chosen, ct).ConfigureAwait(false);

                IReadOnlyList<FavorPrize> open = await _favor.OpenChoicesAsync(account.PlayerId, ct).ConfigureAwait(false);
                if (open.Count == 0) return new PrizeChoiceResult(PrizeChoiceStatus.Ok, NoChoices, 200);

                IReadOnlyList<int> lacking = await LackingAsync(account, ct).ConfigureAwait(false);
                var choices = new List<PrizeChoice>(open.Count);
                foreach (FavorPrize prize in open)
                {
                    if (lacking.Count == 0)
                        await SettleInGoldAsync(account, prize.Reign, ct).ConfigureAwait(false);
                    else
                        choices.Add(new PrizeChoice(prize.Reign, prize.Place, lacking));
                }
                return new PrizeChoiceResult(PrizeChoiceStatus.Ok, choices, 200);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[king] GET /king/prize for player_id={0} failed; answering 503: {1}",
                    account.PlayerId, ex.Message);
                return Answer(PrizeChoiceStatus.Unavailable, 503);
            }
        }

        /// <summary>Claim <c>backId</c> for <c>reign</c>'s open choice.</summary>
        /// <param name="body">The request body, or null when it was over <see cref="MaxBodyBytes"/>.</param>
        public async Task<PrizeChoiceResult> ChooseAsync(ReadOnlyMemory<byte>? body, string? bearer, CancellationToken ct = default)
        {
            // Before anything else, authentication included: an oversized body was never buffered.
            if (body is null) return Answer(PrizeChoiceStatus.Invalid, 413);
            if (!Configured) return Answer(PrizeChoiceStatus.Unavailable, 503);
            var (account, failure) = await ResolveAsync(bearer, ct).ConfigureAwait(false);
            if (account is null) return failure!;
            if (!_limiter.TryTake(account.PlayerId)) return Answer(PrizeChoiceStatus.RateLimited, 429);
            if (!TryParse(body.Value, out int reign, out int backId)) return Answer(PrizeChoiceStatus.Invalid, 400);

            try
            {
                FavorPrize? prize = await _favor.PrizeAsync(reign, account.PlayerId, ct).ConfigureAwait(false);
                if (prize is null || !prize.ChoiceOpen) return Answer(PrizeChoiceStatus.None, 200);

                IReadOnlyList<int> lacking = await LackingAsync(account, ct).ConfigureAwait(false);
                if (lacking.Count == 0)
                {
                    await SettleInGoldAsync(account, reign, ct).ConfigureAwait(false);
                    return Answer(PrizeChoiceStatus.None, 200);
                }
                // The pool, less what the ledger says they own: a place back, an unknown id and an
                // owned pool back all fail here.
                if (!lacking.Contains(backId)) return Answer(PrizeChoiceStatus.Invalid, 400);

                if (!await _favor.CloseChoiceAsync(reign, account.PlayerId, backId, ct).ConfigureAwait(false))
                    return Answer(PrizeChoiceStatus.None, 200);   // another request closed it first

                // Claimed, and the row records the back: from here a dropped connection must not stop
                // the grant halfway. One that fails anyway answers 503 below and is delivered later.
                if (!await DeliverAsync(_favor, _backs!, account, prize with { BackId = backId, ChoiceOpen = false },
                        CancellationToken.None).ConfigureAwait(false))
                    return Answer(PrizeChoiceStatus.Unavailable, 503);
                return Answer(PrizeChoiceStatus.Granted, 200);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[king] POST /king/prize/choose reign {0} for player_id={1} failed; answering 503: {2}",
                    reign, account.PlayerId, ex.Message);
                return Answer(PrizeChoiceStatus.Unavailable, 503);
            }
        }

        /// <summary>
        /// Grants a closed choice's recorded back on its key, then marks it delivered. Idempotent:
        /// a back already granted on that key is a duplicate the ledger ignores. False, with a log
        /// line, when the grant or the mark fails; the row stays undelivered for the next try.
        /// The sweep calls this too (<see cref="KingPayoutSweep"/>).
        /// </summary>
        public static async Task<bool> DeliverAsync(IFavorStore favor, IPrizeBacks backs, Account account,
            FavorPrize chosen, CancellationToken ct)
        {
            if (chosen.BackId is not int backId) return true;
            try
            {
                await backs.GrantAsync(account, backId, ChoiceEventId(chosen.Reign), ct).ConfigureAwait(false);
                await favor.MarkChoiceDeliveredAsync(chosen.Reign, chosen.PlayerId, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("[king] reign {0}: chosen back {1} for player_id={2} not delivered yet; retried later: {3}",
                    chosen.Reign, backId, chosen.PlayerId, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The pool as this build's catalog knows it. A pool id that is not a row of
        /// <c>CardBackDefinitions.All</c> is left out with a log line, once, at composition: granting
        /// or reading it would throw (<see cref="CardBackOwnership.Item"/>), and every player holding a
        /// choice would get a 503 instead of the backs that do exist.
        /// </summary>
        public static IReadOnlyList<int> CatalogPool(IReadOnlyList<int> poolIds, Func<int, bool> inCatalog)
        {
            var known = new List<int>(poolIds.Count);
            foreach (int id in poolIds)
            {
                if (inCatalog(id)) known.Add(id);
                else Console.Error.WriteLine("[king] pool back {0} is not in CardBackDefinitions.All; left out of the pool.", id);
            }
            return known;
        }

        /// <summary>The key reverse-favor-prize.sql reverses a chosen back by.</summary>
        public static string ChoiceEventId(int reign) => "king-choice:" + reign.ToString(CultureInfo.InvariantCulture);

        private async Task<IReadOnlyList<int>> LackingAsync(Account account, CancellationToken ct)
        {
            var lacking = new List<int>(_poolIds.Count);
            foreach (int id in _poolIds)
                if (!await _backs!.OwnsAsync(account, id, ct).ConfigureAwait(false)) lacking.Add(id);
            return lacking;
        }

        /// <summary>Gold first, keyed as the sweep keys it, then the close: a close that fails
        /// leaves the choice to settle again, and the key pays nothing twice.</summary>
        private async Task SettleInGoldAsync(Account account, int reign, CancellationToken ct)
        {
            await KingPayoutSweep.GrantGoldAsync(_economy!, account, _prizes!.PoolEmptyGold,
                KingPayoutSweep.PoolEmptyEventId(reign), reign, ct).ConfigureAwait(false);
            await _favor.CloseChoiceAsync(reign, account.PlayerId, null, ct).ConfigureAwait(false);
        }

        private async Task<(Account? Account, PrizeChoiceResult? Failure)> ResolveAsync(string? bearer, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bearer)) return (null, Answer(PrizeChoiceStatus.Unauthenticated, 401));

            TokenVerificationResult verified;
            try
            {
                verified = await _verifier!.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same rule as /rating: an unreachable JWKS is an outage, not a rejected token.
                return (null, Answer(PrizeChoiceStatus.Unavailable, 503));
            }
            if (!verified.IsSuccess || verified.Identity is null)
                return (null, Answer(PrizeChoiceStatus.Unauthenticated, 401));

            try
            {
                Account account = await _accounts!.ResolveOrCreateAsync(
                    "supabase", CredentialHash.Of(verified.Identity.Subject), verified.Identity.Email, ct).ConfigureAwait(false);
                return (account, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[king] /king/prize could not resolve the account; answering 503: {0}", ex.Message);
                return (null, Answer(PrizeChoiceStatus.Unavailable, 503));
            }
        }

        /// <summary>A JSON object with integer <c>reign</c> and <c>backId</c>. Anything else is false.</summary>
        private static bool TryParse(ReadOnlyMemory<byte> body, out int reign, out int backId)
        {
            reign = 0;
            backId = 0;
            if (body.IsEmpty) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                return root.ValueKind == JsonValueKind.Object &&
                       root.TryGetProperty("reign", out JsonElement r) && r.ValueKind == JsonValueKind.Number &&
                       r.TryGetInt32(out reign) &&
                       root.TryGetProperty("backId", out JsonElement b) && b.ValueKind == JsonValueKind.Number &&
                       b.TryGetInt32(out backId);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static PrizeChoiceResult Answer(PrizeChoiceStatus status, int http)
            => new PrizeChoiceResult(status, NoChoices, http);
    }
}
