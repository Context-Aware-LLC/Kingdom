using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Owns the live board camera's authored perspective pose and fits the logical board box into
    /// the device safe area. The camera rotation and field of view stay fixed; fitting only moves
    /// the camera in its own right/up/back axes, so the approved tabletop perspective does not
    /// change between devices.
    /// </summary>
    public static class BoardCameraRig
    {
        public const float FieldOfView = 36f;
        public const float NearClip = 0.1f;
        public const float FarClip = 80f;

        // The exact-bounds fit felt crowded once the larger hero cluster and vertical mana rail
        // were on the live board. Keep a small authored pull-back even on an inset-free 16:9
        // screen; safe-area fitting may add more retreat, but never removes this breathing room.
        public const float CompositionRetreat = 2f;

        // The prototype's approved camera. Keep this single-sourced: the live scene configures
        // itself from these values at runtime rather than carrying a second YAML copy.
        public static readonly Vector3 AuthoredPosition = new(0f, -5.15f, -18f);
        public static readonly Vector3 LookTarget = new(0f, 0.35f, 0.45f);

        // Cards and front-most HUD furniture step toward the camera on negative Z. Including that
        // depth in the fit prevents the face of a raised edge card from clearing the safe area
        // while its anchor at z=0 technically remains inside it.
        public const float DesignFrontZ = -0.75f;
        public const float DesignBackZ = 0f;

        private const float MaxRetreat = 128f;
        private const float FeasibilityEpsilon = 0.00001f;

        public readonly struct FramingSolution
        {
            public readonly Vector3 CameraLocalOffset;
            public readonly float Retreat;
            public readonly Rect SafeViewport;
            public readonly bool IsIdentity;

            public FramingSolution(
                Vector3 cameraLocalOffset,
                float retreat,
                Rect safeViewport,
                bool isIdentity)
            {
                CameraLocalOffset = cameraLocalOffset;
                Retreat = retreat;
                SafeViewport = safeViewport;
                IsIdentity = isIdentity;
            }
        }

        /// <summary>Apply the approved perspective pose before safe-area fitting.</summary>
        public static void Configure(Camera camera)
        {
            if (camera == null) return;

            camera.orthographic = false;
            camera.fieldOfView = FieldOfView;
            camera.nearClipPlane = NearClip;
            camera.farClipPlane = FarClip;
            camera.depthTextureMode |= DepthTextureMode.Depth;
            camera.transform.SetPositionAndRotation(AuthoredPosition, AuthoredRotation);
        }

        /// <summary>
        /// Fit the logical Kingdom board bounds into the safe viewport without letterboxing.
        /// The solve is analytic for right/up translation and binary only for the minimum camera
        /// retreat. It allocates on safe-area changes, never per frame.
        /// </summary>
        public static FramingSolution ApplySafeArea(
            Camera camera,
            Rect safeAreaPx,
            int screenWidth,
            int screenHeight)
        {
            if (camera == null)
                return new FramingSolution(Vector3.zero, 0f, new Rect(0f, 0f, 1f, 1f), true);

            Configure(camera);

            Rect safeViewport = NormalizeSafeArea(safeAreaPx, screenWidth, screenHeight);
            float aspect = screenWidth > 0 && screenHeight > 0
                ? (float)screenWidth / screenHeight
                : 16f / 9f;
            float tanVertical = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanHorizontal = tanVertical * aspect;

            Vector3[] cameraPoints = DesignCornersInCameraSpace(camera.transform);
            float leftNdc = safeViewport.xMin * 2f - 1f;
            float rightNdc = safeViewport.xMax * 2f - 1f;
            float bottomNdc = safeViewport.yMin * 2f - 1f;
            float topNdc = safeViewport.yMax * 2f - 1f;

            float retreat = CompositionRetreat;
            if (!TryOffsetIntervals(cameraPoints, retreat, tanHorizontal, tanVertical,
                    leftNdc, rightNdc, bottomNdc, topNdc,
                    out _, out _, out _, out _))
            {
                float low = retreat;
                float high = Mathf.Max(retreat + 1f, 1f);
                while (high < MaxRetreat &&
                       !TryOffsetIntervals(cameraPoints, high, tanHorizontal, tanVertical,
                           leftNdc, rightNdc, bottomNdc, topNdc,
                           out _, out _, out _, out _))
                    high *= 2f;

                high = Mathf.Min(high, MaxRetreat);
                for (int i = 0; i < 36; i++)
                {
                    float mid = (low + high) * 0.5f;
                    if (TryOffsetIntervals(cameraPoints, mid, tanHorizontal, tanVertical,
                            leftNdc, rightNdc, bottomNdc, topNdc,
                            out _, out _, out _, out _))
                        high = mid;
                    else
                        low = mid;
                }

                retreat = high;
            }

            if (!TryOffsetIntervals(cameraPoints, retreat, tanHorizontal, tanVertical,
                    leftNdc, rightNdc, bottomNdc, topNdc,
                    out float minX, out float maxX, out float minY, out float maxY))
            {
                Debug.LogError("[BoardCameraRig] Could not fit the board into the safe area; "
                               + "using the authored camera pose.");
                return new FramingSolution(Vector3.zero, 0f, safeViewport, true);
            }

            // Preserve the authored centre whenever it is inside the feasible interval. An
            // asymmetric inset moves only the axis it actually constrains.
            float offsetX = Mathf.Clamp(0f, minX, maxX);
            float offsetY = Mathf.Clamp(0f, minY, maxY);
            var localOffset = new Vector3(offsetX, offsetY, -retreat);

            camera.transform.SetPositionAndRotation(
                AuthoredPosition + AuthoredRotation * localOffset,
                AuthoredRotation);

            bool identity = retreat < 0.0001f
                            && Mathf.Abs(offsetX) < 0.0001f
                            && Mathf.Abs(offsetY) < 0.0001f;
            return new FramingSolution(localOffset, retreat, safeViewport, identity);
        }

        public static Quaternion AuthoredRotation =>
            Quaternion.LookRotation(LookTarget - AuthoredPosition, Vector3.up);

        private static Rect NormalizeSafeArea(Rect safeAreaPx, int width, int height)
        {
            if (width <= 0 || height <= 0 || safeAreaPx.width <= 1f || safeAreaPx.height <= 1f)
                return new Rect(0f, 0f, 1f, 1f);

            float xMin = Mathf.Clamp01(safeAreaPx.xMin / width);
            float xMax = Mathf.Clamp01(safeAreaPx.xMax / width);
            float yMin = Mathf.Clamp01(safeAreaPx.yMin / height);
            float yMax = Mathf.Clamp01(safeAreaPx.yMax / height);
            if (xMax - xMin <= 0.0001f || yMax - yMin <= 0.0001f)
                return new Rect(0f, 0f, 1f, 1f);

            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static Vector3[] DesignCornersInCameraSpace(Transform cameraTransform)
        {
            var points = new Vector3[8];
            int index = 0;
            foreach (float z in new[] { DesignFrontZ, DesignBackZ })
            foreach (float y in new[] { -DeviceLayout.DesignHalfHeight, DeviceLayout.DesignHalfHeight })
            foreach (float x in new[] { -DeviceLayout.DesignHalfWidth, DeviceLayout.DesignHalfWidth })
                points[index++] = cameraTransform.InverseTransformPoint(new Vector3(x, y, z));
            return points;
        }

        private static bool TryOffsetIntervals(
            Vector3[] points,
            float retreat,
            float tanHorizontal,
            float tanVertical,
            float leftNdc,
            float rightNdc,
            float bottomNdc,
            float topNdc,
            out float minOffsetX,
            out float maxOffsetX,
            out float minOffsetY,
            out float maxOffsetY)
        {
            minOffsetX = float.NegativeInfinity;
            maxOffsetX = float.PositiveInfinity;
            minOffsetY = float.NegativeInfinity;
            maxOffsetY = float.PositiveInfinity;

            foreach (Vector3 point in points)
            {
                float depth = point.z + retreat;
                if (depth <= NearClip) return false;

                minOffsetX = Mathf.Max(minOffsetX,
                    point.x - rightNdc * depth * tanHorizontal);
                maxOffsetX = Mathf.Min(maxOffsetX,
                    point.x - leftNdc * depth * tanHorizontal);
                minOffsetY = Mathf.Max(minOffsetY,
                    point.y - topNdc * depth * tanVertical);
                maxOffsetY = Mathf.Min(maxOffsetY,
                    point.y - bottomNdc * depth * tanVertical);
            }

            return minOffsetX <= maxOffsetX + FeasibilityEpsilon
                   && minOffsetY <= maxOffsetY + FeasibilityEpsilon;
        }
    }
}
