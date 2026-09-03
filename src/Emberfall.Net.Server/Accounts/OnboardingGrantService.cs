using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What one call to <see cref="OnboardingGrantService.EnsureGrantedAsync"/> did.</summary>
    public enum OnboardingOutcome
    {
        /// <summary>This call wrote the grant.</summary>
        Granted,

        /// <summary>The account already carried it. Nothing was written.</summary>
        AlreadyGranted,

        /// <summary>A guest (device-token) account — see the service's remarks. Nothing was written.</summary>
        SkippedGuest,
    }

    /// <summary>
    /// Binds the pure <see cref="OnboardingGrant"/> to the append-only entitlement ledger — the write
    /// path that makes an unowned card a real thing (spec:
    /// <c>docs/dev-docs/specs/card-ownership.md</c>, slice 1).
    ///
    /// <para><b>Once per account, keyed on the transaction id.</b> The whole grant is a single
    /// <see cref="EntitlementBatch"/> under <c>signup:{playerId}</c>, whose rows derive as
    /// <c>signup:{playerId}:{index}</c>. A replay conflicts on those keys, writes nothing and comes
    /// back <see cref="EntitlementBatchOutcome.Duplicate"/> — so this is safe to call on <i>every</i>
    /// account resolve, and there is deliberately no "is it due?" query for two resolves to race
    /// against. It is also all or nothing: <see cref="IEntitlementLedger.ApplyBatchAsync"/> may not
    /// expose a partially applied batch, so a crash mid-grant leaves the account un-granted and the
    /// next resolve simply lands it. That is the one ordering with no half-granted state, which is
    /// also why <see cref="OnboardingGrant.Resolve"/> hands back everything at once.</para>
    ///
    /// <para><b>This supersedes the file-absence key.</b> <see cref="OnboardingGrant"/> was written
    /// against a client-local <c>owned-cards.json</c> whose absence meant "never granted". With the
    /// ledger as the authority that key moves here. The distinction it was drawing survives and is
    /// now stronger: <i>granted then spent</i> is still never re-granted, for the better reason that
    /// the ledger keeps the original grant rows forever — so a balance falling to zero cannot be
    /// mistaken for never having received it, and no convention about empty-versus-absent files has
    /// to be maintained by every future writer.</para>
    ///
    /// <para><b>Guests are skipped, not rejected.</b>
    /// <see cref="EconomyAccountPolicy.RequireDurable"/> throws for a <c>device_token</c> credential:
    /// inventory may not move before account linking. An unauthenticated player connecting is an
    /// ordinary, supported case rather than an error, so this returns
    /// <see cref="OnboardingOutcome.SkippedGuest"/> and leaves their ownership unknown — which is
    /// exactly the "do not constrain" state they already have. RETIREMENT TRIGGER: if account linking
    /// ever lets a guest's collection be carried forward, this skip becomes a grant-then-link and
    /// this paragraph is the thing to revisit.</para>
    ///
    /// <para><b>Storeless and clockless, like the grant it serves.</b> It resolves the plan from the
    /// card database and writes ledger rows; it opens no packs. The starter packs land as an
    /// entitlement BALANCE that <see cref="BoosterPackGrantService"/> spends later, so the client
    /// gets the reveal moment it was designed for instead of forty cards appearing silently.</para>
    /// </summary>
    public sealed class OnboardingGrantService
    {
        /// <summary>
        /// Transaction-id prefix — the once-per-account idempotency anchor. The full id is
        /// <c>signup:{playerId}</c>, which <see cref="EntitlementBatch"/> expands per row.
        /// </summary>
        public const string TxnPrefix = "signup:";

        /// <summary>Ledger <c>source</c> for every row this service writes.</summary>
        public const string GrantSource = "signup";

        /// <summary>
        /// The credential kind that identifies a guest. <b>The same constant</b>
        /// <see cref="EconomyAccountPolicy.RequireDurable"/> throws on, rather than a second copy of
        /// the string — the two have to agree, or this service would hand an account to a policy that
        /// rejects it, and two spellings is how that stops being true.
        /// </summary>
        private const string GuestCredentialKind = EconomyAccountPolicy.GuestCredentialKind;

        private readonly IEntitlementLedger _ledger;
        private readonly ICardDatabase _cards;
        private readonly ITimeSource _time;

        public OnboardingGrantService(IEntitlementLedger ledger, ICardDatabase cards, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _cards = cards ?? throw new ArgumentNullException(nameof(cards));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>
        /// Grant this account its onboarding collection if it has never had one. Safe to call on every
        /// account resolve — see the type remarks on why there is no due-check.
        /// </summary>
        public async Task<OnboardingOutcome> EnsureGrantedAsync(Account account,
                                                                CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (IsGuest(account)) return OnboardingOutcome.SkippedGuest;
            EconomyAccountPolicy.RequireDurable(account);

            OnboardingGrantPlan plan = OnboardingGrant.Resolve(_cards);
            var deltas = new List<EntitlementDelta>(plan.GrantedCardIds.Count + 1);

            IReadOnlyList<int> granted = plan.GrantedCardIds;
            for (int i = 0; i < granted.Count; i++)
                deltas.Add(new EntitlementDelta(
                    BoosterPackGrantService.CardItemPrefix + granted[i], 1, GrantSource));

            // ONE delta for the packs, not one per pack: an unopened pack is a fungible balance the
            // opener debits one at a time, so five rows of +1 and one row of +5 are the same
            // entitlement — and the single row cannot be half-written.
            if (plan.Packs.Count > 0)
                deltas.Add(new EntitlementDelta(plan.Packs[0].Id, plan.Packs.Count, GrantSource));

            if (deltas.Count == 0)
                throw new InvalidOperationException(
                    "The onboarding grant resolved to nothing at all. The card database is empty or " +
                    "holds no collectible commons in the launch pairing — granting an account nothing " +
                    "would burn its once-only signup transaction id on an empty collection.");

            var batch = new EntitlementBatch(
                account.PlayerId,
                TxnPrefix + account.PlayerId,
                deltas,
                requirement: null,
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));

            // The same per-player gate the pack opener takes, for the same reason: two resolves of one
            // account must not both be mid-grant. The batch's idempotency would make a race harmless
            // anyway; this keeps it from being a race at all.
            await using IAsyncDisposable gate =
                await _ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct).ConfigureAwait(false);

            EntitlementBatchResult result = await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (result.Applied) return OnboardingOutcome.Granted;
            if (result.Duplicate) return OnboardingOutcome.AlreadyGranted;

            // Conflict means this account's signup transaction id is bound to a DIFFERENT set of
            // deltas than the grant now resolves to — the grant's CONTENTS changed after this player
            // was onboarded (a card added to the launch set, most likely). That is a content decision
            // arriving at an existing player, not a transient fault to retry: the account keeps what
            // it was actually given, and re-granting under the same id would either double a
            // collection or silently do nothing depending on the adapter. Fail loudly instead.
            throw new InvalidOperationException(
                $"The onboarding grant for player {account.PlayerId} is already bound to a different " +
                "set of entitlements than the current grant resolves to. The grant's contents changed " +
                "after this account was onboarded; it must not be re-granted under the same " +
                "transaction id. Granting the difference is a migration, not a signup.");
        }

        private static bool IsGuest(Account account)
            => string.Equals(account.CredentialKind, GuestCredentialKind, StringComparison.OrdinalIgnoreCase);
    }
}
