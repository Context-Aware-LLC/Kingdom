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

        // ── Unified socket geometry (2026-08-09, art-pipeline/frames/README.md "Shared geometry";
        //    re-measured 2026-08-16 against Sam's enlarged-aperture frame_common.png) ──
        // Fractions of the 1456×2048 master frame, measured from the top-left. Aside from the two
        // stat sockets below, one layout serves spell and minion alike.
        //
        // Every socket moved on 2026-08-16: Sam widened the art aperture (0.8516 × 0.4844 of the
        // card -> 0.9114 × 0.5254) and the rest of the furniture shifted to make room — the stat
        // bosses most of all, ~100px down into the bottom corners. The numbers below were tracked
        // from the 2026-08-09 ones by template-matching each socket's neighbourhood from the old
        // frame onto the new (scoring only pixels opaque in BOTH, so the changed aperture cannot
        // drag the match), which carries Sam's live hand-tuning forward instead of re-detecting
        // over the top of it. Each match beat the residual floor set by a control run against an
        // unconformed sibling frame, and the disc radii are unchanged (0.0444h -> 0.0454h), which
        // is why the digit em sizes below did not move.
        //
        // All eight frames are conformed as of 2026-08-17 — every socket is inside tolerance, worst
        // in the set 10%. The apertures still differ per frame and that is FINE, because the frame
        // draws on top of the art quad: an aperture SMALLER than ArtWindow is covered, and only one
        // LARGER opens a gap onto the dark body quad behind. So the single rule is that ArtWindow
        // must cover the largest aperture in the set. The bottom edge below is set 2px past epic's
        // 1144, the lowest in the set, which closes the 5-row strip epic showed before.
        // RETIREMENT TRIGGER: if any frame's aperture is ever enlarged past this rect, this edge
        // moves with it in the same commit — see docs/dev-docs/specs/card-frame-geo.md.
        public static readonly Vector4 ArtWindow = new(0.0460f, 0.0308f, 0.9574f, 0.5596f); // l, t, r, b

        public static readonly Vector2 CostSocket = new(0.0911f, 0.0731f); // carries the 2026-08-09 "nudge it down a couple px" (Sam, live check)
        public const float CostDigitEm = 200f / 2048f;

        // Minion/weapon-only — spells never show these (ShowAttack/ShowHealth/ShowDurability below).
        public static readonly Vector2 StatSocketLeft  = new(0.0876f, 0.9343f); // attack
        public static readonly Vector2 StatSocketRight = new(0.9139f, 0.9332f); // health / durability
        public const float StatDigitEm = 220f / 2048f;

        // §3.1 type spec. Title now sits in the frame's own title band, between the art window
        // and the rules parchment — Sam moved it there 2026-08-09 specifically to free up room,
        // so it no longer shares a row with the cost socket and needs no left clamp.
        // The title band is the same 0.1118h tall as before, just pushed down with everything
        // else; the parchment grew a little in both axes (0.8489 × 0.2598 -> 0.8743 × 0.2637), so
        // the body bed follows it and keeps the insets it had (13px top, 17px bottom, flush l/r).
        public const float NameCenterX   = 0.5f;    // of card width
        public const float NameCenterY   = 0.613f;  // of card height, from the top (title-band center)
        public const float NameEm        = 107f / 2048f;
        public const float BodyCenterY   = 0.798f;  // rules-parchment center
        // 0.846 = 1232px, which fits the NARROWEST parchment in the set (epic_spell and
        // legendary_spell both measure 1233 wide). The previous 0.874 was 1273px and overhung
        // those two, and rare, by ~20px per side — rules text ran off the parchment onto the
        // stone rails, which the parchment edge sitting right there makes obvious.
        public const float BodyWidth     = 0.846f;  // of card width
        public const float BodyHeight    = 0.234f;  // of card height
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
