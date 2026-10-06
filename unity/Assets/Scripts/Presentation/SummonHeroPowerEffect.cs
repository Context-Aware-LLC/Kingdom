using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Plays a 4x4 authored sheet at the already-resolved summon slot. The playback entry
    /// releases while the effect is still visible, so the new minion appears through its settling tail.</summary>
    internal static class SummonHeroPowerEffect
    {
        private const int Columns = 4;
        private const int Rows = 4;
        private const int SortOrder = 82;

        public static IEnumerator Play(Transform parent, Sprite sheet, Transform slot,
                                       float releaseInSeconds)
        {
            const float leadSeconds = 0.92f;
            const float tailSeconds = 0.24f;
            float wait = Mathf.Max(0f, releaseInSeconds - leadSeconds);
            float duration = Mathf.Min(releaseInSeconds, leadSeconds) + tailSeconds;
            if (wait > 0f) yield return new WaitForSeconds(wait);
            if (parent == null || sheet == null || slot == null) yield break;

            Texture2D texture = sheet.texture;
            Rect full = sheet.textureRect;
            var frames = new Sprite[Columns * Rows];
            for (int frame = 0; frame < frames.Length; frame++)
            {
                int col = frame % Columns;
                int row = Rows - 1 - frame / Columns;
                // Boundaries, rather than rounded cell sizes, also work for sheets such as
                // 1254x1254: no final row/column extends outside the texture.
                var rect = Rect.MinMaxRect(
                    full.x + Mathf.Floor(col * full.width / Columns),
                    full.y + Mathf.Floor(row * full.height / Rows),
                    full.x + Mathf.Floor((col + 1) * full.width / Columns),
                    full.y + Mathf.Floor((row + 1) * full.height / Rows));
                frames[frame] = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            }
            var go = new GameObject("HeroPowerSummonEffect");
            go.transform.SetParent(parent, true);
            go.transform.position = slot.position + Vector3.back * 0.06f;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sortingOrder = SortOrder;
            float frameSeconds = duration / (Columns * Rows);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (parent == null || go == null || slot == null) break;
                elapsed += Time.deltaTime;
                int frame = Mathf.Min(Columns * Rows - 1, Mathf.FloorToInt(elapsed / frameSeconds));
                SpriteMaterials.Set(renderer, frames[frame]);
                float scale = 2.15f / Mathf.Max(renderer.sprite.bounds.size.x, renderer.sprite.bounds.size.y);
                go.transform.localScale = Vector3.one * scale;
                go.transform.position = slot.position + Vector3.back * 0.06f;
                float fade = Mathf.InverseLerp(duration * 0.72f, duration, elapsed);
                renderer.color = new Color(1f, 1f, 1f, 1f - fade);
                yield return null;
            }
            foreach (Sprite sprite in frames) Object.Destroy(sprite);
            if (go != null) Object.Destroy(go);
        }
    }
}
