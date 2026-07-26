using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Ring, socket and stat-boss geometry for one hero-frame material, read from the
    /// HeroFrames_&lt;Material&gt;.json that split_hero_frames.py writes beside the PNGs it
    /// measured them off.
    ///
    /// WHY THIS IS DATA AND NOT CONSTANTS. The image generator does not honour the requested
    /// layout, and where the bosses land is expected to differ every batch (Sam, 2026-07-25).
    /// Constants in C# would keep rendering numerals at the previous batch's positions after
    /// the art was replaced, and nothing would report it — the frame would simply look subtly
    /// wrong. All fractions are of the square canvas, top-left origin.
    /// </summary>
    public sealed class HeroFrameGeometry
    {
        public sealed class Frame
        {
            public float SocketDiameter = 0.55f;
            public float RingDiameter   = 0.7464f;
            public Vector2[] Bosses     = new Vector2[0];
            public float BossDiameter   = 0.19f;

            /// <summary>Boss <paramref name="i"/>, or a corner of the ring's lower diagonal if
            /// the art has fewer than expected — visibly placed, never silently at centre.</summary>
            public Vector2 BossAt(int i)
            {
                if (Bosses != null && i < Bosses.Length) return Bosses[i];
                float r = RingDiameter * 0.5f;
                float k = 0.7071f * r;                     // 45 degrees off vertical
                return new Vector2(0.5f + (i == 0 ? -k : k), 0.5f + k);
            }

            public float BossDiameterOrDefault => BossDiameter > 0.01f ? BossDiameter : 0.19f;
        }

        public Frame HeroPower = new Frame();
        public Frame Weapon    = new Frame();

        public static HeroFrameGeometry Load(string material)
        {
            var result = new HeroFrameGeometry();
            string json = ReadJson(material);
            if (string.IsNullOrEmpty(json)) return result;

            Root root;
            try { root = JsonUtility.FromJson<Root>(json); }
            catch (System.Exception e)
            {
                Debug.LogError($"[HeroFrameGeometry] HeroFrames_{material}.json is unreadable " +
                               $"({e.Message}); sockets fall back to nominal geometry.");
                return result;
            }

            if (root?.frames == null) return result;
            Apply(root.frames.HeroPower, result.HeroPower);
            Apply(root.frames.Weapon,    result.Weapon);
            return result;
        }

        private static void Apply(FrameJson src, Frame dst)
        {
            if (src == null) return;
            if (src.socketDiameter > 0.01f) dst.SocketDiameter = src.socketDiameter;
            if (src.ringDiameter   > 0.01f) dst.RingDiameter   = src.ringDiameter;
            if (src.bosses == null) return;

            var pts = new Vector2[src.bosses.Length];
            float dsum = 0f;
            for (int i = 0; i < src.bosses.Length; i++)
            {
                BossJson b = src.bosses[i];
                pts[i] = b?.xy != null && b.xy.Length >= 2
                       ? new Vector2(b.xy[0], b.xy[1])
                       : new Vector2(0.5f, 0.5f);
                dsum += b?.diameter ?? 0f;
            }
            dst.Bosses = pts;
            if (pts.Length > 0 && dsum > 0f) dst.BossDiameter = dsum / pts.Length;
        }

        private static string ReadJson(string material)
        {
#if UNITY_EDITOR
            string path = $"Assets/Art/UI/HeroColumn/HeroFrames_{material}.json";
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset != null) return asset.text;
            Debug.LogWarning($"[HeroFrameGeometry] {path} not found; sockets fall back to " +
                             "nominal geometry. Re-run tooling/scripts/split_hero_frames.py.");
            return null;
#else
            // NOT WIRED FOR BUILDS YET. The JSON needs an Addressables entry the way ui/card_back
            // does — without one this resolves to nothing in a player and the numerals drift off
            // the bosses, which is exactly the failure d0d8efa had to clean up for the card back.
            // Loud on purpose: a silent fallback here looks almost right, which is worse.
            Debug.LogError($"[HeroFrameGeometry] HeroFrames_{material}.json is not addressable, " +
                           "so stat numerals use nominal positions in this build. " +
                           "Register it in UI_Core before shipping the hero sockets.");
            return null;
#endif
        }

        // ── JSON shapes (field names must match split_hero_frames.py's output exactly) ──

        [System.Serializable] private sealed class Root      { public FramesJson frames; }
        [System.Serializable] private sealed class FramesJson { public FrameJson HeroPower; public FrameJson Weapon; }
        [System.Serializable] private sealed class FrameJson  { public BossJson[] bosses; public float socketDiameter; public float ringDiameter; }
        [System.Serializable] private sealed class BossJson   { public float[] xy; public float diameter; }
    }
}
