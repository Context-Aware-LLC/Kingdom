using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
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

        /// <summary>
        /// Unopened packs the player holds, by pack line — the balance of every item
        /// <see cref="PackCatalog.IsPackItem"/> recognises, with zero and negative lines dropped.
        ///
        /// <para><b>Carried for RESUME, not for browsing</b> (spec
        /// <c>first-run-and-pack-opening.md</c>, slice 2). The gift routes on this number being above
        /// zero, which is what makes the five starter packs unstrandable: a player who opens two and
        /// quits still lands on the rest next launch, without needing the menu entry that is waiting
        /// on the main-menu overhaul.</para>
        ///
        /// <para><b>Populated for <see cref="CollectionResultStatus.Unconstrained"/> as well as
        /// <see cref="CollectionResultStatus.Known"/></b>, unlike <see cref="Cards"/>. A superuser has
        /// no card list because there is nothing to enumerate — but their packs are real ledger rows
        /// with a real balance, and Sam's own account being unable to open its starter packs would
        /// make the gift untestable on the one device it is developed on.</para>
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, long>> Packs { get; }

        /// <summary>Null means cosmetic ownership could not be read; an empty map means none owned.</summary>
        public IReadOnlyDictionary<int, long>? CardBacks { get; }

        public WalletBalances Wallet { get; }

        /// <summary>Diagnostic only, and never the raw subject — <see cref="CredentialHash"/>'s output.
        /// It exists so a support conversation can join a client's report to a server log line.</summary>
        public string PlayerId { get; }

        public CollectionResult(CollectionResultStatus status,
                                IReadOnlyList<KeyValuePair<int, int>>? cards = null,
                                WalletBalances wallet = default,
                                string playerId = "",
                                IReadOnlyList<KeyValuePair<string, long>>? packs = null,
                                IReadOnlyDictionary<int, long>? cardBacks = null)
        {
            Status = status;
            CardBacks = cardBacks;
            Cards = cards ?? Array.Empty<KeyValuePair<int, int>>();
            Wallet = wallet;
            PlayerId = playerId ?? "";
            Packs = packs ?? Array.Empty<KeyValuePair<string, long>>();
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

            // Last-seen is stamped HERE because this is the one authenticated route every launch
            // reaches: both the sign-in gate and a restored-session boot sync the collection before
            // the menu shows. GET /session stamps too, but no client calls it, so on its own the
            // column never moved. A failed stamp is logged and costs the player nothing.
            //
            // RETIREMENT TRIGGER: when the client blocks its boot on GET /session
            // (login-handshake.md §2), delete this stamp and restore "called by the handshake and
            // by nothing else" on IAccountStore.MarkSeenAsync — the value goes back to meaning
            // "last login" rather than "last collection sync".
            try
            {
                await accountStore.MarkSeenAsync(account.PlayerId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[collection] last-seen stamp failed for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
            }

            if (onboarding is not null)
            {
                try
                {
                    await onboarding.EnsureGrantedAsync(account, ct).ConfigureAwait(false);
                    // The backfill: an account that emptied its shelf before the flag was derived
                    // is marked on its next launch, because this is the route every launch reaches.
                    account = await onboarding.MarkOnboardedIfCompleteAsync(account, accountStore, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[collection] onboarding grant or flag failed for player_id={0}: {1}",
                        account.PlayerId, ex.Message);
                }
            }

            IReadOnlyDictionary<int, long>? cardBacks = null;
            try
            {
                cardBacks = await CardBackOwnership.ReadAsync(ledger, account, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[collection] card-back ownership unavailable for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
            }

            IOwnedCardCounts? owned = await AccountOwnedCounts.ForAsync(account, ledger, ct)
                .ConfigureAwait(false);

            // ForAsync returns AllCardsOwnedCounts for a superuser and never enumerates it — there is
            // nothing to enumerate. Saying so explicitly is what stops the client caching an empty
            // list for Sam's own account and undecking him.
            IReadOnlyList<KeyValuePair<string, long>> packs =
                await PacksOf(ledger, account, ct).ConfigureAwait(false);

            if (owned is AllCardsOwnedCounts)
                return new CollectionResult(CollectionResultStatus.Unconstrained,
                    wallet: await WalletOf(economy, account, ct).ConfigureAwait(false),
                    playerId: account.PlayerId,
                    packs: packs, cardBacks: cardBacks);

            // Null here means the ledger is configured but ForAsync still declined — today only when
            // the account is null, which cannot happen on this path. Treated as Unavailable rather
            // than as an empty collection, because "unknown" must never be served as "owns nothing".
            if (owned is not OwnedCardCounts counts)
                return new CollectionResult(CollectionResultStatus.Unavailable);

            return new CollectionResult(
                CollectionResultStatus.Known,
                counts.Entries(),
                await WalletOf(economy, account, ct).ConfigureAwait(false),
                account.PlayerId,
                packs, cardBacks);
        }

        /// <summary>
        /// The player's unopened packs, by line. Every ledger balance filtered through
        /// <see cref="PackCatalog"/>, so only real pack lines reach the client and an item shape this
        /// build does not recognise is skipped rather than shown as an unopenable tile.
        ///
        /// <para><b>Balances at or below zero are dropped.</b> A pack the player has opened leaves a
        /// zero row behind — the ledger is append-only, so the reservation debit does not delete the
        /// grant — and a zero-count shelf entry is a tile that cannot be tapped.</para>
        ///
        /// <para><b>A pack read failure costs the packs, not the answer</b>, the same posture
        /// <see cref="WalletOf"/> takes — a shelf empty for one launch is a missed gift, while a throw
        /// is a 500 on the screen the player just opened. <b>Its reach is narrower than it looks,
        /// though, and saying so is the point:</b> for a standard account
        /// <c>LedgerOwnedCounts</c> has already run this same <c>BalancesAsync</c> to build the card
        /// list, so a broken sweep has thrown before this is ever reached. It is a
        /// <see cref="AccountKind.Superuser"/> — who skips the card projection entirely — whose read
        /// this catch actually saves.</para>
        ///
        /// <para><b>That shared call is a second round trip.</b> One ledger sweep answers both
        /// projections and this endpoint makes it twice, because the card half is reached through
        /// <c>AccountOwnedCounts.ForAsync</c>, which owns its own read and is also called from
        /// <c>MatchGate</c>. Threading one balance snapshot through both is a change to that
        /// signature and to its other caller, which is more than this slice should move.</para>
        /// </summary>
        private static async Task<IReadOnlyList<KeyValuePair<string, long>>> PacksOf(
            IEntitlementLedger ledger, Account account, CancellationToken ct)
        {
            try
            {
                IReadOnlyDictionary<string, long> balances =
                    await ledger.BalancesAsync(account.PlayerId, ct).ConfigureAwait(false);

                var packs = new List<KeyValuePair<string, long>>();
                foreach (KeyValuePair<string, long> row in balances)
                {
                    if (row.Value <= 0) continue;
                    if (!PackCatalog.IsPackItem(row.Key)) continue;
                    packs.Add(row);
                }

                // Sorted by id for the same reason OnboardingGrant.CardGrant sorts: a dictionary walk
                // has no promised order, and a shelf that reshuffles between two identical launches
                // reads as a bug to the player looking at it.
                packs.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                return packs;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[collection] pack balance read failed for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
                return Array.Empty<KeyValuePair<string, long>>();
            }
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
