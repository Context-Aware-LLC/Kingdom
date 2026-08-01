using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Data-driven frame/rarity assembly (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2 + hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/card-rendering.md §3.1).
    /// The four painted frames are keyed by rarity alone (<see cref="FrameSprite"/>), and
    /// <see cref="Sockets"/> carries each frame's Sam-calibrated digit centers and art window.
    /// <see cref="GemColor"/>/<see cref="FrameTint"/> remain only as the no-sprite fallback. No
    /// per-card branching: everything resolves keyed by type+rarity.
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

        // Frame sprite key — one painted frame per rarity, shared by every card type
        // (Sam's 2026-07-25 mapping: CarvedStone=common, DarkGilt=rare, IronEmber=epic,
        // AgedGilt=legendary; supersedes both the frame_{type}_{rarity} placeholder scheme
        // and the 2026-07-24 one-master ruling). Resolves through IUiSpriteProvider.
        public static string FrameSprite(Rarity rarity) =>
            $"frame_{rarity.ToString().ToLowerInvariant()}";

        // ── §3.1 socket table (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/card-rendering.md, Sam-calibrated 2026-07-25) ──────────────
        // Fractions of the 1456×2048 master frame, measured from the top-left. Digit centers
        // are the final resolved values (measured boss center + eyeball nudge); the art
        // window is the frame's transparent hole (l, t, r, b).
        public readonly struct FrameSockets
        {
            public readonly Vector2 Cost, Attack, Health; // digit centers as (x/w, y/h)
            public readonly float   DigitEm;              // digit em height / card height
            public readonly Vector4 ArtWindow;            // l, t, r, b fractions

            public FrameSockets(Vector2 cost, Vector2 attack, Vector2 health,
                                float digitEm, Vector4 artWindow)
            {
                Cost = cost; Attack = attack; Health = health;
                DigitEm = digitEm; ArtWindow = artWindow;
            }
        }

        private const float DigitEmStd = 200f / 2048f;

        private static readonly FrameSockets CarvedStone = new(   // common
            new Vector2(0.1140f, 0.0770f), new Vector2(0.1162f, 0.9080f), new Vector2(0.8781f, 0.9094f),
            DigitEmStd, new Vector4(0.0687f, 0.1279f, 0.9334f, 0.5518f));

        private static readonly FrameSockets DarkGilt = new(      // rare
            new Vector2(0.1050f, 0.0751f), new Vector2(0.1187f, 0.8841f), new Vector2(0.8800f, 0.8846f),
            DigitEmStd, new Vector4(0.0549f, 0.1230f, 0.9492f, 0.6060f));

        private static readonly FrameSockets IronEmber = new(     // epic (the calibration master)
            new Vector2(0.1180f, 0.0850f), new Vector2(0.1151f, 0.9068f), new Vector2(0.8762f, 0.9100f),
            DigitEmStd, new Vector4(0.0783f, 0.1294f, 0.9217f, 0.5996f));

        private static readonly FrameSockets AgedGilt = new(      // legendary — smaller bosses, digits 180
            new Vector2(0.1175f, 0.0739f), new Vector2(0.1127f, 0.9005f), new Vector2(0.8816f, 0.8970f),
            180f / 2048f, new Vector4(0.0975f, 0.1201f, 0.9032f, 0.5645f));

        public static FrameSockets Sockets(Rarity rarity) => rarity switch
        {
            Rarity.RARE      => DarkGilt,
            Rarity.EPIC      => IronEmber,
            Rarity.LEGENDARY => AgedGilt,
            _                => CarvedStone,
        };

        // §3.1 type spec — global, frame-independent. Name/body sizes are the 2026-07-25
        // in-engine calibration (Sam, live at board scale): the mock's 89/68 read too small
        // on screen.
        public const float NameCenterX   = 0.56f;   // of card width
        public const float NameCenterY   = 0.068f;  // of card height, from the top
        public const float NameLeftClamp = 0.232f;  // name's left edge never crosses this
        public const float NameEm        = 107f / 2048f;
        public const float BodyCenterY   = 0.70f;   // rules-band center
        public const float BodyEm        = 108f / 2048f;
        public const float BodyEmFloor   = 78f / 2048f; // auto-shrink floor for long rules text
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
