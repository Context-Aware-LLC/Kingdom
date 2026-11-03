using System.Collections;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Nightveil's shadow rises beside the enemy hero as Backstab lands.</summary>
    internal static class NightveilBackstabShadow
    {
        public const string SpriteKey = "effect_hero_power_nightveil";
        public const float PortraitWipeSeconds = 0.55f * SpellSchoolEffects.Tempo;
        public const float RiseSeconds = 0.55f * SpellSchoolEffects.Tempo;
        // The shadow sits behind the hero's frame (8) and portrait (9).
        private const int SortOrder = 7;
        private const float PortraitScale = 0.62f * SpellSchoolEffects.Size;
        private const float Opacity = 0.72f;
        private const float FadeSeconds = 1.0f;

        public static IEnumerator Play(Transform parent, Sprite sprite, Transform enemyHero,
                                       float strikeInSeconds)
        {
            float wait = strikeInSeconds - RiseSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || enemyHero == null || sprite == null) yield break;

            Transform portrait = enemyHero.Find("Portrait");
            Renderer portraitRenderer = portrait != null ? portrait.GetComponent<Renderer>() : null;
            Bounds bounds = portraitRenderer != null
                ? portraitRenderer.bounds
                : new Bounds(enemyHero.position, new Vector3(1.5f, 2f, 0.1f));

            Camera camera = Camera.main;
            Vector3 right = camera != null ? camera.transform.right : Vector3.right;
            float height = bounds.size.y * PortraitScale;
            float scale = height / sprite.bounds.size.y;
            float width = sprite.bounds.size.x * scale;
            Vector3 floor = bounds.center;
            floor.y = bounds.min.y + 0.04f;
            floor += right * (bounds.extents.x + width * 0.30f);
            // Behind even the tilted portrait's far edge, in front of the board backdrop.
            floor.z = bounds.max.z + 0.025f;
            Vector3 landed = floor + Vector3.up * (height * 0.5f);
            Vector3 below = landed - Vector3.up * (height * 0.58f);
            Vector3 emerged = landed + Vector3.up * (height * 0.08f);

            var go = new GameObject("NightveilBackstabShadow");
            go.transform.SetParent(parent, true);
            go.transform.position = below;
            go.transform.localScale = Vector3.one * scale;

            var renderer = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(renderer, sprite);
            renderer.sortingOrder = SortOrder;
            Color shadowColor = new Color(0.10f, 0.085f, 0.12f, 0f);
            renderer.color = shadowColor;

            try
            {
                float elapsed = 0f;
                while (elapsed < RiseSeconds)
                {
                    if (parent == null || go == null) yield break;
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / RiseSeconds);
                    go.transform.position = Vector3.Lerp(below, emerged, Mathf.SmoothStep(0f, 1f, t));
                    shadowColor.a = Opacity * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.4f));
                    renderer.color = shadowColor;
                    yield return null;
                }

                // Damage lands at full visibility. The figure then continues to drift upward
                // throughout a full second of fading, rather than vanishing on the strike.
                float fade = 0f;
                while (fade < FadeSeconds)
                {
                    if (parent == null || go == null) yield break;
                    fade += Time.deltaTime;
                    float t = Mathf.Clamp01(fade / FadeSeconds);
                    go.transform.position = emerged + Vector3.up * (height * 0.18f * t);
                    shadowColor.a = Opacity * (1f - Mathf.SmoothStep(0f, 1f, t));
                    renderer.color = shadowColor;
                    yield return null;
                }
            }
            finally
            {
                if (go != null) Object.Destroy(go);
            }
        }
    }
}
