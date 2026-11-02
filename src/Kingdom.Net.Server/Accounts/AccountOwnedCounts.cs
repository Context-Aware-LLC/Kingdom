using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// The one place an <see cref="Account"/> becomes an <see cref="IOwnedCardCounts"/> for L6.
    /// <c>MatchGate</c> threads the result into <c>DeckSubmission.Validate</c> → <c>QueueEligibility.Check</c>
    /// on the match-join path (NOR-313), so an illegal deck is refused at the handshake and no card the
    /// player does not own reaches match state.
    ///
    /// <para><b>Three distinct answers, and collapsing any two of them is a real bug:</b> null means
    /// ownership is UNKNOWN and nothing may be constrained; an empty projection means a real account
    /// that owns nothing, whose every deck is a draft; and <see cref="AllCardsOwnedCounts"/> means an
    /// identity that is never meant to be constrained at all. Reading "unknown" as "empty" undecks
    /// every legitimate player on a box with no economy configured; reading "empty" as "unknown" makes
    /// ownership unenforceable by omission.</para>
    /// </summary>
    public static class AccountOwnedCounts
    {
        /// <summary>
        /// The submitting account's ownership source for L6.
        ///
        /// <list type="bullet">
        ///   <item><b><see cref="AccountKind.Superuser"/></b> → <see cref="AllCardsOwnedCounts"/>. A bot
        ///     opponent or an internal/QA identity: never constrained, whatever the ledger says, and
        ///     the ledger is not even consulted.</item>
        ///   <item><b>No account</b> → null. Every seat resolved without the account store: a tokenless
        ///     tooling seat (<c>ServerOptions.AllowTokenlessMatches</c>, never on a booted server),
        ///     or a signed-in connect on a dev box with no
        ///     accounts database. Neither is a player on a deployment, and their ownership is
        ///     genuinely unknown, which is not the same as owning nothing. Null here means
        ///     UNCONSTRAINED — this branch hands out the full pool, so do not widen it.</item>
        ///   <item><b>No ledger</b> → null. A dev box running without an economy is a supported
        ///     configuration and must not suddenly start refusing decks.</item>
        ///   <item><b>Otherwise</b> → <see cref="LedgerOwnedCounts"/>. This branch is what makes L6
        ///     real; until it landed this method returned null here and the rule was inert for every
        ///     standard account, invisibly — an unconstrained pool and a complete collection render
        ///     identically.</item>
        /// </list>
        /// </summary>
        public static async Task<IOwnedCardCounts?> ForAsync(Account? account,
                                                             IEntitlementLedger? ledger,
                                                             CancellationToken ct = default)
        {
            if (account is null) return null;
            if (account.Kind == AccountKind.Superuser) return AllCardsOwnedCounts.Instance;
            if (ledger is null) return null;

            return await LedgerOwnedCounts.LoadAsync(ledger, account.PlayerId, ct).ConfigureAwait(false);
        }
    }
}
