using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Data-driven frame/rarity assembly (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2 + hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/card-rendering.md §3.1).
    ///
    /// <para>ONE frame serves every card (2026-08-19). There is no rarity axis and no type axis:
    /// the three digit sockets and the gem bezel are their own sprites composited on top, so a
    /// spell is not a different frame — it is the same frame that simply does not draw two of the
    /// sockets. That collapsed the eight painted frames (4 rarities × spell/minion) to one, and it
    /// is what lets <see cref="FrameSprite"/> be a constant rather than a lookup.</para>
    ///
    /// <para><b>Rarity now reads entirely from the gem</b> (<see cref="GemSprite"/>) in the
    /// bottom-centre socket. <see cref="GemColor"/>/<see cref="FrameTint"/> remain only as the
    /// no-sprite fallback.</para>
    /// </summary>
    public static class FrameRarityCatalog
    {
        public enum GlowTierKind { None, Pulse, Legendary }

        // The master frame art is authored at this size; every fraction below is measured off it.
        public const float MasterW = 1456f;
        public const float MasterH = 2048f;

        // §2 rarity gem colors.
        public static Color GemColor(Rarity rarity) => rarity switch
        {
            Rarity.COMMON    => new Color(0.55f, 0.55f, 0.58f), // slate
            Rarity.RARE      => new Color(0.25f, 0.55f, 0.95f), // blue
            Rarity.EPIC      => new Color(0.62f, 0.25f, 0.85f), // purple
            Rarity.LEGENDARY => new Color(0.95f, 0.75f, 0.15f), // gold/orange
            _                => Color.gray,
        };

        // Type silhouette proxy (real frame art is a sprite; this is the placeholder tint).
        public static Color FrameTint(CardType type) => type switch
        {
            CardType.MINION => new Color(0.30f, 0.30f, 0.34f), // rounded shield/portrait
            CardType.SPELL  => new Color(0.24f, 0.20f, 0.36f), // scroll/rune
            CardType.WEAPON => new Color(0.34f, 0.26f, 0.18f), // forged metal
            _               => new Color(0.22f, 0.22f, 0.32f),
        };

        // ── The four composited sprites ──────────────────────────────────────────────────────
        // All resolve through IUiSpriteProvider, which searches Assets/Art/UI by sprite NAME, so
        // the Frames/ subfolder they live in is organisational only.
        //
        // The frame carries NO socket art: it is plain rail wherever a socket lands, and each
        // socket sprite brings its own housing. That is the whole reason one frame can serve
        // minion, spell and weapon alike — the type only decides which sockets get drawn.
        // RETIREMENT TRIGGER: if a socket's housing is ever painted back INTO the frame, this
        // claim and the keep-clear zones in docs/dev-docs/specs/card-frame-geo.md both die.
        public const string FrameSprite      = "frame_card";
        public const string StatSocketSprite = "socket_stat"; // cost, attack, health/durability — one sprite, drawn three times
        public const string GemSocketSprite  = "socket_gem";  // the rope bezel the rarity gem sits in

        // Socket sprite canvas sizes, in master pixels. The disc is centred in its canvas with a
        // transparent margin, so the sprite is drawn at CANVAS span and lands concentric on the
        // socket centre — do not size these to the disc.
        public const float StatSocketPx = 256f; // O246 disc, O210 flat interior, ~18px rope band
        public const float GemSocketPx  = 224f; // O206 bezel, O170 well

        public static string GemSprite(Rarity rarity) =>
            $"gem_{rarity.ToString().ToLowerInvariant()}";

        // ── Unified geometry (2026-08-19) ────────────────────────────────────────────────────
        // Fractions of the 1456×2048 master, measured from the top-left off the shipped PNGs.
        // One layout, no branching. The pixel figures are in the comment beside each value and
        // the full derivation is docs/dev-docs/specs/card-frame-geo.md.

        // The aperture is cut to transparency and the frame draws ON TOP of the art quad, so the
        // art may overflow — the border occludes it. The frame's aperture is now a plain rounded
        // rect: the arc that used to bite the top-left corner was the cost boss's own footprint,
        // and the boss is a separate sprite drawn over the art now.
        // RETIREMENT TRIGGER: re-cutting the aperture moves this rect in the same commit.
        public static readonly Vector4 ArtWindow = new(0.045330f, 0.031250f, 0.953709f, 0.556152f); // 66, 64, 1389, 1139

        public static readonly Vector2 CostSocket = new(0.091346f, 0.073242f); // 133, 150
        public const float CostDigitEm = 200f / MasterH;

        // Minion/weapon-only — spells never show these (ShowAttack/ShowHealth/ShowDurability below),
        // and on a spell the two sockets are simply not drawn, leaving clean rail behind.
        public static readonly Vector2 StatSocketLeft  = new(0.087912f, 0.934082f); // 128, 1913  attack
        public static readonly Vector2 StatSocketRight = new(0.911401f, 0.933105f); // 1327, 1911 health / durability
        public const float StatDigitEm = 220f / MasterH;

        // Title band, between the art aperture and the rules parchment: y 1139..1369, 230 tall.
        public const float NameCenterX = 0.5f;
        public const float NameCenterY = 0.612305f; // 1254
        public const float NameEm      = 107f / MasterH;
        // Auto-fit floor for the four shipped names that exceed the title band at NameEm. The
        // widest, "Cogsworth, The Grand Rebuilder", needs 78.3px in the shipping Alfa Slab face;
        // 72px leaves room for TMP's SDF padding while staying at the same readable floor as body
        // copy. Ellipsis is only eligible after this floor is exhausted.
        public const float NameEmFloor = 72f / MasterH;

        // Rules text box. NOTE this is NOT the parchment's centre — the parchment runs y 1369..1905
        // (centre 1637) but the two stat sockets bite its bottom corners from y 1790 up, so the
        // text box stops there and sits higher than the panel it is printed on. Setting
        // BodyCenterY from the parchment instead pushes the last line under the stat rings.
        // RETIREMENT TRIGGER: moving StatSocketLeft/Right vertically moves this floor with them.
        public const float BodyCenterY = 0.771240f; // 1579   box y 1369..1790
        public const float BodyWidth   = 0.851648f; // 1240   in a 1262-wide parchment
        public const float BodyHeight  = 0.205566f; // 421
        // 100 -> 118 on 2026-08-31 (Sam: "increase the size of the font on cards").
        //
        // RAISING THE CEILING IS FREE; RAISING THE FLOOR IS NOT. CardPresenter drives the label
        // with TMP auto-sizing between BodyEmFloor and this, so this is the size a card gets when
        // its text FITS at it — raise it and short text grows, while anything too long simply
        // auto-shrinks as it always did. Nothing can overflow because of this number.
        //
        // The FLOOR is the opposite: it is the size long text stops shrinking AT, so raising it
        // pushes the longest cards out of the parchment. Measured over all 275 shipped cards with
        // the real GoogleSans-Regular SDF face at the real box, cards whose text no longer fits:
        //
        //      floor 72 -> 0      floor 84 -> 2      floor 100 -> 13
        //      floor 78 -> 1      floor 92 -> 6      floor 112 -> 75
        //
        // So the floor is held at 72 and every card keeps its full text. The long cards still gain
        // 8% from the board card growing under them (this is an em of CardH). Spending 78 or 84 is
        // a real option, but it costs a specific card its parchment — Masterwork Constructor, 109
        // chars, the corpus's longest — and that is a content decision, not a typography one.
        public const float BodyEm      = 118f / MasterH;
        public const float BodyEmFloor = 72f / MasterH; // auto-shrink floor for long rules text
        public const int   KeywordLeadPct = 115;        // keyword lead, % of body

        public static readonly Color NameParchment = new Color32(245, 238, 225, 255);
        public static readonly Color BodyInk       = new Color32(28, 18, 12, 255);

        // Rarity gem socket, bottom-centre. y is the lowest the O206 bezel can sit: the frame's
        // last opaque row under it is 2044, so 2044 − 103 = 1941. The gem is sized by HEIGHT and
        // the four gem PNGs are normalised to square canvases of equal fill by
        // tooling/scripts/normalize_gem_art.py — re-run it after any gem is re-exported, because
        // a padded export renders as a gem that is mysteriously too small.
        // RETIREMENT TRIGGER: re-cutting the frame's bottom rail moves this y.
        public static readonly Vector2 GemSocket = new(0.5f, 0.947754f); // 728, 1941
        public const float GemHeight = 0.075684f; // 155 — O155 stone in the O170 well

        // NO KeywordIcon HERE ANY MORE (removed 2026-09-04, Sam: "we aren't doing keyword icons").
        // It minted "icon_kw_<keyword>" addresses and had no callers at all — StatusMarkCatalog
        // was the one the renderer actually used, and that is gone too. No icon_kw_*.png ever
        // existed outside the deleted placeholder generator, so every such address resolved to
        // null and drew nothing. Keywords read from the card's body text.

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
