using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Bubbles a ring of venom around a board minion's attack socket. The three sprites are three
    /// drawings of the same ring, stacked and cross-faded in a loop, so the bubbles swell and burst
    /// in place instead of sliding. Aura ownership remains in <see cref="CardMaterialBinder"/>.
    /// </summary>
    public sealed class VenomBubbleView : MonoBehaviour
    {
        internal const float EntranceDuration = 0.30f;
        internal const float FrameSeconds = 0.45f;

        // Above 1 each drawing holds at full strength before it gives way to the next, and the
        // two overlap during the handoff; at exactly 1 the ring dims to half at every crossing.
        private const float FrameHold = 1.6f;

        private readonly SpriteRenderer[] _frames = new SpriteRenderer[3];
        private readonly Vector3[] _scales = new Vector3[3];
        private float _entrance;
        private float _phase;
        private bool _visible;

        public void Setup(SpriteRenderer first, SpriteRenderer second, SpriteRenderer third)
        {
            _frames[0] = first;
            _frames[1] = second;
            _frames[2] = third;
            for (int i = 0; i < _frames.Length; i++)
                _scales[i] = _frames[i] != null ? _frames[i].transform.localScale : Vector3.one;
            // Two venomous minions side by side should not bubble in step.
            _phase = (GetHashCode() & 255) * (FrameSeconds * 3f / 255f);

            SetFramesEnabled(false);
            enabled = false;
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;

            if (!visible)
            {
                SetFramesEnabled(false);
                enabled = false;
                return;
            }

            _entrance = 0f;
            SetFramesEnabled(true);
            enabled = true;
            ApplyPose(0f, 0f);
        }

        private void Update()
        {
            _entrance = Mathf.MoveTowards(_entrance, 1f, Time.deltaTime / EntranceDuration);
            ApplyPose(Mathf.SmoothStep(0f, 1f, _entrance), Time.time + _phase);
        }

        private void ApplyPose(float entrance, float time)
        {
            float cycle = Mathf.Repeat(time / FrameSeconds, _frames.Length);
            float scale = Mathf.Lerp(0.78f, 1f, entrance);
            for (int i = 0; i < _frames.Length; i++)
            {
                SpriteRenderer frame = _frames[i];
                if (frame == null) continue;

                float distance = Mathf.Abs(cycle - i);
                distance = Mathf.Min(distance, _frames.Length - distance);
                float weight = Mathf.Clamp01((1f - distance) * FrameHold);

                frame.transform.localScale = _scales[i] * scale;
                Color color = frame.color;
                color.a = entrance * weight;
                frame.color = color;
            }
        }

        private void SetFramesEnabled(bool value)
        {
            foreach (SpriteRenderer frame in _frames)
                if (frame != null) frame.enabled = value;
        }
    }
}
