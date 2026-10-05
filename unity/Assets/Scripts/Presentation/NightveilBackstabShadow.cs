using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Nightveil's shadow rises beside the enemy hero as Backstab lands.</summary>
    internal static class NightveilBackstabShadow
    {
        public const string SpriteKey = "effect_hero_cast_nightveil";
        private const int SortOrder = 82;
        private const float PortraitScale = 0.62f;
        private const float FadeSeconds = 0.18f;

        public static IEnumerator Play(Transform parent, Sprite sprite, Transform enemyHero,
                                       float strikeInSeconds, float riseSeconds)
        {
            riseSeconds = Mathf.Max(0.08f, riseSeconds);
            float wait = strikeInSeconds - riseSeconds;
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
            floor += right * (bounds.extents.x * 0.72f + width * 0.24f);
            floor.z = enemyHero.position.z - 0.04f;
            Vector3 landed = floor + Vector3.up * (height * 0.5f);
            Vector3 emerged = landed + Vector3.up * (height * 0.52f);

            var go = new GameObject("NightveilBackstabShadow");
            go.transform.SetParent(parent, true);
            go.transform.position = landed;
            go.transform.localScale = Vector3.one * scale;

            var renderer = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(renderer, sprite);
            renderer.sortingOrder = SortOrder;
            renderer.color = new Color(0.025f, 0.025f, 0.035f, 0.52f);

            float elapsed = 0f;
            while (elapsed < riseSeconds)
            {
                if (parent == null || go == null) yield break;
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / riseSeconds));
                go.transform.position = Vector3.Lerp(landed, emerged, t);
                yield return null;
            }

            float fade = 0f;
            Color shadowColor = renderer.color;
            while (fade < FadeSeconds)
            {
                if (parent == null || go == null) yield break;
                fade += Time.deltaTime;
                shadowColor.a = 0.52f * (1f - Mathf.Clamp01(fade / FadeSeconds));
                renderer.color = shadowColor;
                yield return null;
            }
            if (go != null) Object.Destroy(go);
        }
    }
}
