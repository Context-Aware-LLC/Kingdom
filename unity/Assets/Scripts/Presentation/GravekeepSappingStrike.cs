using System.Collections;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Gravekeep's Weaken: a narrow green siphon from the power socket, ending in a
    /// brief emerald flash over the minion as its stats change.</summary>
    internal static class GravekeepSappingStrike
    {
        private const int Segments = 7;
        private const int SortOrder = 82;
        private static readonly Color Green = new Color(0.28f, 1f, 0.40f, 1f);

        public static IEnumerator Play(Transform parent, Vector3 from, Vector3 to,
                                       float strikeInSeconds, float leadSeconds)
        {
            float wait = strikeInSeconds - leadSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null) yield break;

            Camera camera = Camera.main;
            if (camera != null)
            {
                Vector3 eye = camera.transform.position;
                from = Vector3.Lerp(from, eye, 0.07f);
                to = Vector3.Lerp(to, eye, 0.07f);
            }

            var root = new GameObject("GravekeepSappingStrike").transform;
            root.SetParent(parent, true);
            var bolt = MakeLine(root, "SappingBolt", 0.045f);
            Vector3[] points = BuildBolt(from, to);
            var flash = MakeFlash(root, to);

            float elapsed = 0f;
            while (elapsed < leadSeconds)
            {
                if (parent == null) break;
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, leadSeconds));
                bolt.positionCount = Segments + 1;
                for (int i = 0; i <= Segments; i++)
                    bolt.SetPosition(i, Vector3.Lerp(from, points[i], t));
                yield return null;
            }

            bolt.enabled = false;
            flash.enabled = true;
            float hold = 0.08f;
            while (hold > 0f)
            {
                if (parent == null) break;
                hold -= Time.deltaTime;
                float pulse = 1f + 0.22f * Mathf.Sin(hold * 45f);
                flash.transform.localScale = Vector3.one * (0.78f * pulse);
                Color c = Green; c.a = Mathf.Clamp01(hold / 0.08f);
                flash.color = c;
                yield return null;
            }
            if (root != null) Object.Destroy(root.gameObject);
        }

        private static Vector3[] BuildBolt(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            Vector3 side = Vector3.Cross(direction.normalized, Vector3.forward).normalized;
            if (side.sqrMagnitude < 0.01f) side = Vector3.up;
            var points = new Vector3[Segments + 1];
            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                float zig = i == 0 || i == Segments ? 0f : (i % 2 == 0 ? 1f : -1f) * 0.10f;
                points[i] = Vector3.Lerp(from, to, t) + side * zig;
            }
            return points;
        }

        private static LineRenderer MakeLine(Transform parent, string name, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = width;
            line.endWidth = width * 0.45f;
            line.numCapVertices = 3;
            line.sortingOrder = SortOrder;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = Green;
            line.endColor = Green;
            return line;
        }

        private static SpriteRenderer MakeFlash(Transform parent, Vector3 position)
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { name = "GravekeepImpactFlash", filterMode = FilterMode.Bilinear };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + .5f - size * .5f) / (size * .5f);
                float dy = (y + .5f - size * .5f) / (size * .5f);
                float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)), 2f);
                pixels[y * size + x] = new Color(Green.r, Green.g, Green.b, a * .82f);
            }
            texture.SetPixels(pixels);
            texture.Apply();
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), size);
            var go = new GameObject("SappingImpactFlash");
            go.transform.SetParent(parent, true);
            go.transform.position = position;
            var renderer = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(renderer, sprite);
            renderer.sortingOrder = SortOrder;
            renderer.color = Green;
            renderer.enabled = false;
            go.transform.localScale = Vector3.one * .78f;
            return renderer;
        }
    }
}
