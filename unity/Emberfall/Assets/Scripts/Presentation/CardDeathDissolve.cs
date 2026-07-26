using System;
using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Burns a whole dying card away — art, frame, gem, keyword icons and every TMP label at once —
    /// by snapshotting the card's visual subtree to a RenderTexture, hiding the real hierarchy, and
    /// running that single image through SG_Dissolve (M_Dissolve).
    ///
    /// WHY A SNAPSHOT. A card is ~13 renderers across three material families (SG_CardComposite on
    /// the body quad, URP/Unlit on the art quad, sprite-default on four frame sprites, TMP's SDF
    /// shader on six labels). There is no per-renderer material swap that dissolves all of them
    /// coherently — the burn edge has to cross the composite, not each layer independently. One
    /// flattened texture makes the card a single surface for the length of the effect, which is the
    /// only moment it is safe to stop being interactive geometry.
    ///
    /// LAYER DISCIPLINE. The capture camera sees only the "CardCapture" layer (slot 8). The card's
    /// subtree is moved onto it, rendered, and moved back inside ONE synchronous call, so the board
    /// camera — which renders at end of frame, long after this returns — never observes the change.
    /// That is deliberately different from a persistent capture layer excluded from the main
    /// camera's culling mask: with a synchronous restore the failure mode of a leaked layer is a
    /// card that keeps rendering normally, rather than a card that silently disappears.
    ///
    /// WHERE IT SITS. The quad is planted at the visual's exact world pose — same position, same
    /// rotation, same size — so the swap from real hierarchy to snapshot is invisible on the frame
    /// it happens. M_Dissolve (Emberfall/SG_Dissolve, hand-HLSL) alpha-blends at queue Transparent
    /// with ZWrite off, which is safe here for one specific reason: board cards never overlap, and
    /// a minion only ever dies on the board. Anything that dies while overlapping would need the
    /// depth-vs-sort-order argument re-made from scratch.
    /// </summary>
    public sealed class CardDeathDissolve : MonoBehaviour
    {
        /// <summary>Layer the capture camera renders, and nothing else does. Slot 8 in TagManager.</summary>
        public const string CaptureLayerName = "CardCapture";

        // A board card is 137x194 px on screen (ortho 4.6 => ~117 px/world unit). 256 wide is a
        // ~1.9x supersample, which keeps the burn edge from aliasing without paying for a full-res
        // target on every death. Height is derived from CardH/CardW so the capture is never
        // stretched — the camera's aspect is set from the same two numbers.
        private const int RtWidth  = 256;
        private const int RtHeight = 361;

        // Far enough back to clear the card's own z-spread (frame at -0.006, gem/icons at -0.012,
        // aegis at +0.01) with room for a hovered card's forward offset, close enough that the
        // ortho near/far window stays tight.
        private const float CamDistance = 3f;

        private static Camera s_captureCam;

        private RenderTexture _rt;
        private Material      _mat;
        private GameObject    _quad;
        private Transform     _visual;
        private Coroutine     _tween;

        // ── Public entry point ──────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot <paramref name="visual"/>, hide it, and burn the snapshot away over
        /// <paramref name="duration"/> seconds. Returns false if the effect could not be staged
        /// (missing layer, missing material, capture failure) — the caller must then fall back to
        /// destroying the card outright, because the visual has NOT been hidden in that case.
        /// <paramref name="onComplete"/> runs after this component has released the RenderTexture
        /// and the material instance, so it is safe for it to destroy the card root.
        /// </summary>
        public bool Play(Transform visual, float duration, Action onComplete)
        {
            // onComplete is deliberately NOT fired on the failure path: the caller's fallback is
            // its own destroy, and firing it here too would destroy the root twice.
            if (!Prepare(visual)) return false;
            _tween = StartCoroutine(Burn(duration, onComplete));
            return true;
        }

        // ── Staging, split out so the offscreen preview can step the sweep by hand ──

        /// <summary>
        /// Capture + hide + build the burn quad, leaving _DissolveAmount at 0. Split from
        /// <see cref="Play"/> so Emberfall/Preview Card Dissolve can drive the real component
        /// through a parameter sweep without a play-mode coroutine.
        /// </summary>
        public bool Prepare(Transform visual)
        {
            if (visual == null) return false;

            Material shared = MaterialCatalog.Get(MaterialCatalog.Dissolve);
            if (shared == null) return false;

            // FAIL SAFE, NOT FAIL VISIBLE. BoardController has already dropped this id from
            // _presenters by the time it calls us, so an exception escaping here would strand a
            // fully-rendered dead minion on the board forever, un-clickable and un-diffable —
            // strictly worse than the blink-out this effect replaces. Any staging failure returns
            // false and the caller destroys the card outright.
            try
            {
                _rt = Capture(visual);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CardDeathDissolve] snapshot failed, card will blink out: {ex.Message}");
                Cleanup();
                return false;
            }
            if (_rt == null) return false;

            _visual = visual;
            _visual.gameObject.SetActive(false);

            // An explicit instance, never renderer.material (which warns and leaks in edit mode)
            // and never sharedMaterial (which is the project asset every dying card shares).
            _mat = new Material(shared) { name = shared.name + " (dissolve instance)" };
            _mat.SetTexture(ShaderIds.BaseMap, _rt);
            _mat.SetFloat(ShaderIds.DissolveAmount, 0f);

            // Slide this card's noise field so a board wipe does not burn every card through
            // holes in identical places. Derived from world position rather than Random so a
            // given board slot always burns the same way — reproducible for the offscreen sweep,
            // and different for every slot, which is the only property the effect needs.
            Vector3 at = visual.position;
            _mat.SetVector(ShaderIds.NoiseOffset,
                new Vector4(at.x * 7.31f + at.y * 3.17f, at.y * 5.77f - at.x * 2.13f, 0f, 0f));

            _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad.name  = "DeathDissolve";
            _quad.layer = gameObject.layer;
            DestroyObj(_quad.GetComponent<MeshCollider>());
            _quad.transform.SetParent(transform, false);
            _quad.transform.SetPositionAndRotation(visual.position, visual.rotation);
            _quad.transform.localScale = new Vector3(BoardController.CardW, BoardController.CardH, 1f);
            _quad.GetComponent<Renderer>().sharedMaterial = _mat;
            return true;
        }

        /// <summary>Drives the burn directly. 0 = whole card, 1 = fully gone.</summary>
        public void SetAmount(float amount)
        {
            if (_mat != null) _mat.SetFloat(ShaderIds.DissolveAmount, Mathf.Clamp01(amount));
        }

        /// <summary>Releases the capture target and the material instance. Idempotent.</summary>
        public void Cleanup()
        {
            if (_tween != null) { StopCoroutine(_tween); _tween = null; }
            if (_rt != null)  { _rt.Release(); DestroyObj(_rt); _rt = null; }
            if (_mat != null) { DestroyObj(_mat); _mat = null; }
            if (_quad != null) { DestroyObj(_quad); _quad = null; }
        }

        private void OnDestroy() => Cleanup();

        // ── Burn ────────────────────────────────────────────────────────────────────

        private IEnumerator Burn(float duration, Action onComplete)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                SetAmount(elapsed / duration);
                yield return null;
            }
            SetAmount(1f);
            _tween = null;
            Cleanup();
            onComplete?.Invoke();
        }

        // ── Capture ─────────────────────────────────────────────────────────────────

        private static RenderTexture Capture(Transform visual)
        {
            int layer = LayerMask.NameToLayer(CaptureLayerName);
            if (layer < 0)
            {
                Debug.LogWarning($"[CardDeathDissolve] layer '{CaptureLayerName}' missing from " +
                                 "TagManager — card cannot be snapshotted, dying cards will blink out.");
                return null;
            }

            var rt = new RenderTexture(RtWidth, RtHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();

            // Includes inactive children so the restore is total — the art quad is inactive
            // whenever a card's illustration has not resolved.
            Transform[] nodes = visual.GetComponentsInChildren<Transform>(true);
            var saved = new int[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                saved[i] = nodes[i].gameObject.layer;
                nodes[i].gameObject.layer = layer;
            }

            Camera cam = CaptureCamera(layer);
            cam.transform.SetPositionAndRotation(visual.position - visual.forward * CamDistance, visual.rotation);
            cam.orthographicSize = BoardController.CardH * 0.5f;
            cam.aspect           = BoardController.CardW / BoardController.CardH;
            cam.targetTexture    = rt;
            try
            {
                cam.Render();
            }
            finally
            {
                cam.targetTexture = null;
                // Synchronous restore is the whole safety argument above: nothing else has
                // rendered between the two loops, so no other camera ever saw the capture layer.
                for (int i = 0; i < nodes.Length; i++)
                    if (nodes[i] != null) nodes[i].gameObject.layer = saved[i];
            }
            return rt;
        }

        private static Camera CaptureCamera(int layer)
        {
            if (s_captureCam != null)
            {
                s_captureCam.cullingMask = 1 << layer;
                return s_captureCam;
            }

            var go = new GameObject("__CardDissolveCapture") { hideFlags = HideFlags.HideAndDontSave };
            s_captureCam = go.AddComponent<Camera>();
            s_captureCam.enabled         = false;   // rendered on demand only, never in the loop
            s_captureCam.orthographic    = true;
            s_captureCam.cullingMask     = 1 << layer;
            s_captureCam.clearFlags      = CameraClearFlags.SolidColor;
            s_captureCam.backgroundColor = Color.clear;
            s_captureCam.nearClipPlane   = 0.05f;
            s_captureCam.farClipPlane    = CamDistance * 2f;
            s_captureCam.allowMSAA       = false;
            s_captureCam.allowHDR        = false;
            return s_captureCam;
        }

        // Edit mode forbids Destroy; the preview harness runs there.
        private static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
