using System.Collections;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>The stamper ends the local turn; the wax remains through the opponent's turn.</summary>
    public sealed class RoyalCouncilSeal : MonoBehaviour, IPointerPressTarget
    {
        // Stamper, wax and both supports share the hourglass perspective, mirrored inward.
        public static Quaternion RestRotation => Quaternion.Euler(RoyalCouncilBoard.PropTilt, 6f, 0f);
        private Transform _wax;
        private Transform _stamp;
        private Renderer _renderer;
        private Renderer _stampRenderer;
        private MaterialPropertyBlock _properties;
        private Vector3 _rest;
        private Vector3 _scale;
        private Vector3 _stampRest;
        private Quaternion _stampRotation;
        private Quaternion _stampPressRotation;
        private InputRouter _input;
        private PlayerId _local;
        private bool _ready, _pressing, _opponentTurn;
        public bool IsReady => _ready && !_pressing && _input != null && !_input.BlocksInput;

        public void Initialize(Transform wax, Transform stamper)
        {
            _wax = wax; _rest = wax.localPosition; _scale = wax.localScale;
            _stamp = stamper; _stampRest = stamper.position; _stampRotation = stamper.rotation;
            _stampPressRotation = Quaternion.FromToRotation(stamper.up, wax.up) * _stampRotation;
            _renderer = wax.GetComponent<Renderer>();
            _stampRenderer = stamper.GetComponent<Renderer>();
            _properties = new MaterialPropertyBlock();
            Bounds b = _stampRenderer.bounds;
            transform.position = b.center;
            var hit = gameObject.AddComponent<BoxCollider>();
            hit.size = new Vector3(Mathf.Max(1.1f, b.size.x * 1.2f), Mathf.Max(1.0f, b.size.y * 1.2f), b.size.z + 0.12f);
            UiSfx.Preload(UiSfx.WaxStamp);
            Show(false);
        }

        public void Bind(PlayerId local, InputRouter input) { _local = local; _input = input; }
        public void OnStateUpdated(GameState state)
        {
            _ready = _input != null && state.ActivePlayer == _local && state.Phase == TurnPhase.Main && state.Outcome == MatchOutcome.InProgress;
            _opponentTurn = state.ActivePlayer != _local && state.Phase == TurnPhase.Main && state.Outcome == MatchOutcome.InProgress;
            if (!_pressing) Show(_ready);
        }
        void IPointerPressTarget.OnPointerDown()
        {
            if (!IsReady) return;
            StartCoroutine(Press());
        }
        private IEnumerator Press()
        {
            _pressing = true;
            Vector3 normal = _wax.up;
            Bounds waxBounds = _wax.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 waxSurface = _wax.TransformPoint(new Vector3(waxBounds.center.x, waxBounds.max.y, waxBounds.center.z));
            // Work in the inclined wax plane so the face lands flat and lifts away normally.
            Bounds stampBounds = _stamp.GetComponent<MeshFilter>().sharedMesh.bounds;
            _stamp.rotation = _stampPressRotation;
            Vector3 faceOffset = _stamp.TransformVector(new Vector3(stampBounds.center.x, stampBounds.min.y, stampBounds.center.z));
            _stamp.rotation = _stampRotation;
            Vector3 target = waxSurface + normal * 0.02f - faceOffset;
            Vector3 lifted = target + normal * 0.75f;
            yield return MoveStamp(_stampRest, lifted, 0.22f, _stampPressRotation);
            yield return MoveStamp(lifted, target, 0.12f, _stampPressRotation);
            _renderer.enabled = true;
            _input.OnEndTurnClicked();
            UiSfx.Play(UiSfx.WaxStamp);
            for (float t = 0; t < 0.18f; t += Time.unscaledDeltaTime)
            {
                float press = Mathf.Sin(t / 0.18f * Mathf.PI);
                _wax.localScale = Vector3.Scale(_scale, new Vector3(1f + press * 0.035f, 1f - press * 0.14f, 1f + press * 0.035f));
                yield return null;
            }
            _wax.localPosition = _rest; _wax.localScale = _scale;
            yield return MoveStamp(target, lifted, 0.14f, _stampPressRotation);
            yield return MoveStamp(lifted, _stampRest, 0.25f, _stampRotation);
            _stamp.rotation = _stampRotation;
            yield return new WaitForSecondsRealtime(0.35f);
            _pressing = false; Show(_ready);
        }
        private IEnumerator MoveStamp(Vector3 from, Vector3 to, float duration, Quaternion rotation)
        {
            Quaternion fromRotation = _stamp.rotation;
            for (float t = 0; t < duration; t += Time.unscaledDeltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / duration);
                _stamp.position = Vector3.Lerp(from, to, k);
                _stamp.rotation = Quaternion.Slerp(fromRotation, rotation, k);
                yield return null;
            }
            _stamp.position = to;
            _stamp.rotation = rotation;
        }
        private void Show(bool ready)
        {
            _renderer.enabled = _opponentTurn;
            _stampRenderer.GetPropertyBlock(_properties);
            // glTFast uses baseColorFactor; the URP Lit material uses _BaseColor. Both are
            // assigned so re-importing the model cannot silently lose turn feedback.
            Color color = ready ? Color.white : new Color(0.60f, 0.55f, 0.56f);
            _properties.SetColor("baseColorFactor", color);
            _properties.SetColor("_BaseColor", color);
            _stampRenderer.SetPropertyBlock(_properties);
        }
    }
}
