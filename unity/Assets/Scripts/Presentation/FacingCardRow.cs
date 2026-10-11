using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// One row of cards laid out flat to the camera, centred in a band of the viewport and sized
    /// to fill it. Shared by the two surfaces that present cards head-on over the board: the
    /// Unearth offer (<see cref="UnearthChoiceView"/>) and the mulligan row
    /// (<see cref="MulliganRowLayout"/>, #401).
    ///
    /// <para>The board camera is a tilted perspective rig (<see cref="BoardCameraRig"/>), so a
    /// card lying on the board plane is never flat to the screen. The row therefore sits on a
    /// plane <c>distance</c> in front of the camera and takes the camera's rotation; its card
    /// scale is relative to a board-size card (<see cref="BoardController.CardW"/>).</para>
    /// </summary>
    internal static class FacingCardRow
    {
        internal readonly struct Solution
        {
            public readonly Vector3    Centre;     // world position of the band's centre
            public readonly Quaternion Rotation;   // the camera's rotation: flat to the screen
            public readonly float      CardScale;  // relative to a board-size card
            public readonly float      Step;       // world distance between card centres
            public readonly float      BandWidth;  // world size of the band on the row's plane
            public readonly float      BandHeight;

            public Solution(Vector3 centre, Quaternion rotation, float cardScale, float step,
                            float bandWidth, float bandHeight)
            {
                Centre = centre; Rotation = rotation; CardScale = cardScale; Step = step;
                BandWidth = bandWidth; BandHeight = bandHeight;
            }

            /// <summary>Offset of slot <paramref name="index"/> from the band centre, along the
            /// row's own x axis.</summary>
            public Vector3 SlotOffset(int index, int count)
                => new Vector3((index - (count - 1) * 0.5f) * Step, 0f, 0f);
        }

        /// <summary>The device safe area as a normalised viewport rect; the full screen when
        /// the platform reports none.</summary>
        public static Rect SafeViewport()
        {
            float w = Mathf.Max(1, Screen.width);
            float h = Mathf.Max(1, Screen.height);
            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f) return new Rect(0f, 0f, 1f, 1f);
            return Rect.MinMaxRect(safe.xMin / w, safe.yMin / h, safe.xMax / w, safe.yMax / h);
        }

        /// <param name="band">Normalised viewport rect the row is centred in and sized against.</param>
        /// <param name="widthFraction">Share of the band's width the whole row may take.</param>
        /// <param name="heightFraction">Share of the band's height one card may take.</param>
        public static bool TrySolve(Camera camera, Rect band, float distance, int count, float gap,
                                    float maxScale, float widthFraction, float heightFraction,
                                    out Solution solution)
        {
            solution = default;
            if (camera == null || band.width <= 0f || band.height <= 0f) return false;
            count = Mathf.Max(1, count);

            Vector3 centre = camera.ViewportToWorldPoint(new Vector3(band.center.x, band.center.y, distance));
            Vector3 left   = camera.ViewportToWorldPoint(new Vector3(band.xMin, band.center.y, distance));
            Vector3 right  = camera.ViewportToWorldPoint(new Vector3(band.xMax, band.center.y, distance));
            Vector3 bottom = camera.ViewportToWorldPoint(new Vector3(band.center.x, band.yMin, distance));
            Vector3 top    = camera.ViewportToWorldPoint(new Vector3(band.center.x, band.yMax, distance));
            float bandWidth  = Vector3.Distance(left, right);
            float bandHeight = Vector3.Distance(bottom, top);

            float unscaledWidth = count * BoardController.CardW + (count - 1) * gap;
            float scale = Mathf.Min(maxScale, Mathf.Min(
                widthFraction * bandWidth / unscaledWidth,
                heightFraction * bandHeight / BoardController.CardH));

            solution = new Solution(centre, camera.transform.rotation, scale,
                                    (BoardController.CardW + gap) * scale, bandWidth, bandHeight);
            return true;
        }
    }
}
