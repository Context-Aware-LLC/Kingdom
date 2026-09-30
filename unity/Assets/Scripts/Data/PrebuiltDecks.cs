using Emberfall.Core;

namespace Emberfall.Data
{
    /// <summary>
    /// Unity-side alias for the slice decklists. The lists themselves moved to
    /// <see cref="Emberfall.Core.PrebuiltDecks"/> so the headless server can construct matches
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5-bis, RC F1) — this shim keeps existing Unity call sites
    /// stable and guarantees client and server deal from the same source.
    /// </summary>
    public static class PrebuiltDecks
    {
        public static MatchSetup EmberwardVsTidecaller()
            => Emberfall.Core.PrebuiltDecks.EmberwardVsTidecaller();
    }
}
