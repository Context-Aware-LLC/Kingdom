using System;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Physical sand follows the server's absolute deadline, including timer refreshes.</summary>
    public sealed class RoyalCouncilHourglass : MonoBehaviour
    {
        private Transform _upper, _lower, _stream;
        private Vector3 _upperScale, _lowerScale;
        private Vector3 _upperBase, _lowerBase, _upperPosition, _lowerPosition;
        private Renderer _upperRenderer, _lowerRenderer;
        private MaterialPropertyBlock _properties;
        private Func<long> _now;
        private long? _deadline;
        private long _duration;
        private Quaternion _restRotation;
        private float _flipAt = -10f;
        public float Fraction { get; private set; } = 1f;
        public bool Urgent { get; private set; }

        public void Initialize(RoyalCouncilBoard board, Material glass, Func<long> now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _properties = new MaterialPropertyBlock();
            transform.position = board.Part("RC.Mount.Timer").position;
            foreach (string name in new[] { "RC.Prop.Hourglass", "RC.Prop.Hourglass.Glass", "RC.Prop.Hourglass.Sand.Lower", "RC.Prop.Hourglass.Sand.Stream", "RC.Prop.Hourglass.Sand.Upper" })
                board.Part(name).SetParent(transform, true);
            _upper = board.Part("RC.Prop.Hourglass.Sand.Upper");
            _lower = board.Part("RC.Prop.Hourglass.Sand.Lower");
            _stream = board.Part("RC.Prop.Hourglass.Sand.Stream");
            _upperScale = _upper.localScale; _lowerScale = _lower.localScale;
            _upperPosition = _upper.localPosition; _lowerPosition = _lower.localPosition;
            _upperBase = _upper.GetComponent<MeshFilter>().sharedMesh.bounds.min;
            _lowerBase = _lower.GetComponent<MeshFilter>().sharedMesh.bounds.min;
            _upperRenderer = _upper.GetComponent<Renderer>(); _lowerRenderer = _lower.GetComponent<Renderer>();
            var glassRenderer = board.Part("RC.Prop.Hourglass.Glass").GetComponent<Renderer>();
            glassRenderer.sharedMaterial = glass;
            glassRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            glassRenderer.GetPropertyBlock(_properties);
            _properties.SetColor("_BaseColor", new Color(0.70f, 0.84f, 0.92f, 0.16f));
            _properties.SetFloat("_Rim", 0.48f);
            glassRenderer.SetPropertyBlock(_properties);
            // Tip the physical hourglass toward a readable silhouette in the tabletop camera.
            // Its foot stays seated on the platform; its pivot does not orbit the table.
            _restRotation = Quaternion.Euler(32f, -6f, 0f);
            transform.rotation = _restRotation;
            SetSand(1f, false);
        }

        public void OnTurnTimer(TurnTimer timer)
        {
            // Receiving the same deadline again (reconnect/refresh) must not refill the sand.
            if (_deadline == timer.DeadlineMs) return;
            _deadline = timer.DeadlineMs;
            _duration = Math.Max(1, timer.DeadlineMs - _now());
            _flipAt = Time.unscaledTime;
        }
        public void OnStateUpdated(GameState state)
        {
            if (state.Phase == TurnPhase.Mulligan || state.Outcome != MatchOutcome.InProgress)
            {
                _deadline = null;
                SetSand(1f, false);
            }
        }
        private void Update()
        {
            if (!_deadline.HasValue) return;
            long remaining = Math.Max(0, _deadline.Value - _now());
            SetSand(remaining / (float)_duration, remaining <= 15000);
            float beat = Mathf.Clamp01((Time.unscaledTime - _flipAt) / 0.5f);
            float rock = Mathf.Sin(beat * Mathf.PI) * 8f + (Urgent ? Mathf.Sin(Time.unscaledTime * 10f) * 2f : 0f);
            transform.rotation = Quaternion.Euler(0f, 0f, rock) * _restRotation;
        }

        private void SetSand(float fraction, bool urgent)
        {
            Fraction = Mathf.Clamp01(fraction); Urgent = urgent;
            // Mesh coordinates have their base at the foot; retaining the base while scaling
            // the vertical axis makes the lower heap accumulate instead of float upward.
            _upper.localScale = Vector3.Scale(_upperScale, new Vector3(1f, Mathf.Max(0.015f, Fraction), 1f));
            _lower.localScale = Vector3.Scale(_lowerScale, new Vector3(1f, Mathf.Max(0.015f, 1f - Fraction), 1f));
            _upper.localPosition = _upperPosition + _upper.localRotation * new Vector3(0f, _upperBase.y * (_upperScale.y - _upper.localScale.y), 0f);
            _lower.localPosition = _lowerPosition + _lower.localRotation * new Vector3(0f, _lowerBase.y * (_lowerScale.y - _lower.localScale.y), 0f);
            _upperRenderer.enabled = Fraction > 0.001f;
            _lowerRenderer.enabled = Fraction < 0.999f;
            _stream.gameObject.SetActive(_deadline.HasValue && Fraction > 0.001f);
            Color color = urgent ? new Color(1f, 0.23f, 0.06f) : new Color(0.92f, 0.64f, 0.24f);
            TintSand(_upperRenderer, color);
            TintSand(_lowerRenderer, color);
        }

        private void TintSand(Renderer renderer, Color color)
        {
            renderer.GetPropertyBlock(_properties);
            _properties.SetColor("baseColorFactor", color);
            _properties.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(_properties);
        }
    }
}
