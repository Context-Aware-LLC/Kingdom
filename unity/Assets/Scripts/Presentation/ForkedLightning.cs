using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Tempest's Chain Spark, drawn: one bolt leaves the hero power socket, splits a quarter of
    /// the way out, and a branch lands on every target at the same moment (Sam, 2026-10-04).
    ///
    /// <para><b>One sprite, stretched between two points.</b> <c>effect_chain_spark_arc.png</c> is
    /// a whole arc with a flare at each end, so a segment is that sprite rotated and scaled until
    /// its flares sit on the segment's ends. The big flare goes where the energy is: on the socket
    /// for the stem, on the target for a branch.</para>
    ///
    /// <para><b>It is timed backwards from the strike.</b> The caller says how long until the
    /// board redraws; the branches finish growing on that frame, so the health numbers change as
    /// the bolt lands and not before.</para>
    /// </summary>
    internal static class ForkedLightning
    {
        public const string ArcSpriteKey = "effect_chain_spark_arc";

        // Where the two flares sit in the sprite, as fractions of it (x from the left, y from the
        // bottom). Measured off the art — re-measure if the file is ever replaced.
        private static readonly Vector2 BigFlare   = new Vector2(0.1873f, 0.2573f);
        private static readonly Vector2 SmallFlare = new Vector2(0.8697f, 0.3859f);

        /// <summary>How far from the socket toward its targets the bolt splits.</summary>
        private const float ForkAt = 0.25f;
        /// <summary>The stem's share of the lead time; the branches take the rest.</summary>
        private const float StemShare = 0.4f;
        private const float LingerSeconds = 0.10f;
        private const float FadeSeconds = 0.22f;

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
            public bool BigFlareAtFrom;
            public bool Mirrored;
            public float GrowStart, GrowEnd;
        }

        /// <param name="strikeInSeconds">Seconds from now until the board redraws.</param>
        /// <param name="leadSeconds">How long the bolt takes from first spark to strike.</param>
        public static IEnumerator Play(Transform parent, Sprite sprite, Camera camera, Vector3 socket,
                                       IReadOnlyList<Vector3> targets, float strikeInSeconds,
                                       float leadSeconds)
        {
            float wait = strikeInSeconds - leadSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || camera == null) yield break;

            Vector3 eye = camera.transform.position;
            Vector3 Lift(Vector3 p) => Vector3.Lerp(p, eye, DepthFraction);

            var arcs = new List<Arc>();
            if (targets.Count == 1)
            {
                // Nothing to fork to: one bolt, socket to target, over the whole lead.
                arcs.Add(Make(parent, sprite, Lift(socket), Lift(targets[0]),
                              bigFlareAtFrom: false, mirrored: false, 0f, leadSeconds));
            }
            else
            {
                Vector3 centre = Vector3.zero;
                foreach (Vector3 t in targets) centre += t;
                centre /= targets.Count;
                Vector3 fork = Vector3.Lerp(socket, centre, ForkAt);

                float stem = leadSeconds * StemShare;
                arcs.Add(Make(parent, sprite, Lift(socket), Lift(fork),
                              bigFlareAtFrom: true, mirrored: false, 0f, stem));
                // Alternate the bow, or two branches off one sprite read as a copy of each other.
                for (int i = 0; i < targets.Count; i++)
                    arcs.Add(Make(parent, sprite, Lift(fork), Lift(targets[i]),
                                  bigFlareAtFrom: false, mirrored: i % 2 == 1, stem, leadSeconds));
            }

            float total = leadSeconds + LingerSeconds + FadeSeconds;
            for (float t = 0f; t < total; t += Time.deltaTime)
            {
                if (parent == null) break;
                float fade = t <= leadSeconds + LingerSeconds
                    ? 1f
                    : 1f - Mathf.Clamp01((t - leadSeconds - LingerSeconds) / FadeSeconds);
                foreach (Arc arc in arcs) Pose(arc, sprite, t, fade);
                yield return null;
            }

            foreach (Arc arc in arcs)
                if (arc.Transform != null) Object.Destroy(arc.Transform.gameObject);
        }

        private static Arc Make(Transform parent, Sprite sprite, Vector3 from, Vector3 to,
                                bool bigFlareAtFrom, bool mirrored, float growStart, float growEnd)
        {
            var go = new GameObject("ForkedLightningArc");
            go.transform.SetParent(parent, worldPositionStays: true);
            var sr = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(sr, sprite);
            sr.sortingOrder = SortOrder;
            sr.enabled = false;
            return new Arc
            {
                Transform = go.transform, Renderer = sr, From = from, To = to,
                BigFlareAtFrom = bigFlareAtFrom, Mirrored = mirrored,
                GrowStart = growStart, GrowEnd = growEnd,
            };
        }

        private static void Pose(Arc arc, Sprite sprite, float t, float fade)
        {
            if (arc.Transform == null) return;
            if (t < arc.GrowStart) { arc.Renderer.enabled = false; return; }

            float span = Mathf.Max(arc.GrowEnd - arc.GrowStart, 0.0001f);
            float grown = Mathf.Clamp01((t - arc.GrowStart) / span);
            // Never zero length: the placement divides by it.
            Vector3 tip = Vector3.Lerp(arc.From, arc.To, Mathf.Max(grown, 0.05f));

            Vector3 big   = arc.BigFlareAtFrom ? arc.From : tip;
            Vector3 small = arc.BigFlareAtFrom ? tip : arc.From;
            Place(arc.Transform, sprite, big, small, arc.Mirrored);

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
            if (length < 0.0001f || e.sqrMagnitude < 0.0001f) return;

            float scale = length / e.magnitude;
            float angle = Mathf.Atan2(d.y, d.x) - Mathf.Atan2(e.y, e.x);
            Quaternion rotation = Quaternion.Euler(0f, 0f, angle * Mathf.Rad2Deg);

            arc.rotation = rotation;
            arc.localScale = new Vector3(scale, mirrored ? -scale : scale, 1f);
            Vector3 offset = rotation * new Vector3(a.x * scale, a.y * scale, 0f);
            arc.position = new Vector3(big.x - offset.x, big.y - offset.y, big.z);
        }
    }
}
