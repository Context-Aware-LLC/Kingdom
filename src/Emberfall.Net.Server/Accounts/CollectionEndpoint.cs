using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// What <c>GET /collection</c> answers with. Three shapes, and the client's whole offline story
    /// depends on it being able to tell them apart.
    /// </summary>
    public enum CollectionResultStatus
    {
        /// <summary>The ledger was read. <see cref="CollectionResult.Cards"/> is this player's
        /// collection — <b>possibly empty, which is a real answer</b> meaning they own nothing.</summary>
        Known,

        /// <summary>This identity is never meant to be constrained (a <see cref="AccountKind.Superuser"/>
        /// — a bot, or Sam's own account). Not "owns everything as a list": there is no list, and the
        /// client must not cache one. See <see cref="AllCardsOwnedCounts"/>.</summary>
        Unconstrained,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store or no ledger — a supported dev configuration.
        /// Ownership is genuinely UNKNOWN, which is not the same as owning nothing, so the client must
        /// leave whatever it already had alone. 503.</summary>
        Unavailable,
    }

    /// <summary>The endpoint's answer, before it becomes JSON.</summary>
    public sealed class CollectionResult
    {
        public CollectionResultStatus Status { get; }

        /// <summary>Empty unless <see cref="Status"/> is <see cref="CollectionResultStatus.Known"/>.</summary>
        public IReadOnlyList<KeyValuePair<int, int>> Cards { get; }

        public WalletBalances Wallet { get; }

        /// <summary>Diagnostic only, and never the raw subject — <see cref="CredentialHash"/>'s output.
        /// It exists so a support conversation can join a client's report to a server log line.</summary>
        public string PlayerId { get; }

        public CollectionResult(CollectionResultStatus status,
                                IReadOnlyList<KeyValuePair<int, int>>? cards = null,
                                WalletBalances wallet = default,
                                string playerId = "")
        {
            Status = status;
            Cards = cards ?? Array.Empty<KeyValuePair<int, int>>();
            Wallet = wallet;
            PlayerId = playerId ?? "";
        }

        /// <summary>The HTTP status this maps to, kept beside the enum so a new member cannot be added
        /// without deciding one.</summary>
        public int HttpStatus => Status switch
        {
            CollectionResultStatus.Known => 200,
            CollectionResultStatus.Unconstrained => 200,
            CollectionResultStatus.Unauthenticated => 401,
            CollectionResultStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>GET /collection</c> — the read-down that makes a reinstalled player's collection come back.
    ///
    /// <para><b>Why this exists at all.</b> The entitlement ledger has been the authority on what a
    /// player owns since 2026-09-01, keyed on the authenticated <c>player_id</c>, and the client could
    /// not read a byte of it: <c>CollectionController</c> and <c>CharacterRosterController</c> both go
    /// to <c>JsonOwnedCardStore</c> on local disk. So a player who uninstalled, reinstalled and signed
    /// back into the right identity landed on an empty local scope — which renders as <i>owns
    /// everything</i>, because a missing record means "unknown, do not constrain". The account was
    /// never lost; there was simply no wire from it to the screen. This is that wire.
    /// Spec: <c>docs/dev-docs/specs/card-ownership.md</c>, slice 3.</para>
    ///
    /// <para><b>Bearer-authed with the same pair the match handshake uses</b> —
    /// <see cref="IIdentityTokenVerifier"/> then <see cref="IAccountStore"/> — so this route and
    /// <c>MatchGate.HandleFreshAsync</c> cannot disagree about who somebody is. The subject is hashed
    /// through the one <see cref="CredentialHash"/> both call; two spellings of that hash would
    /// silently resolve the same human to two accounts.</para>
    ///
    /// <para><b>It fires the onboarding grant, and that is load-bearing rather than tidy.</b> The
    /// grant fires when an account RESOLVES, and until now the only place one resolved was a match
    /// join. A reinstalled player signs in and opens their collection before ever queueing, so
    /// without this the ledger would be legitimately empty at exactly the moment the client asks —
    /// and an empty <c>Known</c> answer is not harmless: the client caches it as "owns nothing" and
    /// turns every deck into a draft. Idempotent on <c>signup:{playerId}</c>, so calling it here and
    /// at the handshake grants exactly once.</para>
    ///
    /// <para><b>A grant failure must not fail the read.</b> Same posture as the handshake: it is
    /// logged and the collection is served from whatever the ledger does hold. The alternative turns
    /// a database blip into an empty collection screen.</para>
    /// </summary>
    public static class CollectionEndpoint
    {
        /// <summary>
        /// Resolve the bearer token to an account and project its collection.
        ///
        /// <para>Takes its collaborators rather than reading configuration, so the whole route is
        /// exercised by the dotnet suite without Kestrel — the HTTP mapping in <c>ServerApp</c> is
        /// then only a status code and a JSON writer.</para>
        /// </summary>
        public static async Task<CollectionResult> ReadAsync(
            string? bearerToken,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IEntitlementLedger? ledger,
            EconomyService? economy,
            OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new CollectionResult(CollectionResultStatus.Unauthenticated);

            // No verifier is NOT "let them in" — an unauthenticated read of somebody's collection is
            // the whole thing this endpoint must never do. A box without one simply has no /collection.
            if (verifier is null || accountStore is null || ledger is null)
                return new CollectionResult(CollectionResultStatus.Unavailable);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null)
                return new CollectionResult(CollectionResultStatus.Unauthenticated);

            string subHash = CredentialHash.Of(verified.Identity.Subject);
            Account account = await accountStore
                .ResolveOrCreateAsync("supabase", subHash, verified.Identity.Email, ct)
                .ConfigureAwait(false);

            if (onboarding is not null)
            {
                try
                {
                    await onboarding.EnsureGrantedAsync(account, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[collection] onboarding grant failed for player_id={0}: {1}",
                        account.PlayerId, ex.Message);
                }
            }

            IOwnedCardCounts? owned = await AccountOwnedCounts.ForAsync(account, ledger, ct)
                .ConfigureAwait(false);

            // ForAsync returns AllCardsOwnedCounts for a superuser and never enumerates it — there is
            // nothing to enumerate. Saying so explicitly is what stops the client caching an empty
            // list for Sam's own account and undecking him.
            if (owned is AllCardsOwnedCounts)
                return new CollectionResult(CollectionResultStatus.Unconstrained,
                    wallet: await WalletOf(economy, account, ct).ConfigureAwait(false),
                    playerId: account.PlayerId);

            // Null here means the ledger is configured but ForAsync still declined — today only when
            // the account is null, which cannot happen on this path. Treated as Unavailable rather
            // than as an empty collection, because "unknown" must never be served as "owns nothing".
            if (owned is not OwnedCardCounts counts)
                return new CollectionResult(CollectionResultStatus.Unavailable);

            return new CollectionResult(
                CollectionResultStatus.Known,
                counts.Entries(),
                await WalletOf(economy, account, ct).ConfigureAwait(false),
                account.PlayerId);
        }

        /// <summary>The wallet, or zeroes on a box with no economy service. A missing wallet must not
        /// cost the player their card list — the two are independent reads of the same ledger and the
        /// collection is the one this endpoint exists for.</summary>
        private static async Task<WalletBalances> WalletOf(
            EconomyService? economy, Account account, CancellationToken ct)
        {
            if (economy is null) return default;
            try
            {
                return await economy.GetWalletAsync(account, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[collection] wallet read failed for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
                return default;
            }
        }
    }
}
