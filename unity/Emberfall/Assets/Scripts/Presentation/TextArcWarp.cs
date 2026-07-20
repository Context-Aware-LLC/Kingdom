using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Bends a TMP text's baseline along a circular arc (middle raised, ends
    /// dipped, glyphs rotated to the tangent) — the loading-screen wordmark
    /// arching over the orb. Pure vertex warp; no layout or material changes.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class TextArcWarp : MonoBehaviour
    {
        [SerializeField, Range(0f, 90f)]
        [Tooltip("Total angular sweep of the text along the arc, in degrees. 0 = flat.")]
        private float arcDegrees = 60f; // wordmark value, Sam-approved 2026-07-19

        private TMP_Text text;

        public float ArcDegrees
        {
            get => arcDegrees;
            set
            {
                arcDegrees = Mathf.Clamp(value, 0f, 90f);
                Rewarp();
            }
        }

        private void OnEnable()
        {
            text = GetComponent<TMP_Text>();
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
            Rewarp();
        }

        private void OnDisable()
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (isActiveAndEnabled)
                Rewarp();
        }
#endif

        private void OnTextChanged(Object changed)
        {
            if (changed == text)
                Warp();
        }

        /// <summary>Regenerate the mesh from layout, then apply the arc.</summary>
        public void Rewarp()
        {
            if (text == null)
                text = GetComponent<TMP_Text>();
            if (text == null)
                return;
            text.ForceMeshUpdate();
            Warp();
        }

        private void Warp()
        {
            if (arcDegrees <= 0f || text == null)
                return;

            TMP_TextInfo info = text.textInfo;
            if (info.characterCount == 0)
                return;

            // Arc geometry: the visible text width maps onto arcDegrees of a
            // circle whose radius follows (arc length ≈ chord for small sweeps).
            Bounds bounds = text.textBounds;
            float width = bounds.size.x;
            if (width <= 0f)
                return;

            float arcRad = arcDegrees * Mathf.Deg2Rad;
            float radius = width / arcRad;
            float centerX = bounds.center.x;

            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (!ch.isVisible)
                    continue;

                int mi = ch.materialReferenceIndex;
                int vi = ch.vertexIndex;
                Vector3[] verts = info.meshInfo[mi].vertices;

                float midX = (ch.bottomLeft.x + ch.bottomRight.x) * 0.5f;
                float baseY = ch.baseLine;
                float t = (midX - centerX) / width;          // [-0.5 .. 0.5]
                float theta = t * arcRad;

                // Pivot each glyph at its baseline midpoint, rotate to the arc
                // tangent, then lift onto the circle (center char stays put).
                Vector3 pivot = new Vector3(midX, baseY, 0f);
                Vector3 target = new Vector3(
                    centerX + radius * Mathf.Sin(theta),
                    baseY + radius * (Mathf.Cos(theta) - 1f),
                    0f);
                Quaternion rot = Quaternion.Euler(0f, 0f, -theta * Mathf.Rad2Deg);

                for (int v = 0; v < 4; v++)
                    verts[vi + v] = target + rot * (verts[vi + v] - pivot);
            }

            text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
        }
    }
}
