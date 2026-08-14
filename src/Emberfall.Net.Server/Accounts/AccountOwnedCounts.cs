using Emberfall.Core.Collection;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The one place an <see cref="Account"/> becomes an <see cref="IOwnedCardCounts"/> for L6.
    /// <c>MatchGate</c> threads the result into <c>DeckSubmission.Validate</c> → <c>QueueEligibility.Check</c>
    /// on the match-join path (NOR-313), so a <see cref="AccountKind.Superuser"/> account (a bot opponent,
    /// an internal/QA identity) stays unconstrained by construction — and a standard account keeps today's
    /// null "do not constrain" until a real ledger-backed source is threaded in behind it.
    /// </summary>
    public static class AccountOwnedCounts
    {
        /// <summary>
        /// <see cref="AllCardsOwnedCounts.Instance"/> for a superuser account; null otherwise — the same
        /// "unknown, do not constrain" null a standard account gets today (and a null <paramref name="account"/>,
        /// which is every seat resolved without the account store: guest, or authenticated with none
        /// configured), until a real ledger-backed ownership source is threaded in for it.
        /// </summary>
        public static IOwnedCardCounts? For(Account? account)
            => account?.Kind == AccountKind.Superuser ? AllCardsOwnedCounts.Instance : null;
    }
}
