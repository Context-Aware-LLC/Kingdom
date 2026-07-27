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
    /// The flatten itself lives in <see cref="CardSnapshot"/>, which also carries the LAYER
    /// DISCIPLINE argument for the synchronous restore — read it there, next to the loops it
    /// defends. The collection wall bakes its faces through the same call.
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
        public const string CaptureLayerName = CardSnapshot.CaptureLayerName;

        // A board card is 137x194 px on screen (ortho 4.6 => ~117 px/world unit). 256 wide is a
        // ~1.9x supersample, which keeps the burn edge from aliasing without paying for a full-res
        // target on every death. Height is derived from CardH/CardW so the capture is never
        // stretched — the camera's aspect is set from the same two numbers.
        private const int RtWidth  = 256;
        private const int RtHeight = 361;

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
                _rt = CardSnapshot.Capture(visual, RtWidth, RtHeight);
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

        // Edit mode forbids Destroy; the preview harness runs there.
        private static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
