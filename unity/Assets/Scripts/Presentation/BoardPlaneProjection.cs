using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Converts pointer coordinates into positions on a plane parallel to the gameplay board.
    /// Ray-plane intersection works for both the shipping orthographic camera and the angled
    /// perspective camera; <c>ScreenToWorldPoint</c> with a guessed depth does not once the camera
    /// rotates away from the board normal.
    /// </summary>
    public static class BoardPlaneProjection
    {
        /// <summary>
        /// Intersects <paramref name="screenPosition"/> with the world plane at
        /// <paramref name="planeZ"/>. Returns false when the camera is absent or its ray cannot
        /// reach that plane.
        /// </summary>
        public static bool TryScreenToPlane(
            Camera camera,
            Vector2 screenPosition,
            float planeZ,
            out Vector3 worldPosition)
        {
            worldPosition = default;
            if (camera == null) return false;

            var plane = new Plane(Vector3.forward, new Vector3(0f, 0f, planeZ));
            Ray ray = camera.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out float distance) || distance < 0f) return false;

            worldPosition = ray.GetPoint(distance);
            return true;
        }
    }
}
