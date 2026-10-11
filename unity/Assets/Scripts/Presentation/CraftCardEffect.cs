using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    // The same normalized trajectory fractures the baked face or assembles it in reverse.
    internal sealed class CraftCardEffect : VisualElement
    {
        private const int Columns = 5;
        private const int Rows = 7;
        private const float Duration = 1.15f;
        private readonly Texture2D _texture;
        private readonly Fragment[] _fragments;
        private float _scatter;

        private struct Fragment
        {
            public Vector2 A, B, C, Center, Landing;
            public float Spin;
        }

        private CraftCardEffect(Texture2D texture)
        {
            _texture = texture;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            _fragments = new Fragment[Columns * Rows * 2];
            var random = new System.Random(731);
            var points = new Vector2[Columns + 1, Rows + 1];
            for (int y = 0; y <= Rows; y++)
            for (int x = 0; x <= Columns; x++)
            {
                float jitterX = x == 0 || x == Columns ? 0 : ((float)random.NextDouble() - 0.5f) * 0.6f;
                float jitterY = y == 0 || y == Rows ? 0 : ((float)random.NextDouble() - 0.5f) * 0.6f;
                points[x, y] = new Vector2((x + jitterX) / Columns, (y + jitterY) / Rows);
            }
            int index = 0;
            for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                AddFragment(points[x, y], points[x + 1, y], points[x + 1, y + 1], random, index++);
                AddFragment(points[x, y], points[x + 1, y + 1], points[x, y + 1], random, index++);
            }
            generateVisualContent += Draw;
        }

        private void AddFragment(Vector2 a, Vector2 b, Vector2 c, System.Random random, int index)
        {
            _fragments[index] = new Fragment
            {
                A = a, B = b, C = c, Center = (a + b + c) / 3f,
                Landing = new Vector2(0.15f + (float)random.NextDouble() * 0.7f,
                    0.72f + (float)random.NextDouble() * 0.2f),
                Spin = ((float)random.NextDouble() - 0.5f) * 9f,
            };
        }

        public static async Task PlayAsync(VisualElement face, Texture2D texture, bool render, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (texture == null || face?.panel == null || face.contentRect.width <= 0f) return;
            var effect = new CraftCardEffect(texture);
            StyleFloat originalOpacity = face.style.opacity;
            face.parent.Add(effect);
            face.style.opacity = 0f;
            try
            {
                float started = Time.realtimeSinceStartup;
                while (face.panel != null)
                {
                    ct.ThrowIfCancellationRequested();
                    float progress = Mathf.Clamp01((Time.realtimeSinceStartup - started) / Duration);
                    effect._scatter = render ? 1f - progress : progress;
                    effect.style.left = face.layout.x;
                    effect.style.top = face.layout.y;
                    effect.style.width = face.layout.width;
                    effect.style.height = face.layout.height;
                    effect.MarkDirtyRepaint();
                    if (progress >= 1f) break;
                    await Task.Delay(16, ct);
                }
                if (render && face.panel != null) await Task.Delay(200, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Presentation failure must not turn a confirmed spend into an ambiguous result.
                Debug.LogWarning($"[Craft] animation skipped: {ex.GetType().Name}");
            }
            finally
            {
                face.style.opacity = originalOpacity;
                effect.RemoveFromHierarchy();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            Vector2 size = contentRect.size;
            if (size.x <= 0f || size.y <= 0f) return;
            float travel = Mathf.SmoothStep(0f, 1f, _scatter);
            float crystal = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.65f, _scatter));
            float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.82f, 1f, _scatter));
            MeshWriteData cards = context.Allocate(_fragments.Length * 3, _fragments.Length * 3, _texture);
            MeshWriteData crystals = context.Allocate(_fragments.Length * 12, _fragments.Length * 12);
            ushort cardIndex = 0;
            ushort crystalIndex = 0;
            foreach (Fragment fragment in _fragments)
            {
                Vector2 center = Vector2.Scale(Vector2.Lerp(fragment.Center, fragment.Landing, travel), size);
                float burst = Mathf.Sin(_scatter * Mathf.PI);
                center += new Vector2((fragment.Center.x - 0.5f) * size.x * 0.5f, -size.y * 0.14f) * burst;
                float angle = fragment.Spin * travel;
                float shrink = Mathf.Lerp(1f, 0.25f, travel);
                Color tint = new Color(1f, 1f, 1f, 1f - crystal);
                CardVertex(cards, fragment.A, fragment.Center, center, size, angle, shrink, tint, ref cardIndex);
                CardVertex(cards, fragment.B, fragment.Center, center, size, angle, shrink, tint, ref cardIndex);
                CardVertex(cards, fragment.C, fragment.Center, center, size, angle, shrink, tint, ref cardIndex);

                float radius = size.x * 0.027f;
                Vector2 top = center + Rotate(new Vector2(0f, -radius * 1.8f), angle);
                Vector2 right = center + Rotate(new Vector2(radius * 0.65f, 0f), angle);
                Vector2 bottom = center + Rotate(new Vector2(0f, radius * 1.8f), angle);
                Vector2 left = center + Rotate(new Vector2(-radius * 0.65f, 0f), angle);
                float opacity = crystal * alpha;
                Facet(crystals, center, top, right, new Color(0.82f, 1f, 1f, opacity), ref crystalIndex);
                Facet(crystals, center, right, bottom, new Color(0.2f, 0.68f, 0.88f, opacity), ref crystalIndex);
                Facet(crystals, center, bottom, left, new Color(0.13f, 0.4f, 0.64f, opacity), ref crystalIndex);
                Facet(crystals, center, left, top, new Color(0.48f, 0.88f, 0.96f, opacity), ref crystalIndex);
            }
        }

        private static void CardVertex(MeshWriteData mesh, Vector2 point, Vector2 origin, Vector2 center,
            Vector2 size, float angle, float scale, Color tint, ref ushort index)
        {
            Vector2 position = center + Rotate(Vector2.Scale(point - origin, size) * scale, angle);
            WriteVertex(mesh, position, new Vector2(point.x, 1f - point.y), tint, ref index);
        }

        private static void Facet(MeshWriteData mesh, Vector2 a, Vector2 b, Vector2 c, Color tint, ref ushort index)
        {
            WriteVertex(mesh, a, Vector2.zero, tint, ref index);
            WriteVertex(mesh, b, Vector2.zero, tint, ref index);
            WriteVertex(mesh, c, Vector2.zero, tint, ref index);
        }

        private static void WriteVertex(MeshWriteData mesh, Vector2 point, Vector2 uv, Color tint, ref ushort index)
        {
            mesh.SetNextVertex(new Vertex { position = new Vector3(point.x, point.y, Vertex.nearZ), uv = uv, tint = tint });
            mesh.SetNextIndex(index++);
        }

        private static Vector2 Rotate(Vector2 point, float angle)
        {
            float sin = Mathf.Sin(angle);
            float cos = Mathf.Cos(angle);
            return new Vector2(point.x * cos - point.y * sin, point.x * sin + point.y * cos);
        }
    }
}
