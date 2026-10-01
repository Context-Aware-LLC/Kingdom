using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Animates three independent Shroud/Stealth smoke pieces into place around a board minion,
    /// then keeps them moving just enough that the status remains alive without obscuring the card.
    /// Aura ownership remains in <see cref="CardMaterialBinder"/>.
    /// </summary>
    public sealed class ShroudSmokeView : MonoBehaviour
    {
        internal const float EntranceDuration = 0.52f;

        private static readonly Vector3 LeftStartOffset = new Vector3(-0.46f, -0.06f, 0f);
        private static readonly Vector3 RightStartOffset = new Vector3(0.46f, 0.04f, 0f);
        private static readonly Vector3 LowerStartOffset = new Vector3(0f, -0.34f, 0f);

        private SpriteRenderer _left;
        private SpriteRenderer _right;
        private SpriteRenderer _lower;
        private Vector3 _leftHome;
        private Vector3 _rightHome;
        private Vector3 _lowerHome;
        private Vector3 _leftScale;
        private Vector3 _rightScale;
        private Vector3 _lowerScale;
        private float _entrance;
        private float _phase;
        private bool _visible;

        public void Setup(SpriteRenderer left, SpriteRenderer right, SpriteRenderer lower)
        {
            _left = left;
            _right = right;
            _lower = lower;
            _leftHome = left != null ? left.transform.localPosition : Vector3.zero;
            _rightHome = right != null ? right.transform.localPosition : Vector3.zero;
            _lowerHome = lower != null ? lower.transform.localPosition : Vector3.zero;
            _leftScale = left != null ? left.transform.localScale : Vector3.one;
            _rightScale = right != null ? right.transform.localScale : Vector3.one;
            _lowerScale = lower != null ? lower.transform.localScale : Vector3.one;
            _phase = (GetHashCode() & 255) * (Mathf.PI * 2f / 255f);

            SetPiecesEnabled(false);
            enabled = false;
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;

            if (!visible)
            {
                SetPiecesEnabled(false);
                enabled = false;
                return;
            }

            _entrance = 0f;
            SetPiecesEnabled(true);
            enabled = true;
            ApplyPose(0f, 0f);
        }

        private void Update()
        {
            _entrance = Mathf.MoveTowards(_entrance, 1f, Time.deltaTime / EntranceDuration);
            float eased = Mathf.SmoothStep(0f, 1f, _entrance);
            ApplyPose(eased, Time.time + _phase);
        }

        private void ApplyPose(float entrance, float time)
        {
            float drift = entrance;
            if (_left != null)
            {
                _left.transform.localPosition = _leftHome
                    + Vector3.Lerp(LeftStartOffset, Vector3.zero, entrance)
                    + new Vector3(0f, Mathf.Sin(time * 0.83f) * 0.018f * drift, 0f);
                _left.transform.localScale = _leftScale * (1f + Mathf.Sin(time * 0.67f) * 0.008f * drift);
                SetAlpha(_left, entrance * (0.80f + Mathf.Sin(time * 0.91f) * 0.05f));
            }

            if (_right != null)
            {
                _right.transform.localPosition = _rightHome
                    + Vector3.Lerp(RightStartOffset, Vector3.zero, entrance)
                    + new Vector3(0f, Mathf.Sin(time * 0.76f + 1.7f) * 0.021f * drift, 0f);
                _right.transform.localScale = _rightScale * (1f + Mathf.Sin(time * 0.61f + 0.8f) * 0.009f * drift);
                SetAlpha(_right, entrance * (0.78f + Mathf.Sin(time * 0.86f + 1.2f) * 0.05f));
            }

            if (_lower != null)
            {
                // A slight delay makes the side plumes close first and the lower bank gather
                // behind them instead of all three pieces arriving as one rigid rectangle.
                float lowerEntrance = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((entrance - 0.12f) / 0.88f));
                _lower.transform.localPosition = _lowerHome
                    + Vector3.Lerp(LowerStartOffset, Vector3.zero, lowerEntrance)
                    + new Vector3(Mathf.Sin(time * 0.54f) * 0.014f * drift, 0f, 0f);
                _lower.transform.localScale = _lowerScale * (1f + Mathf.Sin(time * 0.58f + 2.1f) * 0.007f * drift);
                SetAlpha(_lower, lowerEntrance * (0.72f + Mathf.Sin(time * 0.72f + 2.4f) * 0.04f));
            }
        }

        private void SetPiecesEnabled(bool value)
        {
            if (_left != null) _left.enabled = value;
            if (_right != null) _right.enabled = value;
            if (_lower != null) _lower.enabled = value;
        }

        private static void SetAlpha(SpriteRenderer renderer, float alpha)
        {
            Color color = renderer.color;
            color.a = Mathf.Clamp01(alpha);
            renderer.color = color;
        }
    }
}
