using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// A brief, damped translation of the match camera. Because the board and match HUD are
    /// camera-composed, moving the camera makes the death impact read across practically the full
    /// screen while leaving overlay navigation and the match-end controls stable and tappable.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class ScreenEarthquake : MonoBehaviour
    {
        private const float Duration = 0.68f;
        private const float MaxOffset = 0.22f;
        private const float Frequency = 28f;
        private const float PositionEpsilonSquared = 0.000001f;

        private Vector3 _baseLocalPosition;
        private Vector3 _appliedOffset;
        private float _remaining;
        private float _noiseSeed;

        public static void Play(Camera camera)
        {
            if (!Application.isPlaying || camera == null) return;
            ScreenEarthquake earthquake = camera.GetComponent<ScreenEarthquake>();
            if (earthquake == null)
                earthquake = camera.gameObject.AddComponent<ScreenEarthquake>();
            earthquake.Kick();
        }

        private void Awake()
        {
            _baseLocalPosition = transform.localPosition;
        }

        private void Kick()
        {
            CaptureExternalCameraMove();
            _remaining = Duration;
            _noiseSeed = Time.realtimeSinceStartup * 17.13f;
            enabled = true;
        }

        private void LateUpdate()
        {
            CaptureExternalCameraMove();
            if (_remaining <= 0f)
            {
                RestoreBasePosition();
                enabled = false;
                return;
            }

            _remaining = Mathf.Max(0f, _remaining - Time.unscaledDeltaTime);
            float life = _remaining / Duration;
            float amplitude = MaxOffset * life * life;
            float sample = (Duration - _remaining) * Frequency;
            float x = Mathf.PerlinNoise(_noiseSeed, sample) * 2f - 1f;
            float y = Mathf.PerlinNoise(_noiseSeed + 31.7f, sample) * 2f - 1f;
            _appliedOffset = new Vector3(x, y * 0.78f, 0f) * amplitude;
            transform.localPosition = _baseLocalPosition + _appliedOffset;
        }

        private void CaptureExternalCameraMove()
        {
            Vector3 expected = _baseLocalPosition + _appliedOffset;
            Vector3 current = transform.localPosition;
            if ((current - expected).sqrMagnitude > PositionEpsilonSquared)
                _baseLocalPosition = current;
            else
                transform.localPosition = _baseLocalPosition;
            _appliedOffset = Vector3.zero;
        }

        private void OnDisable()
        {
            RestoreBasePosition();
        }

        private void RestoreBasePosition()
        {
            Vector3 expected = _baseLocalPosition + _appliedOffset;
            if ((transform.localPosition - expected).sqrMagnitude <= PositionEpsilonSquared)
                transform.localPosition = _baseLocalPosition;
            _appliedOffset = Vector3.zero;
        }
    }
}
