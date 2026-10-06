using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Tempest's Chain Spark, drawn: a thin arc jumps from the hero power socket to the first
    /// target, then bounces from there to the next (Sam, 2026-10-04). These are one-damage pings,
    /// so each hop is a small arc that is gone almost as it lands — an earlier version forked one
    /// continuous bolt across the board, and it read as a board wipe.
    ///
    /// <para><b>The type and <c>HeroPowerDelivery.ForkedLightning</c> kept the old name.</b>
    /// Nothing forks any more.</para>
    ///
    /// <para><b>One sprite, stretched between two points.</b> <c>effect_hero_power_tempest.png</c> is
    /// a whole arc with a flare at each end, so a hop is that sprite rotated and scaled until its
    /// flares sit on the hop's ends, big flare on whatever was struck. Only its LENGTH follows the
    /// distance: its thickness stops at <see cref="MaxThickness"/>, or a hop the length of the
    /// board would be as tall as a card.</para>
    ///
    /// <para><b>It is timed backwards from the strike.</b> The caller says how long until the
    /// board redraws; the last hop lands on that frame, so the health numbers change as the spark
    /// arrives and not before.</para>
    /// </summary>
    internal static class ForkedLightning
    {
        public const string ArcSpriteKey = "effect_hero_power_tempest";

        // Where the two flares sit in the sprite, as fractions of it (x from the left, y from the
        // bottom). Measured off the art — re-measure if the file is ever replaced.
        private static readonly Vector2 BigFlare   = new Vector2(0.1873f, 0.2573f);
        private static readonly Vector2 SmallFlare = new Vector2(0.8697f, 0.3859f);

        /// <summary>The most the sprite is scaled ACROSS its length. At 1 the arc stands about
        /// five world units tall; a card is two.</summary>
        private const float MaxThickness = 0.2f;
        /// <summary>How long a hop stays lit after it lands, and how long it takes to go.</summary>
        private const float LingerSeconds = 0.06f;
        private const float FadeSeconds = 0.16f;

        /// <summary>Card materials are opaque, so depth is what draws the bolt over them — it
        /// rides this fraction of the way from the board to the camera. Lifting along the camera
        /// ray keeps every point over the thing it was resolved from.</summary>
        private const float DepthFraction = 0.06f;
        /// <summary>Same band as the play reveal: above the hand fan, below inspect.</summary>
        private const int SortOrder = 80;

        private sealed class Arc
        {
            public Transform Transform;
            public SpriteRenderer Renderer;
            public Vector3 From, To;      // world; the arc grows From → To
            public bool Mirrored;
            public float GrowStart, GrowEnd;
        }

        /// <param name="strikeInSeconds">Seconds from now until the board redraws.</param>
        /// <param name="leadSeconds">How long the spark takes from the socket to its last target.</param>
        public static IEnumerator Play(Transform parent, Sprite sprite, Camera camera, Vector3 socket,
                                       IReadOnlyList<Vector3> targets, float strikeInSeconds,
                                       float leadSeconds)
        {
            float wait = strikeInSeconds - leadSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || camera == null) yield break;

            Vector3 eye = camera.transform.position;
            Vector3 Lift(Vector3 p) => Vector3.Lerp(p, eye, DepthFraction);

            // One hop per target, each starting where the last one landed. Alternate the bow, or
            // two hops off one sprite read as a copy of each other.
            var arcs = new List<Arc>();
            float hop = leadSeconds / targets.Count;
            Vector3 from = socket;
            for (int i = 0; i < targets.Count; i++)
            {
                arcs.Add(Make(parent, sprite, Lift(from), Lift(targets[i]),
                              mirrored: i % 2 == 1, i * hop, (i + 1) * hop));
                from = targets[i];
            }

            float total = leadSeconds + LingerSeconds + FadeSeconds;
            for (float t = 0f; t < total; t += Time.deltaTime)
            {
                if (parent == null) break;
                foreach (Arc arc in arcs) Pose(arc, sprite, t);
                yield return null;
            }

            foreach (Arc arc in arcs)
                if (arc.Transform != null) Object.Destroy(arc.Transform.gameObject);
        }

        private static Arc Make(Transform parent, Sprite sprite, Vector3 from, Vector3 to,
                                bool mirrored, float growStart, float growEnd)
        {
            var go = new GameObject("ChainSparkArc");
            go.transform.SetParent(parent, worldPositionStays: true);
            var sr = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(sr, sprite);
            sr.sortingOrder = SortOrder;
            sr.enabled = false;
            return new Arc
            {
                Transform = go.transform, Renderer = sr, From = from, To = to,
                Mirrored = mirrored, GrowStart = growStart, GrowEnd = growEnd,
            };
        }

        private static void Pose(Arc arc, Sprite sprite, float t)
        {
            if (arc.Transform == null) return;

            // Each hop fades on its own clock, so the first is already going while the second is
            // in the air: a spark that travels, not a web that builds up.
            float fade = 1f - Mathf.Clamp01((t - arc.GrowEnd - LingerSeconds) / FadeSeconds);
            if (t < arc.GrowStart || fade <= 0f) { arc.Renderer.enabled = false; return; }

            float span = Mathf.Max(arc.GrowEnd - arc.GrowStart, 0.0001f);
            float grown = Mathf.Clamp01((t - arc.GrowStart) / span);
            // Never zero length: the placement divides by it.
            Vector3 tip = Vector3.Lerp(arc.From, arc.To, Mathf.Max(grown, 0.05f));
            Place(arc.Transform, sprite, big: tip, small: arc.From, arc.Mirrored);

            // Lightning does not hold still: the brightness jumps every frame it is alive.
            Color c = Color.white;
            c.a = fade * Random.Range(0.72f, 1f);
            arc.Renderer.color = c;
            arc.Renderer.enabled = true;
        }

        /// <summary>Rotate, scale and move the sprite so its big flare sits on
        /// <paramref name="big"/> and its small flare on <paramref name="small"/>.</summary>
        private static void Place(Transform arc, Sprite sprite, Vector3 big, Vector3 small, bool mirrored)
        {
            Bounds b = sprite.bounds;
            Vector2 Local(Vector2 frac)
            {
                var p = new Vector2(b.min.x + b.size.x * frac.x, b.min.y + b.size.y * frac.y);
                return mirrored ? new Vector2(p.x, -p.y) : p;
            }

            Vector2 a = Local(BigFlare);
            Vector2 e = Local(SmallFlare) - a;
            Vector2 d = small - big;
            float length = d.magnitude;
            if (length < 0.0001f || Mathf.Abs(e.x) < 0.0001f) return;

            // Uniform until the thickness cap, then only the length keeps stretching. The flares
            // are not level in the art, so the along-scale is solved for rather than read off:
            // the scaled flare-to-flare vector has to come out exactly `length` long.
            float across = Mathf.Min(length / e.magnitude, MaxThickness);
            float rise = e.y * across;
            float along = Mathf.Sqrt(Mathf.Max(length * length - rise * rise, 0f)) / Mathf.Abs(e.x);

            float angle = Mathf.Atan2(d.y, d.x) - Mathf.Atan2(rise, e.x * along);
            Quaternion rotation = Quaternion.Euler(0f, 0f, angle * Mathf.Rad2Deg);

            arc.rotation = rotation;
            arc.localScale = new Vector3(along, mirrored ? -across : across, 1f);
            Vector3 offset = rotation * new Vector3(a.x * along, a.y * across, 0f);
            arc.position = new Vector3(big.x - offset.x, big.y - offset.y, big.z);
        }
    }
}
