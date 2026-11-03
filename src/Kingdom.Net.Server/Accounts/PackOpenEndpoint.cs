using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>What <c>POST /packs/open</c> answers with.</summary>
    public enum PackOpenEndpointStatus
    {
        /// <summary>This call opened a pack. <see cref="PackOpenEndpointResult.Awards"/> is what the
        /// player got. 200.</summary>
        Opened,

        /// <summary>This exact open had already landed — a retried tap, or a duplicated request. The
        /// awards are the SAME ones, read back out of the ledger, and nothing was spent a second
        /// time. 200, because the client's job is identical either way: show these cards.</summary>
        AlreadyOpened,

        /// <summary>The <c>packId</c> is not a pack line this build knows. 400.</summary>
        UnknownPack,

        /// <summary>The requested batch count is outside the server's bounded range. 400.</summary>
        InvalidCount,

        /// <summary>The player holds no unopened pack on this line. 409.</summary>
        NoneOwned,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store, no ledger, or no pack line configured — a supported
        /// dev configuration, not something the player can act on. 503.</summary>
        Unavailable,

        /// <summary>The ledger refused the open for a reason that is not a missing pack: the derived
        /// instance id is bound to a different reservation. 409, and a server log line.
        /// This is a bug rather than a player state, and it is given its own word so that it cannot
        /// be read as "you have no packs".</summary>
        Conflict,
    }

    /// <summary>One open, before it becomes JSON.</summary>
    public sealed class PackOpenEndpointResult
    {
        public PackOpenEndpointStatus Status { get; }
        public string PackId { get; }

        /// <summary>The SERVER-MINTED instance id this open was anchored on. On the wire for support
        /// and log-joining only — see the endpoint remarks on why the client may never choose it.</summary>
        public string PackInstanceId { get; }

        /// <summary>The revealed slots, in order. Empty on every non-200 status.</summary>
        public IReadOnlyList<PackSlotAward> Awards { get; }

        /// <summary>Diagnostic text for a log line. Never shown to a player.</summary>
        public string Detail { get; }

        public PackOpenEndpointResult(PackOpenEndpointStatus status,
                                      string packId = "",
                                      string packInstanceId = "",
                                      IReadOnlyList<PackSlotAward>? awards = null,
                                      string detail = "")
        {
            Status = status;
            PackId = packId ?? "";
            PackInstanceId = packInstanceId ?? "";
            Awards = awards ?? Array.Empty<PackSlotAward>();
            Detail = detail ?? "";
        }

        public int HttpStatus => Status switch
        {
            PackOpenEndpointStatus.Opened => 200,
            PackOpenEndpointStatus.AlreadyOpened => 200,
            PackOpenEndpointStatus.UnknownPack => 400,
            PackOpenEndpointStatus.InvalidCount => 400,
            PackOpenEndpointStatus.NoneOwned => 409,
            PackOpenEndpointStatus.Conflict => 409,
            PackOpenEndpointStatus.Unauthenticated => 401,
            PackOpenEndpointStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>Several ordered opens produced by one authenticated, locked server operation.</summary>
    public sealed class PackOpenBatchEndpointResult
    {
        public PackOpenEndpointStatus Status { get; }
        public string PackId { get; }
        public IReadOnlyList<PackOpenEndpointResult> Opens { get; }
        public string Detail { get; }

        public PackOpenBatchEndpointResult(
            PackOpenEndpointStatus status,
            string packId = "",
            IReadOnlyList<PackOpenEndpointResult>? opens = null,
            string detail = "")
        {
            Status = status;
            PackId = packId ?? "";
            Opens = opens ?? Array.Empty<PackOpenEndpointResult>();
            Detail = detail ?? "";
        }

        public int HttpStatus => Status switch
        {
            PackOpenEndpointStatus.Opened => 200,
            PackOpenEndpointStatus.AlreadyOpened => 200,
            PackOpenEndpointStatus.UnknownPack => 400,
            PackOpenEndpointStatus.InvalidCount => 400,
            PackOpenEndpointStatus.NoneOwned => 409,
            PackOpenEndpointStatus.Conflict => 409,
            PackOpenEndpointStatus.Unauthenticated => 401,
            PackOpenEndpointStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>POST /packs/open</c> — the verb the entitlement ledger has been missing. Spec:
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, slice 0.
    ///
    /// <para><b>Why it had to exist.</b> <see cref="BoosterPackGrantService"/> has been complete,
    /// transactional and exactly-once since NOR-109, and <c>ServerApp</c> mapped no route to it — so
    /// the five starter packs <see cref="OnboardingGrantService"/> grants were a balance with no
    /// reachable verb. Measured on a device 2026-09-02: a new player put every card they owned into
    /// their first deck and reached 28 of 30 (#346). This is the route that spends them.</para>
    ///
    /// <para><b>The body names a pack LINE, never a pack instance.</b> The instance id is the
    /// idempotency anchor for the whole operation and <see cref="BoosterPackGrantService.OpenAsync"/>
    /// says on its own signature that it must be minted server-side: a client that chooses it can
    /// replay one purchase into unbounded opens by varying it, or collide with another player's. An
    /// earlier draft of the spec put it in the request body with a length and charset bound, which
    /// would have satisfied a security review while leaving the hole open. <b>Any
    /// <c>packInstanceId</c> in the body is ignored — there is no parameter for it to bind to</b>,
    /// and a test pins that.</para>
    ///
    /// <para><b>How the id is minted, and the one gap it leaves</b> (Sam, 2026-09-03; the spec left
    /// this open and recommended exactly this). It is
    /// <c>open:{playerId}:{packId}:{n}</c>, where <c>n</c> is the count of completed opens already on
    /// this line — the balance of <see cref="BoosterPackGrantService.OpenedItemPrefix"/> + packId,
    /// which the service writes one of per finished open. Two in-flight taps read the same <c>n</c>,
    /// derive the same id, and the second comes back
    /// <see cref="PackOpenEndpointStatus.AlreadyOpened"/> having spent nothing — the exactly-once
    /// property the service is built around, reached without a reservation round trip.
    /// <b>The gap, stated rather than discovered later:</b> an open that SUCCEEDS but whose response
    /// is lost computes <c>n+1</c> on the retry and opens a second pack. The player loses nothing
    /// they did not own — the cards are all theirs — but they spend a pack they did not mean to. That
    /// is acceptable for a free gift the player is watching happen; RETIREMENT TRIGGER: if single
    /// packs are ever sold for money, revisit this and take the spec's option 2, a short-lived
    /// server-issued reservation token.</para>
    ///
    /// <para><b>The pack SHAPE comes from <see cref="PackCatalog"/>, not from the request.</b> Card
    /// count and odds are the caller's argument to the grant service, so letting the client name them
    /// would let a retail entitlement be spent at the starter's boosted odds.</para>
    ///
    /// <para>Auth is the same <see cref="IIdentityTokenVerifier"/> + <see cref="IAccountStore"/> pair
    /// <c>GET /collection</c> and <c>MatchGate</c> use, through the one
    /// <see cref="CredentialHash"/> — two spellings of that hash resolve one human to two accounts,
    /// and here that would be one human and two collections.</para>
    /// </summary>
    public static class PackOpenEndpoint
    {
        /// <summary>Prefix of the derived instance id. Public so the tests that pin the minting rule
        /// name it rather than repeating the string.</summary>
        public const string InstancePrefix = BoosterPackGrantService.OpenInstancePrefix;

        /// <summary>
        /// Open one pack on the named line for the bearer's account.
        ///
        /// <para>Takes its collaborators rather than reading configuration, so the whole route is
        /// exercised by the dotnet suite without Kestrel — the mapping in <c>ServerApp</c> is then
        /// only a status code and a JSON writer.</para>
        /// </summary>
        public static async Task<PackOpenEndpointResult> OpenAsync(
            string? bearerToken,
            string? packId,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IEntitlementLedger? ledger,
            BoosterPackGrantService? packs,
            OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new PackOpenEndpointResult(PackOpenEndpointStatus.Unauthenticated);

            // No verifier is not "let them in". A box with no pack line configured (no seed secret,
            // so no grant service) has no /packs/open at all rather than a guessable one.
            if (verifier is null || accountStore is null || ledger is null || packs is null)
                return new PackOpenEndpointResult(PackOpenEndpointStatus.Unavailable);

            // Resolved BEFORE the token is verified: an unknown pack line is the client's mistake
            // either way, and answering it does not depend on who is asking.
            if (!PackCatalog.TryResolve(packId, out BoosterPack pack))
                return new PackOpenEndpointResult(PackOpenEndpointStatus.UnknownPack,
                    detail: "unknown pack line");

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null)
                return new PackOpenEndpointResult(PackOpenEndpointStatus.Unauthenticated);

            string subHash = CredentialHash.Of(verified.Identity.Subject);
            Account account = await accountStore
                .ResolveOrCreateAsync("supabase", subHash, verified.Identity.Email, ct)
                .ConfigureAwait(false);

            // The same reason /collection fires it: an account can resolve here first. A player who
            // signs in and is taken straight to the gift has never hit /collection, and without this
            // their ledger is empty at the moment they are handed five packs.
            if (onboarding is not null)
            {
                try
                {
                    await onboarding.EnsureGrantedAsync(account, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[packs] onboarding grant failed for player_id={0}: {1}",
                        account.PlayerId, ex.Message);
                }
            }

            long owned = await ledger.BalanceAsync(account.PlayerId, pack.Id, ct).ConfigureAwait(false);
            if (owned <= 0)
                return new PackOpenEndpointResult(PackOpenEndpointStatus.NoneOwned, pack.Id);

            string instanceId = await MintInstanceIdAsync(ledger, account.PlayerId, pack, ct)
                .ConfigureAwait(false);

            try
            {
                PackOpenOutcome outcome = await packs
                    .OpenAsync(account, pack, instanceId, ct)
                    .ConfigureAwait(false);

                await MarkOnboardedIfStarterAsync(onboarding, accountStore, account, pack, ct)
                    .ConfigureAwait(false);

                return new PackOpenEndpointResult(
                    outcome.Status == PackOpenStatus.Opened
                        ? PackOpenEndpointStatus.Opened
                        : PackOpenEndpointStatus.AlreadyOpened,
                    outcome.PackId,
                    outcome.PackInstanceId,
                    outcome.Awards);
            }
            catch (InvalidOperationException ex)
            {
                // The balance was read outside the service's per-player lock, so it can go stale
                // under a concurrent open. That is the only benign way to arrive here; the other is a
                // reservation bound to different deltas, which is a real defect. Both are logged with
                // the id so the two can be told apart in the ledger afterwards.
                Console.Error.WriteLine(
                    "[packs] open refused for player_id={0} pack={1} instance={2}: {3}",
                    account.PlayerId, pack.Id, instanceId, ex.Message);
                return new PackOpenEndpointResult(PackOpenEndpointStatus.Conflict, pack.Id, instanceId,
                    detail: ex.Message);
            }
        }

        /// <summary>
        /// Open a bounded group of packs for one bearer. Unlike a client loop over
        /// <see cref="OpenAsync"/>, verification, account resolution, onboarding and the player's
        /// mutation lock each happen once.
        /// </summary>
        public static async Task<PackOpenBatchEndpointResult> OpenManyAsync(
            string? bearerToken,
            string? packId,
            int count,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IEntitlementLedger? ledger,
            BoosterPackGrantService? packs,
            OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (count < 1 || count > BoosterPackGrantService.MaxBatchCount)
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.InvalidCount,
                    detail: "batch count must be between 1 and " +
                        BoosterPackGrantService.MaxBatchCount.ToString(CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.Unauthenticated);
            if (verifier is null || accountStore is null || ledger is null || packs is null)
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.Unavailable);
            if (!PackCatalog.TryResolve(packId, out BoosterPack pack))
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.UnknownPack,
                    detail: "unknown pack line");

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null)
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.Unauthenticated);

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
                    Console.Error.WriteLine("[packs] onboarding grant failed for player_id={0}: {1}",
                        account.PlayerId, ex.Message);
                }
            }

            try
            {
                IReadOnlyList<PackOpenOutcome> outcomes = await packs
                    .OpenManyAsync(account, pack, count, ct)
                    .ConfigureAwait(false);
                if (outcomes.Count == 0)
                    return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.NoneOwned, pack.Id);

                await MarkOnboardedIfStarterAsync(onboarding, accountStore, account, pack, ct)
                    .ConfigureAwait(false);

                var opens = new List<PackOpenEndpointResult>(outcomes.Count);
                foreach (PackOpenOutcome outcome in outcomes)
                {
                    opens.Add(new PackOpenEndpointResult(
                        outcome.Status == PackOpenStatus.Opened
                            ? PackOpenEndpointStatus.Opened
                            : PackOpenEndpointStatus.AlreadyOpened,
                        outcome.PackId,
                        outcome.PackInstanceId,
                        outcome.Awards));
                }
                return new PackOpenBatchEndpointResult(
                    PackOpenEndpointStatus.Opened, pack.Id, opens);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(
                    "[packs] batch open refused for player_id={0} pack={1}: {2}",
                    account.PlayerId, pack.Id, ex.Message);
                return new PackOpenBatchEndpointResult(PackOpenEndpointStatus.Conflict, pack.Id,
                    detail: ex.Message);
            }
        }

        /// <summary>
        /// The open that empties the starter shelf is the one that onboards the player — see
        /// <see cref="OnboardingGrantService.MarkOnboardedIfCompleteAsync"/>. A failure is logged and
        /// costs nothing: the cards are already theirs, and the next <c>GET /collection</c> retries.
        /// </summary>
        private static async Task MarkOnboardedIfStarterAsync(
            OnboardingGrantService? onboarding, IAccountStore accountStore, Account account,
            BoosterPack pack, CancellationToken ct)
        {
            if (onboarding is null) return;
            if (!string.Equals(pack.Id, OnboardingGrant.StarterPack().Id, StringComparison.Ordinal)) return;
            try
            {
                await onboarding.MarkOnboardedIfCompleteAsync(account, accountStore, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[packs] onboarded flag failed for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
            }
        }

        /// <summary>
        /// The server-minted idempotency anchor: <c>open:{playerId}:{packId}:{n}</c>.
        ///
        /// <para><c>n</c> is the balance of this line's completion marker, which
        /// <see cref="BoosterPackGrantService"/> writes exactly one of per finished open — so it is
        /// the number of packs of this line the player has ever opened, and it never goes backwards
        /// (the ledger is append-only and nothing negates a marker).</para>
        ///
        /// <para><b>Read outside the service's per-player lock, deliberately.</b> That lock is taken
        /// inside <c>OpenAsync</c> and is not reentrant, so acquiring it here would deadlock. Racing
        /// two reads is the case this design is built on rather than the case it fails: both derive
        /// the same id and the second open is a replay.</para>
        /// </summary>
        private static async Task<string> MintInstanceIdAsync(
            IEntitlementLedger ledger, string playerId, BoosterPack pack, CancellationToken ct)
        {
            long opened = await ledger
                .BalanceAsync(playerId, BoosterPackGrantService.OpenedItemPrefix + pack.Id, ct)
                .ConfigureAwait(false);
            if (opened < 0) opened = 0;

            return InstancePrefix + playerId + ":" + pack.Id + ":" +
                   opened.ToString(CultureInfo.InvariantCulture);
        }
    }
}
