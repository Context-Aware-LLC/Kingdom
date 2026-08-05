using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Separates live carrier behavior from legacy enum slots retained for stable wire/hash ordinals.
    /// BACKDRAFT, REINFORCE, and SILENCE are typed actions; their presentation markers are derived
    /// from those actions and must never enter runtime keyword state.
    /// </summary>
    public static class KeywordSemantics
    {
        public static bool IsActionMarker(Keyword keyword)
            => keyword == Keyword.BACKDRAFT || keyword == Keyword.REINFORCE ||
               keyword == Keyword.SILENCE;

        public static bool IsCarrierBehavior(Keyword keyword)
            => System.Enum.IsDefined(typeof(Keyword), keyword) && !IsActionMarker(keyword);
    }
}
