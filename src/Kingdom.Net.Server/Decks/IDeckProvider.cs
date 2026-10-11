using Kingdom.Core;

namespace Kingdom.Net.Server.Decks
{
    /// <summary>
    /// The match's FALLBACK decks — the fixed pair a seat is dealt when it submits none
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5-bis, RC F1).
    ///
    /// <para>Client-supplied decks (EF-071) do NOT flow through this seam: a per-match <c>NextMatch()</c>
    /// cannot carry two independent per-seat submissions. They are revalidated server-side at the
    /// handshake (<c>DeckSubmission.Validate</c> → <c>QueueEligibility.Check</c>; state-sync §6.4, VULN-001
    /// "Client-supplied deck lists") and composed into the <c>MatchSetup</c> in
    /// <c>MatchGate.PairAsync</c>, which decomposes THIS provider's pair per seat for any seat that did
    /// not submit (<c>SeatDeck.FromFallback</c>). So this seam is now the fallback source, not the only
    /// source.</para>
    /// </summary>
    public interface IDeckProvider
    {
        /// <summary>The full setup for one match (both decklists + hero classes, pre-shuffle order).</summary>
        MatchSetup NextMatch();
    }

    /// <summary>B1: the fallback deals the two fixed slice decks (Kingdom.Core.PrebuiltDecks).</summary>
    public sealed class PrebuiltDeckProvider : IDeckProvider
    {
        public MatchSetup NextMatch() => PrebuiltDecks.EmberwardVsTidecaller();
    }
}
