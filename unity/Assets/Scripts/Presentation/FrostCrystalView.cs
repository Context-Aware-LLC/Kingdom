using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Grows three silhouette-breaking crystal pieces around a Frosted board minion. The existing
    /// SG_Frost overlay remains responsible for freezing and thawing the card surface itself.
    /// </summary>
    public sealed class FrostCrystalView : MonoBehaviour
    {
        internal const float EntranceDuration = 0.34f;

        private static readonly Vector3 LeftStartOffset = new Vector3(-0.08f, -0.30f, 0f);
        private static readonly Vector3 RightStartOffset = new Vector3(0.08f, -0.30f, 0f);
        private static readonly Vector3 LowerStartOffset = new Vector3(0f, -0.20f, 0f);

        private SpriteRenderer _left;
        private SpriteRenderer _right;
        private SpriteRenderer _lower;
        private Vector3 _leftHome;
        private Vector3 _rightHome;
        private Vector3 _lowerHome;
        private Vector3 _leftScale;
        private Vector3 _rightScale;
        private Vector3 _lowerScale;
        private Quaternion _leftRotation;
        private Quaternion _rightRotation;
        private Quaternion _lowerRotation;
        private float _entrance;
        private bool _visible;

        public void Setup(SpriteRenderer left, SpriteRenderer right, SpriteRenderer lower)
        {
            _left = left;
            _right = right;
            _lower = lower;
            CacheHome(left, out _leftHome, out _leftScale, out _leftRotation);
            CacheHome(right, out _rightHome, out _rightScale, out _rightRotation);
            CacheHome(lower, out _lowerHome, out _lowerScale, out _lowerRotation);
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
            ApplyPose(0f);
        }

        private void Update()
        {
            _entrance = Mathf.MoveTowards(_entrance, 1f, Time.deltaTime / EntranceDuration);
            ApplyPose(_entrance);
            if (Mathf.Approximately(_entrance, 1f)) enabled = false;
        }

        private void ApplyPose(float entrance)
        {
            // The shelf catches first, then the two asymmetric edge clusters climb in sequence.
            float lowerProgress = Segment(entrance, 0f, 0.72f);
            float leftProgress = Segment(entrance, 0.07f, 0.82f);
            float rightProgress = Segment(entrance, 0.15f, 0.85f);

            Grow(_lower, _lowerHome, _lowerScale, _lowerRotation,
                LowerStartOffset, -1.2f, lowerProgress);
            Grow(_left, _leftHome, _leftScale, _leftRotation,
                LeftStartOffset, -2.4f, leftProgress);
            Grow(_right, _rightHome, _rightScale, _rightRotation,
                RightStartOffset, 2.1f, rightProgress);
        }

        private static float Segment(float entrance, float delay, float duration)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((entrance - delay) / duration));
        }

        private static void Grow(SpriteRenderer renderer, Vector3 home, Vector3 homeScale,
                                 Quaternion homeRotation, Vector3 startOffset,
                                 float startAngle, float progress)
        {
            if (renderer == null) return;

            float crystallineOvershoot = Mathf.Sin(progress * Mathf.PI) * 0.055f;
            float scale = Mathf.Lerp(0.08f, 1f, progress) + crystallineOvershoot;
            renderer.transform.localPosition = home + Vector3.Lerp(startOffset, Vector3.zero, progress);
            renderer.transform.localScale = Vector3.Scale(homeScale, new Vector3(scale, scale, 1f));
            renderer.transform.localRotation = Quaternion.Euler(0f, 0f,
                Mathf.Lerp(startAngle, 0f, progress)) * homeRotation;

            Color color = renderer.color;
            color.a = progress;
            renderer.color = color;
        }

        private static void CacheHome(SpriteRenderer renderer, out Vector3 position,
                                      out Vector3 scale, out Quaternion rotation)
        {
            position = renderer != null ? renderer.transform.localPosition : Vector3.zero;
            scale = renderer != null ? renderer.transform.localScale : Vector3.one;
            rotation = renderer != null ? renderer.transform.localRotation : Quaternion.identity;
        }

        private void SetPiecesEnabled(bool value)
        {
            if (_left != null) _left.enabled = value;
            if (_right != null) _right.enabled = value;
            if (_lower != null) _lower.enabled = value;
        }
    }
}
