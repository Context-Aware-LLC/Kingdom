using System.Collections;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Gathers blueprint light at the draw-flight origin, then fades as the card leaves.</summary>
    internal static class ClockwrightBlueprintEffect
    {
        public const string SpriteKey = "effect_hero_power_blueprint";
        public const float GatherSeconds = 0.30f * SpellSchoolEffects.Tempo;
        private const float TailSeconds = 0.35f * SpellSchoolEffects.Tempo;

        public static IEnumerator Play(Transform parent, Sprite art, Transform deck,
                                       float releaseInSeconds)
        {
            float gather = Mathf.Min(GatherSeconds, Mathf.Max(0f, releaseInSeconds));
            float delay = Mathf.Max(0f, releaseInSeconds - gather);
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (parent == null || art == null || deck == null) yield break;

            var go = new GameObject("ClockwrightBlueprintEffect");
            go.transform.SetParent(parent, true);
            var renderer = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(renderer, art);
            // Below the flying card (80), above the deck furniture.
            renderer.sortingOrder = 79;
            float scale = DeckPileView.PileW * 1.5f * SpellSchoolEffects.Size / Mathf.Max(art.bounds.size.x, art.bounds.size.y);
            float duration = gather + TailSeconds;
            // Timed destruction also cleans up if the director is disabled mid-animation.
            Object.Destroy(go, duration + 0.1f);
            for (float elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
            {
                if (parent == null || go == null || deck == null) break;
                float rise = gather > 0f ? Mathf.Clamp01(elapsed / gather) : 1f;
                float fade = Mathf.Clamp01((elapsed - gather) / TailSeconds);
                go.transform.position = deck.position + Vector3.back * 0.06f;
                go.transform.rotation = Quaternion.Euler(0f, 0f, -18f + 36f * elapsed / duration);
                go.transform.localScale = Vector3.one * scale * Mathf.Lerp(0.72f, 1.08f, elapsed / duration);
                renderer.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, rise) * (1f - fade));
                yield return null;
            }
            if (go != null) Object.Destroy(go);
        }
    }
}
