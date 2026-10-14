using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Spell-school effects for played spells: earth (a buildup on the caster, a rock falling on
    /// each target, an impact where it lands), the healing shimmer on everything a spell heals,
    /// and the green poof on a minion a Venom source damages. Each sheet is three frames side by
    /// side. Kept apart from the hero power effects on purpose:
    /// similar, not the same (Sam, 2026-10-13).
    ///
    /// <para>Timing is all relative to the play reveal's start, the moment
    /// <see cref="VfxDirector.ShowPlayReveal"/> is called. The entry releases, and the damage or
    /// healing shows, at <c>PlayReveal.HoldSeconds</c>; the earth rocks are scheduled to land
    /// before that, the last one exactly on it.</para>
    /// </summary>
    internal static class SpellSchoolEffects
    {
        public const string EarthBuildupKey = "effect_spell_earth_buildup";
        public const string EarthRockKey = "effect_spell_earth_rock";
        public const string EarthImpactKey = "effect_spell_earth_impact";
        public const string HealShimmerKey = "effect_spell_heal_shimmer";
        public const string VenomPoofKey = "effect_venom_hit_poof";

        /// <summary>The first rock lands this long after the reveal card is gone, and each further
        /// target this long after the one before. VfxDirector hands both to the classifier, which
        /// lengthens the entry's hold by the same amount.</summary>
        public const float EarthLeadSeconds = 0.55f;
        public const float EarthStaggerSeconds = 0.09f;

        private const int SortOrder = 82;
        private const float DepthFraction = 0.06f;
        private const float BuildupSeconds = 0.62f;
        private const float FallSeconds = 0.34f;
        private const float ImpactSeconds = 0.36f;
        private const float ShimmerSeconds = 0.55f;
        private const float ShimmerStagger = 0.05f;
        private const float PoofSeconds = 0.5f;

        private static readonly Dictionary<Sprite, Sprite[]> Frames = new();

        /// <summary>The buildup runs on the caster while the reveal card collapses, finishing as
        /// the first rock starts to fall.</summary>
        public static IEnumerator EarthCaster(Transform parent, Sprite sheet, Camera camera,
                                              Transform caster, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null || caster == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "EarthCasterBuildup");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < BuildupSeconds && caster != null && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / BuildupSeconds);
                Show(sr, frames, t);
                Place(sr, camera, caster.position + Vector3.down * BoardController.CardH * 0.22f,
                      BoardController.CardW * Mathf.Lerp(1.6f, 1.9f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 5f) * (1f - Ramp(t, 0.8f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>One rock dropping on one target, landing <paramref name="landIn"/> seconds
        /// from now, then the impact where it lands.</summary>
        public static IEnumerator EarthRock(Transform parent, Sprite rockSheet, Sprite impactSheet,
                                            Camera camera, Transform target, Vector3 fallback,
                                            float landIn)
        {
            float wait = landIn - FallSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || rockSheet == null || camera == null) yield break;

            Vector3 Target() => target != null ? target.position : fallback;

            // Down the screen and slightly toward the viewer, so it reads as falling onto the
            // board rather than sliding across it.
            Vector3 drop = camera.transform.up * BoardController.CardH * 2.4f;
            SpriteRenderer rock = MakeRenderer(parent, "EarthFallingRock");
            Sprite[] frames = FramesOf(rockSheet);
            float fall = Mathf.Min(FallSeconds, Mathf.Max(0.01f, landIn));
            float elapsed = 0f;
            while (elapsed < fall && rock != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fall);
                // The three drawings tumble the rock; they loop rather than play once.
                SpriteMaterials.Set(rock, frames[Mathf.FloorToInt(elapsed / 0.07f) % frames.Length]);
                float eased = t * t; // gravity: slow off the top, fastest on landing
                Place(rock, camera, Target() + drop * (1f - eased),
                      BoardController.CardW * Mathf.Lerp(0.95f, 1.15f, eased));
                rock.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 6f));
                yield return null;
            }
            if (rock != null) Object.Destroy(rock.gameObject);
            if (parent == null || impactSheet == null) yield break;

            SpriteRenderer impact = MakeRenderer(parent, "EarthImpact");
            Sprite[] burst = FramesOf(impactSheet);
            elapsed = 0f;
            while (elapsed < ImpactSeconds && impact != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / ImpactSeconds);
                Show(impact, burst, t);
                Place(impact, camera, Target(), BoardController.CardW * Mathf.Lerp(1.3f, 1.9f, t));
                impact.color = new Color(1f, 1f, 1f, 1f - Ramp(t, 0.6f));
                yield return null;
            }
            if (impact != null) Object.Destroy(impact.gameObject);
        }

        /// <summary>The shimmer opens on each healed target as the healing lands, a beat apart
        /// when there are several.</summary>
        public static IEnumerator HealShimmer(Transform parent, Sprite sheet, Camera camera,
                                              Transform target, Vector3 fallback, float startIn)
        {
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "HealShimmer");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < ShimmerSeconds && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / ShimmerSeconds);
                Show(sr, frames, t);
                Place(sr, camera, target != null ? target.position : fallback,
                      BoardController.CardW * Mathf.Lerp(1.0f, 1.35f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 8f) * (1f - Ramp(t, 0.65f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>The poof bursts over a minion the moment a Venom source's hit lands on it,
        /// which for a combat hit is when the lunge connects, the same delay as the damage
        /// number.</summary>
        public static IEnumerator VenomPoof(Transform parent, Sprite sheet, Camera camera,
                                            Transform target, float startIn)
        {
            if (target == null) yield break;
            Vector3 last = target.position;
            if (startIn > 0f) yield return new WaitForSeconds(startIn);
            if (parent == null || sheet == null || camera == null) yield break;

            SpriteRenderer sr = MakeRenderer(parent, "VenomHitPoof");
            Sprite[] frames = FramesOf(sheet);
            float elapsed = 0f;
            while (elapsed < PoofSeconds && sr != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / PoofSeconds);
                if (target != null) last = target.position;
                Show(sr, frames, t);
                Place(sr, camera, last, BoardController.CardW * Mathf.Lerp(1.1f, 1.6f, t));
                sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(t * 10f) * (1f - Ramp(t, 0.6f)));
                yield return null;
            }
            if (sr != null) Object.Destroy(sr.gameObject);
        }

        /// <summary>When the <paramref name="index"/>th rock lands, measured from the reveal's
        /// start: after the card, then a stagger apart. The last lands on the release.</summary>
        public static float RockLandsAt(float cardSeconds, int index) =>
            cardSeconds + EarthLeadSeconds + index * EarthStaggerSeconds;

        public static float ShimmerStartsAt(float releaseSeconds, int index) =>
            Mathf.Max(0f, releaseSeconds - 0.12f) + index * ShimmerStagger;

        public static float BuildupStartsAt(float cardSeconds) =>
            Mathf.Max(0f, cardSeconds + EarthLeadSeconds - FallSeconds - BuildupSeconds * 0.85f);

        // Plays the three frames once over t = 0..1.
        private static void Show(SpriteRenderer sr, Sprite[] frames, float t) =>
            SpriteMaterials.Set(sr, frames[Mathf.Min(frames.Length - 1, Mathf.FloorToInt(t * frames.Length))]);

        private static float Ramp(float t, float from) => Mathf.Clamp01((t - from) / (1f - from));

        // Pulled a little toward the camera, as the hero power effects are, so the effect draws in
        // front of the card it lands on; sized by the larger side of the frame.
        private static void Place(SpriteRenderer sr, Camera camera, Vector3 at, float worldSize)
        {
            if (sr.sprite == null) return;
            sr.transform.position = Vector3.Lerp(at, camera.transform.position, DepthFraction);
            Vector3 extent = sr.sprite.bounds.size;
            sr.transform.localScale = Vector3.one * (worldSize / Mathf.Max(extent.x, extent.y, 0.001f));
        }

        private static SpriteRenderer MakeRenderer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = SortOrder;
            return sr;
        }

        private static Sprite[] FramesOf(Sprite sheet)
        {
            if (Frames.TryGetValue(sheet, out Sprite[] cached)) return cached;
            Rect full = sheet.textureRect;
            var frames = new Sprite[3];
            for (int i = 0; i < frames.Length; i++)
            {
                var rect = Rect.MinMaxRect(
                    full.x + Mathf.Floor(i * full.width / frames.Length), full.y,
                    full.x + Mathf.Floor((i + 1) * full.width / frames.Length), full.y + full.height);
                frames[i] = Sprite.Create(sheet.texture, rect, new Vector2(0.5f, 0.5f), 100f);
            }
            Frames[sheet] = frames;
            return frames;
        }
    }
}
