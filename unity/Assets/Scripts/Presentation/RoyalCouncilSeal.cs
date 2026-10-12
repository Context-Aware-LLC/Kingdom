using System.Collections;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>The stamper ends the local turn; the wax remains through the opponent's turn.</summary>
    public sealed class RoyalCouncilSeal : MonoBehaviour, IPointerPressTarget
    {
        private Transform _wax;
        private Transform _stamp;
        private Renderer _renderer;
        private Renderer _stampRenderer;
        private MaterialPropertyBlock _properties;
        private Vector3 _rest;
        private Vector3 _scale;
        private Vector3 _stampRest;
        private Quaternion _stampRotation;
        private InputRouter _input;
        private PlayerId _local;
        private bool _ready, _pressing, _opponentTurn;
        public bool IsReady => _ready && !_pressing && _input != null && !_input.BlocksInput;

        public void Initialize(Transform wax, Transform stamper)
        {
            _wax = wax; _rest = wax.localPosition; _scale = wax.localScale;
            _stamp = stamper; _stampRest = stamper.position; _stampRotation = stamper.rotation;
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
            Vector3 target = _renderer.bounds.center;
            float baseOffset = _stampRenderer.bounds.max.z - _stamp.position.z;
            target.z = _renderer.bounds.min.z - 0.02f - baseOffset;
            Vector3 lifted = target + Vector3.back * 0.75f;
            yield return MoveStamp(_stampRest, lifted, 0.22f);
            yield return MoveStamp(lifted, target, 0.12f);
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
            yield return MoveStamp(target, lifted, 0.14f);
            yield return MoveStamp(lifted, _stampRest, 0.25f);
            _stamp.rotation = _stampRotation;
            yield return new WaitForSecondsRealtime(0.35f);
            _pressing = false; Show(_ready);
        }
        private IEnumerator MoveStamp(Vector3 from, Vector3 to, float duration)
        {
            for (float t = 0; t < duration; t += Time.unscaledDeltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / duration);
                _stamp.position = Vector3.Lerp(from, to, k);
                yield return null;
            }
            _stamp.position = to;
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
