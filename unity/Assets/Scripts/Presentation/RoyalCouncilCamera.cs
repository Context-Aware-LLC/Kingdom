using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Fits the physical table and held cards, including depth, inside the safe area.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class RoyalCouncilCamera : MonoBehaviour
    {
        public static readonly Bounds PlayBounds = new Bounds(new Vector3(0f, 0f, -0.65f), new Vector3(22.0f, 12.7f, 3.9f));
        private const float BoardViewScale = 1.05f;
        private static readonly Quaternion ViewRotation = Quaternion.LookRotation(new Vector3(0f, 0.34f, 1f), Vector3.up);
        private Camera _camera;
        private int _width, _height;
        private Rect _safe;

        private void Awake() { _camera = GetComponent<Camera>(); Apply(); }
        private void Update()
        {
            if (_width != Screen.width || _height != Screen.height || _safe != Screen.safeArea) Apply();
        }

        public void Apply()
        {
            _width = Screen.width; _height = Screen.height; _safe = Screen.safeArea;
            Fit(_camera, _safe, _width, _height);
        }

        public static void Fit(Camera camera, Rect safe, int width, int height)
        {
            if (camera == null) return;
            camera.orthographic = false;
            camera.fieldOfView = 34f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 80f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.028f, 0.018f, 0.035f);
            if (width < 1 || height < 1) return;
            if (safe.width < 1 || safe.height < 1) safe = new Rect(0, 0, width, height);
            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * width / height;
            float halfX = safe.width / width * 0.97f;
            float halfY = safe.height / height * 0.97f;
            float centerX = (safe.center.x / width - 0.5f) * 2f;
            float centerY = (safe.center.y / height - 0.5f) * 2f;
            Vector3 center = PlayBounds.center;
            Quaternion inverse = Quaternion.Inverse(ViewRotation);
            // Bounds are small and fixed: solve one camera-space interval per corner. Offset
            // scales with depth, so asymmetric notches remain safe at every aspect ratio.
            float distance = 1f;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 p = inverse * Vector3.Scale(PlayBounds.extents, new Vector3(x, y, z));
                distance = Mathf.Max(distance, Mathf.Abs(p.x - centerX * tanX * p.z) / (halfX * tanX) - p.z);
                distance = Mathf.Max(distance, Mathf.Abs(p.y - centerY * tanY * p.z) / (halfY * tanY) - p.z);
            }
            camera.transform.SetPositionAndRotation(center + ViewRotation * new Vector3(-centerX * distance * tanX, -centerY * distance * tanY, -distance), ViewRotation);
            // Enlarge the complete view together: card geometry, spacing and hit targets
            // stay in the same world positions while the board uses more of the screen.
            camera.fieldOfView = 2f * Mathf.Atan(tanY / BoardViewScale) * Mathf.Rad2Deg;
            // Use the spare space above the table so the enlarged lower rail keeps a margin
            // on wide phones. This moves the framing up by 2.5% of the safe-area height.
            camera.transform.position -= camera.transform.up * (distance * tanY / BoardViewScale * 0.05f * safe.height / height);
        }
    }
}
