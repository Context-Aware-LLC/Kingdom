using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Data-driven frame/rarity assembly (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2 + hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/card-rendering.md §3.1).
    /// The eight painted frames (4 rarities × spell/minion, weapons reuse the minion art) are
    /// keyed by rarity+type only for sprite selection (<see cref="FrameSprite"/>) — the
    /// 2026-08-09 standardized frame set (art-pipeline/frames/) is pixel-identical geometry
    /// across all 4 rarities for a given type, so there is exactly ONE socket layout per type
    /// (art-pipeline/frames/README.md "Shared geometry"), measured directly off
    /// frame_common_minion.png/art-opening-mask.png/shared-text-panel.png rather than per-rarity.
    /// <see cref="GemColor"/>/<see cref="FrameTint"/> remain only as the no-sprite fallback. No
    /// per-card, per-rarity branching for placement: everything below is a flat constant.
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

        // Frame sprite key. Every rarity now has a real painted frame for both types (2026-08-09
        // standardized set, art-pipeline/frames/) — weapons draw the minion frame (Sam ruled
        // weapons get no dedicated art; the minion stat sockets already cover attack+durability).
        // Resolves through IUiSpriteProvider.
        public static string FrameSprite(CardType type, Rarity rarity) =>
            type == CardType.SPELL ? $"frame_{Key(rarity)}_spell" : $"frame_{Key(rarity)}";

        private static string Key(Rarity rarity) => rarity.ToString().ToLowerInvariant();

        // ── Unified socket geometry (2026-08-09, art-pipeline/frames/README.md "Shared geometry") ──
        // Fractions of the 1456×2048 master frame, measured from the top-left; identical across
        // all 4 rarities and, aside from the two stat sockets below, across spell and minion too.
        // Measured off the real assets: ArtWindow from art-opening-mask.png's opaque bbox, CostSocket
        // from the gold ring in frame_common_minion.png, TextPanel from shared-text-panel.png's bbox,
        // stat sockets from the README's XCF placement (lower-left/right, 250×250 px each).
        public static readonly Vector4 ArtWindow = new(0.0755f, 0.0659f, 0.9265f, 0.5498f); // l, t, r, b

        public static readonly Vector2 CostSocket = new(0.100f, 0.076f); // nudged down a couple px (Sam, 2026-08-09 live check)
        public const float CostDigitEm = 200f / 2048f;

        // Minion/weapon-only — spells never show these (ShowAttack/ShowHealth/ShowDurability below).
        public static readonly Vector2 StatSocketLeft  = new(0.113f, 0.884f); // attack
        public static readonly Vector2 StatSocketRight = new(0.883f, 0.881f); // health / durability
        public const float StatDigitEm = 220f / 2048f;

        // §3.1 type spec. Title now sits in the frame's own title band, between the art window
        // and the rules parchment — Sam moved it there 2026-08-09 specifically to free up room,
        // so it no longer shares a row with the cost socket and needs no left clamp.
        public const float NameCenterX   = 0.5f;    // of card width
        public const float NameCenterY   = 0.607f;  // of card height, from the top (title-band center)
        public const float NameEm        = 107f / 2048f;
        public const float BodyCenterY   = 0.790f;  // rules-parchment center
        public const float BodyWidth     = 0.85f;   // of card width
        public const float BodyHeight    = 0.23f;   // of card height
        public const float BodyEm        = 100f / 2048f;
        public const float BodyEmFloor   = 72f / 2048f; // auto-shrink floor for long rules text
        public const int   KeywordLeadPct = 115;    // keyword lead, % of body (~124px — held steady as body grew)

        public static readonly Color NameParchment = new Color32(245, 238, 225, 255);
        public static readonly Color BodyInk       = new Color32(28, 18, 12, 255);

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
