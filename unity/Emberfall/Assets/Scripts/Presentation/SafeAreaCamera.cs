using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-062. Keeps the board inside <c>Screen.safeArea</c> on Android without letterboxing.
    ///
    /// <para>WHY THIS HAS TO EXIST AT RUNTIME AND CANNOT BE A CONSTANT. The whole bottom row —
    /// hero plate, both sockets, the mana rail, the deck pile — rests on
    /// <c>ProceduralBoardSetup.Baseline</c> 4.45, which is 0.15 world units above the screen
    /// edge at 4.6. That is 18 px on a 1080p panel, and it is the ENTIRE vertical inset budget.
    /// Android landscape puts the gesture bar on that same bottom edge and it wants 24–48 dp:
    /// 66–132 px at the 2.75x density of a 1080p 6.3" phone. So the bottom row lands under the
    /// gesture bar on every modern Android device, and by how much is device data that only
    /// <c>Screen.safeArea</c> knows.</para>
    ///
    /// <para>Attached by <c>ProceduralBoardSetup.Awake</c> rather than authored into the scene,
    /// so BoardNetworked.unity picks it up without a YAML edit — and so does any board scene
    /// added later, which is what the two-scene era proved: an authored component is a copy,
    /// and copies drift.</para>
    ///
    /// <para>On a screen with no insets — the Editor, a desktop build, an emulator with the
    /// bars hidden — <see cref="DeviceLayout.Solve"/> returns identity and this component sets
    /// exactly the values already authored in the scene. It is a no-op until a device gives it
    /// something to do, which is also why it must be tested on a device and not in the Editor.</para>
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class SafeAreaCamera : MonoBehaviour
    {
        private Camera _camera;
        private Vector3 _authoredPosition;
        private Rect _lastSafeArea;
        private int _lastWidth;
        private int _lastHeight;
        private bool _logged;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _authoredPosition = transform.position;
            Apply();
        }

        // Orientation changes, multi-window resizes and the navigation bar being shown or
        // hidden all arrive as a new Screen.safeArea with no callback attached to it. Polling
        // three struct reads per frame is cheaper than any alternative and cannot miss one.
        private void Update()
        {
            if (Screen.width == _lastWidth
                && Screen.height == _lastHeight
                && Screen.safeArea == _lastSafeArea)
                return;

            Apply();
        }

        private void Apply()
        {
            if (_camera == null) return;

            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            _lastSafeArea = Screen.safeArea;

            DeviceLayout.Solution s = DeviceLayout.Solve(_lastSafeArea, _lastWidth, _lastHeight);

            _camera.orthographic = true;
            _camera.orthographicSize = s.OrthoSize;
            transform.position = new Vector3(
                _authoredPosition.x + s.CentreOffset.x,
                _authoredPosition.y + s.CentreOffset.y,
                _authoredPosition.z);

            // The backdrop is sized in world units, so zooming out uncovers its edges unless it
            // grows with the camera. It is authored with bleed to aspect 2.2173 and crops at
            // everything narrower, so scaling it by the same factor preserves that coverage.
            BoardBackdrop.SetVisibleHeight(s.VisibleHeight);

            // One line, once, naming the numbers a device run needs to report back. EF-062
            // establishes the aspect range and the inset BUDGET from the geometry; the actual
            // per-device insets are only knowable here, and this is the line that carries them
            // out of a device build and into EF-064's capture.
            if (_logged) return;
            _logged = true;

            float aspect = _lastHeight > 0 ? (float)_lastWidth / _lastHeight : 0f;
            string range = aspect < DeviceLayout.MinAspect
                ? "BELOW MinAspect — zoomed out to fit, unsupported"
                : aspect > DeviceLayout.MaxAspect
                    ? "ABOVE MaxAspect — bare edge past the backdrop bleed"
                    : "in range";

            Debug.Log(
                $"[SafeAreaCamera] screen {_lastWidth}x{_lastHeight} aspect {aspect:F4} ({range}) · "
                + $"safeArea x{_lastSafeArea.x:F0} y{_lastSafeArea.y:F0} "
                + $"w{_lastSafeArea.width:F0} h{_lastSafeArea.height:F0} · "
                + $"insets L{_lastSafeArea.x:F0} "
                + $"R{_lastWidth - _lastSafeArea.xMax:F0} "
                + $"B{_lastSafeArea.y:F0} "
                + $"T{_lastHeight - _lastSafeArea.yMax:F0} · "
                + $"orthoSize {DeviceLayout.DesignHalfHeight:F2}->{s.OrthoSize:F4} "
                + $"offset ({s.CentreOffset.x:F4}, {s.CentreOffset.y:F4}) · "
                + $"card in hand {DeviceLayout.CardPixelWidth(_lastHeight, s.OrthoSize):F1}"
                + $"x{DeviceLayout.CardPixelHeight(_lastHeight, s.OrthoSize):F1} px");
        }
    }
}
