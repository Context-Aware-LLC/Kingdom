using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The single shuffle door for decks (OBL-7, hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md §5.2.1). Every shuffle
    /// of a deck — the initial <see cref="MatchEngine.BeginMatch"/> shuffle, the mulligan partial
    /// reshuffle, and any future shuffle-back effect — MUST go through <see cref="Shuffle"/>, which
    /// re-mints the <see cref="InstanceId"/> of every deck-resident card in post-shuffle deck order.
    /// Invariant: <i>a deck card's id order always equals current deck order and encodes nothing
    /// else</i> — ids minted before a shuffle would otherwise be a stable ordinal into the submitted
    /// decklist (deck-builder-canonical, cost-sorted) and leak card identity through id values and
    /// gaps the moment a card became visible. Cards in visible zones are never re-minted (stable ids
    /// for presentation and audit; ruled in hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-kickoff-review-2026-07-15.md Q1).
    /// </summary>
    public static class DeckOps
    {
        public static void Shuffle(GameState state, PlayerState ps)
        {
            state.Rng.Shuffle(ps.Deck);
            for (int i = 0; i < ps.Deck.Count; i++)
                ps.Deck[i].Id = state.NextInstanceId();
        }
    }
}
