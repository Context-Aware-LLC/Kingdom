using Emberfall.Core.Collection;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The one place an <see cref="Account"/> becomes an <see cref="IOwnedCardCounts"/> for L6. Today
    /// <c>DeckSubmission.Validate</c> calls <c>QueueEligibility.Check</c> with no ownership source at
    /// all (NOR-313), so this seam is not yet wired into the match-join path — it exists so a
    /// <see cref="AccountKind.Superuser"/> account (a bot opponent, an internal/QA identity) stays
    /// unconstrained by construction whenever a real ownership source DOES get wired in, rather than
    /// relying on that wiring never happening.
    /// </summary>
    public static class AccountOwnedCounts
    {
        /// <summary>
        /// <see cref="AllCardsOwnedCounts.Instance"/> for a superuser account; null otherwise — the
        /// same "unknown, do not constrain" null a standard account gets today, until a real
        /// ledger-backed ownership source is threaded in for it.
        /// </summary>
        public static IOwnedCardCounts? For(Account account)
            => account?.Kind == AccountKind.Superuser ? AllCardsOwnedCounts.Instance : null;
    }
}
