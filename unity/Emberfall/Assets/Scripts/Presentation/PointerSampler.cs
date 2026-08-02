using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>One frame of the abstract pointer (EF-048).</summary>
    public enum PointerPhase
    {
        /// <summary>Nothing is happening this frame.</summary>
        None,
        /// <summary>The pointer went down THIS frame.</summary>
        Pressed,
        /// <summary>The pointer is down and was already down last frame.</summary>
        Held,
        /// <summary>The pointer came up THIS frame.</summary>
        Released,
    }

    /// <summary>One sample frame of the abstract pointer, for testing and injection.</summary>
    public readonly struct PointerSample
    {
        public readonly PointerPhase Phase;
        public readonly Vector2      Position;   // screen pixels, origin bottom-left (Unity convention)

        public PointerSample(PointerPhase phase, Vector2 position)
        {
            Phase    = phase;
            Position = position;
        }
    }

    /// <summary>
    /// Collapses mouse and touch into ONE press/drag/release stream (EF-048). Everything above this
    /// class is input-model-agnostic: <see cref="InputRouter"/> never asks whether it is being driven
    /// by a finger or a mouse, which is what lets desktop drag and Android tap-and-hold-drag be the
    /// same code path rather than two machines that drift.
    ///
    /// <para>THE REASON THIS IS NOT JUST <c>Input.GetMouseButton*</c>: legacy Unity input does
    /// synthesise mouse events from touches (<c>Input.simulateMouseWithTouches</c>, on by default on
    /// Android) — which is why the old <c>OnMouseDown</c> click machine appeared to work on a phone.
    /// What it does NOT give you is a stable per-finger identity across a drag, and the synthetic
    /// mouse-up can land a frame AFTER <c>touchCount</c> returns to zero. Reading both streams
    /// naively therefore double-fires the end of every touch gesture.</para>
    ///
    /// <para>So: while any touch is live, the mouse stream is ignored outright, and the frames
    /// immediately after a touch ends swallow the synthetic mouse tail.</para>
    ///
    /// <para>For testing: <see cref="QueueSyntheticFrame"/> allows the Editor test harness to feed a
    /// scripted frame sequence, overriding Input readings for headless gesture verification.</para>
    /// </summary>
    public sealed class PointerSampler
    {
        private int     _fingerId = -1;      // -1 = not tracking a finger
        private Vector2 _last;
        private bool    _mouseSuppressed;    // a touch owned the pointer; ignore the synthetic tail
        private bool    _pendingMouseRelease;// down+up arrived in the SAME frame (fast click)

        private System.Collections.Generic.Queue<PointerSample> _syntheticFrames;  // test-only queue

        public PointerSample Sample()
        {
            // Test injection: if synthetic frames are queued, drain them instead of reading Input.
            if (_syntheticFrames != null && _syntheticFrames.Count > 0)
                return _syntheticFrames.Dequeue();

            if (Input.touchCount > 0 || _fingerId >= 0)
            {
                _mouseSuppressed = true;
                return SampleTouch();
            }

            if (_mouseSuppressed)
            {
                // Hold the mouse stream off until it is genuinely idle. Anything still reading as
                // pressed here is the synthetic tail of the touch we just finished, and replaying it
                // would release the same gesture twice.
                if (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0))
                    return new PointerSample(PointerPhase.None, _last);
                _mouseSuppressed = false;
            }

            return SampleMouse();
        }

        /// <summary>Drops any in-flight gesture (focus loss, UI taking over). The next press starts clean.</summary>
        public void Reset()
        {
            _fingerId            = -1;
            _pendingMouseRelease = false;
        }

        /// <summary>
        /// EDITOR/TEST ONLY: inject a scripted frame sequence, overriding Input readings for headless
        /// verification (EF-048 InputGestureVerify). Drains from the queue in order; once depleted,
        /// Sample() resumes reading Input normally.
        /// </summary>
        public void QueueSyntheticFrames(params PointerSample[] frames)
        {
            if (_syntheticFrames == null)
                _syntheticFrames = new System.Collections.Generic.Queue<PointerSample>();
            foreach (var frame in frames)
                _syntheticFrames.Enqueue(frame);
        }

        private PointerSample SampleTouch()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);

                if (_fingerId < 0)
                {
                    // Not tracking yet: the FIRST finger to begin owns the gesture. A second finger
                    // landing mid-drag is ignored rather than hijacking the aim.
                    if (t.phase != TouchPhase.Began) continue;
                    _fingerId = t.fingerId;
                    _last     = t.position;
                    return new PointerSample(PointerPhase.Pressed, t.position);
                }

                if (t.fingerId != _fingerId) continue;
                _last = t.position;

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    _fingerId = -1;
                    return new PointerSample(PointerPhase.Released, t.position);
                }
                return new PointerSample(PointerPhase.Held, t.position);
            }

            // The tracked finger disappeared without ever reporting Ended — app focus loss and
            // OS gesture interception both do this. Synthesise the release, or the drag strands
            // armed forever with no way for the player to clear it.
            if (_fingerId >= 0)
            {
                _fingerId = -1;
                return new PointerSample(PointerPhase.Released, _last);
            }
            return new PointerSample(PointerPhase.None, _last);
        }

        private PointerSample SampleMouse()
        {
            Vector3 mouse = Input.mousePosition;
            _last = new Vector2(mouse.x, mouse.y);

            // Deferred from a same-frame down+up. Must be answered BEFORE this frame's edges, or a
            // rapid double-click swallows the first release.
            if (_pendingMouseRelease)
            {
                _pendingMouseRelease = false;
                return new PointerSample(PointerPhase.Released, _last);
            }

            bool down = Input.GetMouseButtonDown(0);
            bool up   = Input.GetMouseButtonUp(0);

            if (down && up)
            {
                _pendingMouseRelease = true;
                return new PointerSample(PointerPhase.Pressed, _last);
            }
            if (down) return new PointerSample(PointerPhase.Pressed,  _last);
            if (up)   return new PointerSample(PointerPhase.Released, _last);
            if (Input.GetMouseButton(0)) return new PointerSample(PointerPhase.Held, _last);
            return new PointerSample(PointerPhase.None, _last);
        }
    }
}
