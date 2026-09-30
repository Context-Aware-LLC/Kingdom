using System;
using System.Collections.Generic;
using Emberfall.Presentation.Input;
using UnityEngine;
using UnityEngine.InputSystem;

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
        /// <summary>The pointer was canceled by the OS (focus loss, system gesture) before release (NOR-137).</summary>
        Canceled,
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
    /// Turns the <c>ScreenActions</c> map into ONE press/drag/release stream (EF-048). Everything
    /// above this class is input-model-agnostic: <see cref="InputRouter"/> never asks whether it is
    /// being driven by a finger or a mouse, which is what lets desktop drag and Android
    /// tap-and-hold-drag be the same code path rather than two machines that drift.
    ///
    /// <para>THERE IS ONLY ONE STREAM, AND THAT IS THE WHOLE POINT. <c>ScreenActions/Press</c> and
    /// <c>ScreenActions/Position</c> bind to <c>&lt;Pointer&gt;</c>, which resolves to mouse, pen,
    /// or the touchscreen's primary touch — whichever is actually present. Do not add a parallel
    /// <c>EnhancedTouch</c> or <c>Touchscreen</c> read to "also handle touch": that device is
    /// already in this stream, so a second reader double-fires the end of every touch gesture, and
    /// the suppression logic needed to undo that is what this class used to be made of.</para>
    ///
    /// <para>A second finger landing mid-drag cannot hijack the gesture, because
    /// <c>&lt;Pointer&gt;/press</c> follows <c>primaryTouch</c> and primary stays with the finger
    /// that started it until that finger lifts.</para>
    ///
    /// <para>For testing: <see cref="QueueSyntheticFrames"/> allows the Editor test harness to feed
    /// a scripted frame sequence, overriding action readings for headless gesture verification.</para>
    /// </summary>
    public sealed class PointerSampler : IDisposable
    {
        private Vector2 _last;
        private bool    _pendingRelease;   // down+up arrived in the SAME frame (fast click or tap)
        private bool    _gestureActive;    // a press is in flight and has not ended yet
        private bool    _enabled;

        private readonly EmberfallActions _actions = new EmberfallActions();

        private Queue<PointerSample> _syntheticFrames;  // test-only queue

        /// <summary>Whether a press/drag gesture is currently in flight.</summary>
        public bool IsTrackingFinger => _gestureActive;

        /// <summary>
        /// Whether the live press is a real finger rather than a mouse or pen. Callers use this to
        /// pick touch-only behaviour (see <c>CardRevealController</c>: only a touch arms the hold).
        /// This asks about the DEVICE, which the unified pointer stream deliberately does not carry.
        /// </summary>
        public bool HasActiveTouch => Touchscreen.current?.primaryTouch.press.isPressed == true;

        public bool IsPressed
        {
            get
            {
                Enable();
                return _actions.ScreenActions.Press.IsPressed();
            }
        }

        public void Enable()
        {
            if (_enabled) return;
            _actions.ScreenActions.Enable();
            _enabled = true;
        }

        public void Disable()
        {
            if (!_enabled) return;
            _actions.ScreenActions.Disable();
            _enabled = false;
            Reset();
        }

        public void Dispose()
        {
            Disable();
            _actions.Dispose();
        }

        public PointerSample Sample()
        {
            // Test injection: if synthetic frames are queued, drain them instead of reading actions.
            if (_syntheticFrames != null && _syntheticFrames.Count > 0)
                return Track(_syntheticFrames.Dequeue());

            Enable();
            return Track(SamplePointer());
        }

        /// <summary>
        /// Keep <see cref="IsTrackingFinger"/> in step with whatever phase we are about to hand out.
        /// Synthetic frames run through here too, so the harness's "gesture fully cleared" checks
        /// assert the real flag rather than one that was never set.
        /// </summary>
        private PointerSample Track(PointerSample sample)
        {
            switch (sample.Phase)
            {
                case PointerPhase.Pressed:
                    _gestureActive = true;
                    break;
                case PointerPhase.Released:
                case PointerPhase.Canceled:
                    _gestureActive = false;
                    break;
            }
            return sample;
        }

        /// <summary>Drops any in-flight gesture (focus loss, UI taking over). The next press starts clean.</summary>
        public void Reset()
        {
            _pendingRelease = false;
            _gestureActive  = false;
        }

        /// <summary>
        /// EDITOR/TEST ONLY: inject a scripted frame sequence, overriding action readings for
        /// headless verification (EF-048 InputGestureVerify). Drains from the queue in order; once
        /// depleted, Sample() resumes reading the actions normally.
        /// </summary>
        public void QueueSyntheticFrames(params PointerSample[] frames)
        {
            if (_syntheticFrames == null)
                _syntheticFrames = new Queue<PointerSample>();
            foreach (var frame in frames)
                _syntheticFrames.Enqueue(frame);
        }

        private PointerSample SamplePointer()
        {
            _last = _actions.ScreenActions.Position.ReadValue<Vector2>();

            // Deferred from a same-frame down+up. Must be answered BEFORE this frame's edges, or a
            // rapid double-click swallows the first release.
            if (_pendingRelease)
            {
                _pendingRelease = false;
                return new PointerSample(PointerPhase.Released, _last);
            }

            bool down = _actions.ScreenActions.Press.WasPressedThisFrame();
            bool up   = _actions.ScreenActions.Press.WasReleasedThisFrame();

            if (up && WasCanceledByOs())
                return new PointerSample(PointerPhase.Canceled, _last);

            if (down && up)
            {
                _pendingRelease = true;
                return new PointerSample(PointerPhase.Pressed, _last);
            }
            if (down) return new PointerSample(PointerPhase.Pressed,  _last);
            if (up)   return new PointerSample(PointerPhase.Released, _last);
            if (_actions.ScreenActions.Press.IsPressed()) return new PointerSample(PointerPhase.Held, _last);
            return new PointerSample(PointerPhase.None, _last);
        }

        /// <summary>
        /// Distinguish "the player lifted their finger" from "the OS took the touch away" (NOR-137:
        /// a system gesture or focus loss mid-drag must not complete the drag). The action stream
        /// reports both as a plain release, so the touch phase is the only thing that still carries
        /// the difference — and only a touchscreen has one, which is why a mouse release is never a
        /// cancel.
        /// </summary>
        private static bool WasCanceledByOs()
            => Touchscreen.current != null
               && Touchscreen.current.primaryTouch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Canceled;
    }
}
