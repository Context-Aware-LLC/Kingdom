using Emberfall.Core;

namespace Emberfall.Net.Server.Decks
{
    /// <summary>
    /// Where a match's decks come from (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5-bis, RC F1). B2 replaces the
    /// prebuilt impl with client-supplied decks RE-VALIDATED server-side against the card DB and
    /// deck rules at handshake (state-sync §6.4 — never trust the client's list); that validation
    /// lands behind this seam.
    /// </summary>
    public interface IDeckProvider
    {
        /// <summary>The full setup for one match (both decklists + hero classes, pre-shuffle order).</summary>
        MatchSetup NextMatch();
    }

    /// <summary>B1: every match deals the two fixed slice decks (Emberfall.Core.PrebuiltDecks).</summary>
    public sealed class PrebuiltDeckProvider : IDeckProvider
    {
        public MatchSetup NextMatch() => PrebuiltDecks.EmberwardVsTidecaller();
    }
}
