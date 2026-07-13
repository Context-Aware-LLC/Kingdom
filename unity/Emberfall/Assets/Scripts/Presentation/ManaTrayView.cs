using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space mana tray for one player (board-and-vfx.md §1 ManaTrayAnchor, ui/design-system.md §4.4).
    ///
    /// Renders a quad at ManaTrayAnchor driven by a MaterialPropertyBlock _FillLevel float (0..1,
    /// 10-step quantized) — the shader hook-up point for SG_ManaFill. Until the shader arrives,
    /// a TextMeshPro label ("X / Y") provides the M1 placeholder read-out in mana blue.
    ///
    /// The four ManaPool states (Available / Spent / Locked / Temporary) are all stored on the MPB
    /// so SG_ManaFill can express them as fill + tint without additional binding work.
    ///
    /// Created procedurally by GameBootstrap — one instance per player.
    /// </summary>
    public sealed class ManaTrayView : MonoBehaviour
    {
        // Mana blue — matches design-system.md §4.4 --mana token.
        private static readonly Color ManaBlue   = new Color(0.40f, 0.70f, 1.00f);
        private static readonly Color SpentGrey  = new Color(0.25f, 0.25f, 0.35f);

        private static readonly int FillLevelId  = Shader.PropertyToID("_FillLevel");
        private static readonly int SpentLevelId = Shader.PropertyToID("_SpentLevel");
        private static readonly int LockedLevelId= Shader.PropertyToID("_LockedLevel");
        private static readonly int TempLevelId  = Shader.PropertyToID("_TempLevel");

        private int _seatIndex;
        private Renderer _quad;
        private TextMeshPro _label;
        private MaterialPropertyBlock _mpb;

        /// <summary>
        /// Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// </summary>
        public void Init(int seatIndex, Transform anchor)
        {
            _seatIndex = seatIndex;
            transform.SetParent(anchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            // Tray quad — sized to fill the anchor area. SG_ManaFill will drive this renderer.
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "ManaTrayQuad";
            quadGo.transform.SetParent(transform, false);
            quadGo.transform.localScale    = new Vector3(1.1f, 0.18f, 1f);
            quadGo.transform.localPosition = Vector3.zero;
            Destroy(quadGo.GetComponent<MeshCollider>());
            _quad = quadGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(_quad, MaterialCatalog.ManaFill);
            _mpb  = new MaterialPropertyBlock();

            // Placeholder tint — dark fill until SG_ManaFill is assigned.
            _quad.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", SpentGrey);
            _quad.SetPropertyBlock(_mpb);

            // TMP label: "X / Y" — M1 visual read-out, sits just in front of the quad.
            var labelGo = new GameObject("ManaLabel");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            labelGo.transform.localRotation  = Quaternion.identity;

            _label           = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize  = 1.5f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color     = ManaBlue;
        }

        public void OnStateUpdated(GameState state)
        {
            if (_quad == null) return;

            var mana = state.Players[_seatIndex].Mana;
            int cap  = Mathf.Max(1, mana.Max);

            float fill   = (float)mana.Available / cap;
            float spent  = (float)mana.Spent     / cap;
            float locked = (float)mana.Locked    / cap;
            float temp   = (float)mana.Temporary / cap;

            // Write all four states onto the MPB so SG_ManaFill has everything it needs.
            _quad.GetPropertyBlock(_mpb);
            _mpb.SetFloat(FillLevelId,   fill);
            _mpb.SetFloat(SpentLevelId,  spent);
            _mpb.SetFloat(LockedLevelId, locked);
            _mpb.SetFloat(TempLevelId,   temp);
            _quad.SetPropertyBlock(_mpb);

            // M1 placeholder label — "available / max" in mana blue.
            if (_label != null)
                _label.text = $"{mana.Available} / {mana.Max}";
        }
    }
}
