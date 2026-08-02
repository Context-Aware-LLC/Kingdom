using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The drag arrow (EF-048): a tapered line from whatever is being dragged to wherever the pointer
    /// currently is. Built lazily from primitives, the same approach as InputRouter's targeting banner
    /// and the reject toast — no prefab, no scene authoring, nothing to forget to wire.
    ///
    /// <para>WHY AN ARROW AND NOT A CARD THAT FOLLOWS THE FINGER: the hand card's transform is not
    /// ours to move. <c>HandFanLayout</c> rewrites every hand anchor and <c>HandCardHover</c> drives
    /// the visual child every frame, so a drag ghost written into either would fight both and lose
    /// non-deterministically. The arrow is a separate object that owns itself.</para>
    ///
    /// <para>It draws at <see cref="AimZ"/>, nearer the camera than any staged card (the fan stages
    /// 0.03/card and hover pulls 0.40), because the line material is opaque and the depth buffer
    /// overrules sorting order — the same trap documented on HandCardHover.</para>
    /// </summary>
    internal sealed class AimLineView
    {
        /// <summary>In front of the deepest hover pull (0.40) and the whole fan stagger.</summary>
        internal const float AimZ = -1.5f;

        private const float StartWidth = 0.09f;
        private const float EndWidth   = 0.22f;

        private static readonly Color Tint = new Color(0.98f, 0.80f, 0.32f);

        private GameObject   _go;
        private LineRenderer _line;

        internal void Show(Vector3 from, Vector3 to)
        {
            if (_go == null) Build();
            if (_line == null) return;

            from.z = AimZ;
            to.z   = AimZ;
            _line.SetPosition(0, from);
            _line.SetPosition(1, to);
            _go.SetActive(true);
        }

        internal void Hide()
        {
            if (_go != null) _go.SetActive(false);
        }

        internal void Dispose()
        {
            if (_go != null) Object.Destroy(_go);
            _go   = null;
            _line = null;
        }

        private void Build()
        {
            _go = new GameObject("AimLine");
            _line = _go.AddComponent<LineRenderer>();
            _line.useWorldSpace  = true;
            _line.positionCount  = 2;
            _line.startWidth     = StartWidth;
            _line.endWidth       = EndWidth;   // widens toward the pointer, so the arrow reads directional
            _line.numCapVertices = 4;
            _line.alignment      = LineAlignment.View;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows    = false;

            MaterialCatalog.Assign(_line, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            _line.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", Tint);
            _line.SetPropertyBlock(mpb);

            _go.SetActive(false);
        }
    }
}
