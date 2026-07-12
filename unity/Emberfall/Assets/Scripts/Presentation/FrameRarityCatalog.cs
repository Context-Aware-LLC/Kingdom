using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Data-driven frame/rarity assembly (techart/frame-rarity-system.md, dependency D2). Placeholder
    /// colors stand in for the real `frame_{type}_{rarity}` / `gem_{rarity}` atlas sprites — swap
    /// <see cref="GemColor"/>/<see cref="FrameTint"/> for sprite lookups once card art lands. No
    /// per-card branching: every card resolves through <see cref="ShowAttack"/>/<see cref="ShowHealth"/>/
    /// <see cref="ShowDurability"/>/<see cref="HasFoil"/>/<see cref="GlowTier"/> keyed only by type+rarity.
    /// </summary>
    public static class FrameRarityCatalog
    {
        public enum GlowTierKind { None, Pulse, Legendary }

        // §2 rarity gem colors.
        public static Color GemColor(Rarity rarity) => rarity switch
        {
            Rarity.COMMON    => new Color(0.55f, 0.55f, 0.58f), // slate
            Rarity.RARE      => new Color(0.25f, 0.55f, 0.95f), // blue
            Rarity.EPIC      => new Color(0.62f, 0.25f, 0.85f), // purple
            Rarity.LEGENDARY => new Color(0.95f, 0.75f, 0.15f), // gold/orange
            _                => Color.gray,
        };

        // Type silhouette proxy (real frame art is a per-type sprite; this is the placeholder tint).
        public static Color FrameTint(CardType type) => type switch
        {
            CardType.MINION => new Color(0.30f, 0.30f, 0.34f), // rounded shield/portrait
            CardType.SPELL  => new Color(0.24f, 0.20f, 0.36f), // scroll/rune
            CardType.WEAPON => new Color(0.34f, 0.26f, 0.18f), // forged metal
            _               => new Color(0.22f, 0.22f, 0.32f),
        };

        // §1/§3 atlas sprite keys — the templated data-driven swap. Names match the UI atlas
        // (frame-rarity-system.md §1/§2/§4) and resolve through IUiSpriteProvider. No per-card branching.
        public static string FrameSprite(CardType type, Rarity rarity) =>
            $"frame_{type.ToString().ToLowerInvariant()}_{rarity.ToString().ToLowerInvariant()}";

        public static string GemSprite(Rarity rarity) =>
            $"gem_{rarity.ToString().ToLowerInvariant()}";

        // §4: SILENCE is an effect, not a card-face keyword — it has no icon.
        public static string KeywordIcon(Keyword keyword) =>
            keyword == Keyword.SILENCE ? null : $"icon_kw_{keyword.ToString().ToLowerInvariant()}";

        public static bool HasFoil(Rarity rarity) => rarity == Rarity.EPIC || rarity == Rarity.LEGENDARY;

        public static GlowTierKind GlowTier(Rarity rarity) => rarity switch
        {
            Rarity.EPIC      => GlowTierKind.Pulse,
            Rarity.LEGENDARY => GlowTierKind.Legendary,
            _                => GlowTierKind.None,
        };

        // §1 type silhouette / stat-plate visibility — spells show cost only, weapons show durability
        // in place of health.
        public static bool ShowAttack(CardType type)     => type == CardType.MINION || type == CardType.WEAPON;
        public static bool ShowHealth(CardType type)      => type == CardType.MINION;
        public static bool ShowDurability(CardType type)  => type == CardType.WEAPON;
    }
}
