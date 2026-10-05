using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Pyra's Searing Bolt: the portrait cast sends a visible fire projectile to its
    /// selected character. The bloom lands just before the damage is applied.</summary>
    internal static class EmberwardSearingBolt
    {
        public const string SpriteKey = "effect_hero_power_searing_bolt";
        private const int SortOrder = 82;
        private const float DepthFraction = 0.06f;
        private const float TrailThickness = 0.72f;
        private const float BloomSeconds = 0.16f;
        private static Sprite _impactSprite;

        public static IEnumerator Play(Transform parent, Sprite sprite, Camera camera,
                                       Vector3 from, Vector3 to, float strikeInSeconds,
                                       float travelSeconds)
        {
            float wait = strikeInSeconds - travelSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || sprite == null || camera == null) yield break;

            Vector3 eye = camera.transform.position;
            from = Vector3.Lerp(from, eye, DepthFraction);
            to = Vector3.Lerp(to, eye, DepthFraction);
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.01f) yield break;

            var root = new GameObject("EmberwardSearingBolt").transform;
            root.SetParent(parent, true);
            Quaternion rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            Bounds bounds = sprite.bounds;
            // The fireball is painted near the right edge. Give the whole sprite enough length
            // for its nose to arrive on the selected character.
            float width = distance / 0.94f;
            float xScale = width / Mathf.Max(bounds.size.x, 0.001f);
            float yScale = TrailThickness / Mathf.Max(bounds.size.y, 0.001f);

            var boltObject = new GameObject("FireboltTrail");
            boltObject.transform.SetParent(root, true);
            var bolt = boltObject.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(bolt, sprite);
            bolt.sortingOrder = SortOrder;
            bolt.color = Color.white;

            float elapsed = 0f;
            while (elapsed < travelSeconds)
            {
                if (parent == null) yield break;
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, travelSeconds));
                boltObject.transform.SetPositionAndRotation(
                    from + delta.normalized * (width * t * 0.47f), rotation);
                boltObject.transform.localScale = new Vector3(xScale * t, yScale, 1f);
                yield return null;
            }

            boltObject.transform.SetPositionAndRotation(from + delta.normalized * (width * 0.47f),
                                                        rotation);
            boltObject.transform.localScale = new Vector3(xScale, yScale, 1f);
            SpriteRenderer bloom = MakeImpactBloom(root, to);

            float fade = 0f;
            while (fade < BloomSeconds)
            {
                if (parent == null) break;
                fade += Time.deltaTime;
                float a = 1f - Mathf.Clamp01(fade / BloomSeconds);
                Color c = Color.white; c.a = a;
                bolt.color = c;
                Color hot = new Color(1f, 0.56f, 0.16f, a * 0.9f);
                bloom.color = hot;
                bloom.transform.localScale = Vector3.one * Mathf.Lerp(0.9f, 1.35f,
                    Mathf.Clamp01(fade / BloomSeconds));
                yield return null;
            }

            if (root != null) Object.Destroy(root.gameObject);
        }

        private static SpriteRenderer MakeImpactBloom(Transform parent, Vector3 position)
        {
            if (_impactSprite == null)
            {
                const int size = 32;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { name = "SearingBoltImpactBloom", filterMode = FilterMode.Bilinear };
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - size * 0.5f) / (size * 0.5f);
                    float dy = (y + 0.5f - size * 0.5f) / (size * 0.5f);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)), 2f);
                    pixels[y * size + x] = new Color(1f, 0.64f, 0.22f, a);
                }
                texture.SetPixels(pixels);
                texture.Apply();
                _impactSprite = Sprite.Create(texture, new Rect(0, 0, size, size),
                                               new Vector2(0.5f, 0.5f), size);
            }

            var go = new GameObject("FireboltImpactBloom");
            go.transform.SetParent(parent, true);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * 0.9f;
            var renderer = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(renderer, _impactSprite);
            renderer.sortingOrder = SortOrder;
            renderer.color = new Color(1f, 0.56f, 0.16f, 0.9f);
            return renderer;
        }
    }
}
