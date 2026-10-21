using Kingdom.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>Small, local light effects behind a card. No textures, screen flashes or camera
    /// rig: a bounded mesh repaints only during a reveal, then settles to a quiet rarity halo.</summary>
    internal sealed class PackRevealEffect : VisualElement
    {
        private const int Segments = 48;
        private readonly Color _color;
        private readonly bool _premium;
        private readonly bool _legendary;
        private IVisualElementScheduledItem _animation;
        private float _started;
        private float _progress = 1f;
        private bool _hovered;
        private bool _revealed;

        public PackRevealEffect(Rarity rarity)
        {
            _color = rarity == Rarity.LEGENDARY ? new Color(1f, 0.64f, 0.16f) :
                rarity == Rarity.EPIC ? new Color(0.72f, 0.34f, 1f) :
                rarity == Rarity.RARE ? new Color(0.24f, 0.57f, 1f) :
                new Color(0.66f, 0.76f, 0.83f);
            _premium = rarity >= Rarity.EPIC;
            _legendary = rarity == Rarity.LEGENDARY;
            pickingMode = PickingMode.Ignore;
            AddToClassList("reveal-card__light");
            generateVisualContent += Draw;
            RegisterCallback<DetachFromPanelEvent>(_ => Stop());
        }

        public void SetHovered(bool hovered)
        {
            _hovered = hovered;
            MarkDirtyRepaint();
        }

        public void Reveal()
        {
            _revealed = true;
            _hovered = false;
            _progress = 0f;
            _started = Time.realtimeSinceStartup;
            _animation?.Pause();
            _animation = schedule.Execute(() =>
            {
                _progress = Mathf.Clamp01((Time.realtimeSinceStartup - _started) /
                    (_legendary ? 1.2f : 0.75f));
                MarkDirtyRepaint();
                if (_progress >= 1f) _animation?.Pause();
            }).Every(16);
            MarkDirtyRepaint();
        }

        public void Stop() => _animation?.Pause();

        private void Draw(MeshGenerationContext context)
        {
            Vector2 size = contentRect.size;
            if (size.x <= 0 || size.y <= 0) return;
            Vector2 center = size * 0.5f;
            float burst = Mathf.Sin(_progress * Mathf.PI) * (1f - _progress);
            float rest = _hovered ? 0.48f : _revealed ? (_premium ? 0.18f : 0.07f) : 0f;
            float strength = rest + burst * (_legendary ? 0.85f : 0.6f);
            if (strength <= 0f) return;

            // The fan fades to transparent at its perimeter; there is no solid oval plate.
            MeshWriteData halo = context.Allocate(Segments * 3, Segments * 3);
            ushort index = 0;
            for (int i = 0; i < Segments; i++)
            {
                Vertex(halo, center, Tint(strength), ref index);
                Vertex(halo, center + Ellipse(i, size * 0.5f), Tint(0f), ref index);
                Vertex(halo, center + Ellipse(i + 1, size * 0.5f), Tint(0f), ref index);
            }
            if (_progress >= 1f) return;

            int rays = _legendary ? 14 : _premium ? 10 : 6;
            MeshWriteData light = context.Allocate(rays * 9, rays * 9);
            index = 0;
            float travel = 1f - Mathf.Pow(1f - _progress, 3f);
            for (int i = 0; i < rays; i++)
            {
                float angle = (i * Mathf.PI * 2f / rays) + 0.17f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 side = new Vector2(-direction.y, direction.x);
                Vector2 reach = Vector2.Scale(direction, size * (0.3f + 0.18f * travel));
                Vector2 tip = center + reach;
                float width = size.x * (_legendary ? 0.045f : 0.025f) * (1f - _progress);
                Vertex(light, center + reach * 0.32f, Tint(burst * 0.65f), ref index);
                Vertex(light, tip - side * width, Tint(0f), ref index);
                Vertex(light, tip + side * width, Tint(0f), ref index);

                float radius = size.x * 0.009f * (1f - _progress);
                Color spark = Color.Lerp(_color, Color.white, 0.65f);
                spark.a = Mathf.Sin(_progress * Mathf.PI);
                Vector2 top = tip - Vector2.up * radius * 2.5f;
                Vector2 bottom = tip + Vector2.up * radius * 2.5f;
                Vector2 left = tip - Vector2.right * radius;
                Vector2 right = tip + Vector2.right * radius;
                Vertex(light, top, spark, ref index);
                Vertex(light, right, spark, ref index);
                Vertex(light, bottom, spark, ref index);
                Vertex(light, top, spark, ref index);
                Vertex(light, bottom, spark, ref index);
                Vertex(light, left, spark, ref index);
            }
        }

        private Color Tint(float alpha) => new Color(_color.r, _color.g, _color.b, alpha);

        private static Vector2 Ellipse(int segment, Vector2 radius)
        {
            float angle = segment * Mathf.PI * 2f / Segments;
            return new Vector2(Mathf.Cos(angle) * radius.x, Mathf.Sin(angle) * radius.y);
        }

        private static void Vertex(MeshWriteData mesh, Vector2 point, Color tint, ref ushort index)
        {
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(point.x, point.y, UnityEngine.UIElements.Vertex.nearZ),
                tint = tint,
            });
            mesh.SetNextIndex(index++);
        }
    }
}
