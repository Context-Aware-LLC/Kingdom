using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The herald's scroll around any sheet: two rollers and a clip that unfurls sideways from the
    /// middle and rolls shut again. <see cref="FirstRunTidbit"/> and <see cref="MatchProclamation"/>
    /// both read on it; neither knows how it moves. Styled by <c>tidbit.uss</c>
    /// (<c>.tidbit-scroll</c>, <c>__clip</c>, <c>__roller</c>), which every host already links.
    ///
    /// <para><b>The scroll is built here, not in the markup.</b> The constructor wraps the sheet in
    /// a clip and adds the rollers, so a host's markup carries only the sheet. The sheet is always
    /// laid out at full width and only the clip over it opens — animating the sheet's own width
    /// would re-wrap the copy on every frame. Switching to a vertical unroll means swapping width
    /// for height in <see cref="Apply"/> and turning the roller art.</para>
    /// </summary>
    public sealed class ProclamationScroll
    {
        private const string ScrollClass = "tidbit-scroll";
        private const string ClipClass = "tidbit-scroll__clip";
        private const string RollerClass = "tidbit-scroll__roller";

        // The beat schedule, in ms.
        private const int HoldMs = 160;    // the rolled scroll sits shut before it opens
        private const int UnfurlMs = 520;
        private const int FurlMs = 260;

        // A roller is this much taller than the sheet, so its caps clear the top and bottom edges.
        private const float RollerOverhang = 1.3f;
        // rolled-complete.png is 621 x 1242.
        private const float RollerAspect = 0.5f;

        private enum Motion { None, Opening, Closing }

        private readonly VisualElement _sheet;
        private readonly VisualElement _clip;
        private readonly VisualElement _rollerLeft;
        private readonly VisualElement _rollerRight;
        private readonly IVisualElementScheduledItem _ticker;

        private Motion _motion;
        private float _motionStartedAt;
        // 0 is rolled shut, 1 is fully open.
        private float _open;
        // The unfurl sound starts with the first frame that is drawn, not with the call.
        private bool _unfurlSounded;
        private Action _onFinished;

        /// <summary>
        /// Wrap <paramref name="sheet"/> where it stands in its parent. A sheet already wrapped (a
        /// root bound twice) reuses its scroll rather than nesting a second.
        /// </summary>
        public ProclamationScroll(VisualElement sheet)
        {
            _sheet = sheet ?? throw new ArgumentNullException(nameof(sheet));
            if (sheet.parent == null) throw new ArgumentException("The sheet must already be in a tree.", nameof(sheet));

            if (sheet.parent.ClassListContains(ClipClass))
            {
                _clip = sheet.parent;
                Root = _clip.parent;
                List<VisualElement> rollers = Root.Query<VisualElement>(className: RollerClass).ToList();
                _rollerLeft = rollers[0];
                _rollerRight = rollers[1];
            }
            else
            {
                VisualElement host = sheet.parent;
                int at = host.IndexOf(sheet);
                Root = new VisualElement { name = "tidbit-scroll" };
                Root.AddToClassList(ScrollClass);
                _clip = new VisualElement();
                _clip.AddToClassList(ClipClass);
                host.Insert(at, Root);
                Root.Add(_clip);
                _clip.Add(sheet);
                _rollerLeft = Roller(mirrored: false);
                _rollerRight = Roller(mirrored: true);
            }

            _ticker = Root.schedule.Execute(Tick).Every(16);
            _ticker.Pause();
            // At rest the scroll still has to follow its host: a resize changes the width the clip
            // and the rollers were placed against.
            Root.RegisterCallback<GeometryChangedEvent>(_ => { if (_motion == Motion.None && IsOpen) Apply(); });
            _sheet.RegisterCallback<GeometryChangedEvent>(_ => { if (_motion == Motion.None && IsOpen) Apply(); });
        }

        /// <summary>The wrapper: what a seal goes on, so the part hanging over the sheet's edge is
        /// not cut off by the clip.</summary>
        public VisualElement Root { get; }

        /// <summary>True from <see cref="Open"/> until <see cref="Close"/> has rolled it shut.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>True while it is unfurling or rolling up. A tap then is a tap on a button half
        /// behind a roller, not a player saying they have read it.</summary>
        public bool IsMoving => _motion != Motion.None;

        /// <summary>Unfurl. <paramref name="onOpened"/> runs once the sheet is fully open.</summary>
        public void Open(Action onOpened = null)
        {
            IsOpen = true;
            _onFinished = onOpened;
            _motion = Motion.Opening;
            _motionStartedAt = Time.realtimeSinceStartup;
            _open = 0f;
            // Nothing is drawn until the sheet has been measured at its real width: the first
            // layout wraps the copy against a clip of zero, and rollers sized off that height would
            // flash the full screen tall.
            Root.style.opacity = 0f;
            _clip.style.width = 0f;
            _unfurlSounded = false;
            UiSfx.Preload(UiSfx.ScrollUnfurls);
            _ticker.Resume();
        }

        /// <summary>Roll shut. <paramref name="onClosed"/> runs once it is closed.</summary>
        public void Close(Action onClosed = null)
        {
            _onFinished = onClosed;
            _motion = Motion.Closing;
            _motionStartedAt = Time.realtimeSinceStartup;
            _ticker.Resume();
        }

        /// <summary>Stop wherever it is, without running a callback.</summary>
        public void Stop()
        {
            IsOpen = false;
            _motion = Motion.None;
            _onFinished = null;
            _ticker?.Pause();
        }

        private VisualElement Roller(bool mirrored)
        {
            var roller = new VisualElement { pickingMode = PickingMode.Ignore };
            roller.AddToClassList(RollerClass);
            if (mirrored) roller.style.scale = new Scale(new Vector2(-1f, 1f));
            Root.Add(roller);
            return roller;
        }

        private void Tick()
        {
            if (_motion == Motion.None) { _ticker.Pause(); return; }

            // Hold the clock until there is a layout to animate against.
            if (!Measure(out _, out _)) { _motionStartedAt = Time.realtimeSinceStartup; return; }

            float elapsed = (Time.realtimeSinceStartup - _motionStartedAt) * 1000f;
            float p;
            if (_motion == Motion.Opening)
            {
                if (!_unfurlSounded)
                {
                    _unfurlSounded = true;
                    UiSfx.Play(UiSfx.ScrollUnfurls);
                }
                p = Mathf.Clamp01((elapsed - HoldMs) / UnfurlMs);
                float left = 1f - p;
                _open = 1f - left * left * left;      // ease out: fast off the roll, slow to the stop
            }
            else
            {
                p = Mathf.Clamp01(elapsed / FurlMs);
                _open = 1f - p * p * p;               // ease in: it gathers speed as it rolls up
            }

            Apply();
            if (p < 1f) return;

            if (_motion == Motion.Closing) IsOpen = false;
            _motion = Motion.None;
            _ticker.Pause();
            Action finished = _onFinished;
            _onFinished = null;
            finished?.Invoke();
        }

        /// <summary>The sheet's full width and its height at that width. False until both are laid
        /// out — an unresolved layout is NaN, which is why the tests are written as <c>!(x > 0)</c>.</summary>
        private bool Measure(out float width, out float height)
        {
            height = 0f;
            width = Root.resolvedStyle.width;
            if (!(width > 0f)) return false;

            _sheet.style.width = width;
            if (!(Mathf.Abs(_sheet.resolvedStyle.width - width) < 1f)) return false;

            height = _sheet.resolvedStyle.height;
            return height > 0f;
        }

        /// <summary>Place the clip and both rollers for the current <see cref="_open"/>.</summary>
        private void Apply()
        {
            if (!Measure(out float width, out float height)) return;

            float shown = width * _open;
            float edge = (width - shown) / 2f;        // from the wrapper's left to the clip's
            _clip.style.width = shown;
            // The clip is centred and the sheet is wider than it, so the sheet is pulled left by
            // the half that is still rolled up. That keeps it still while the window opens.
            _sheet.style.marginLeft = -edge;

            float rollerHeight = height * RollerOverhang;
            float rollerWidth = rollerHeight * RollerAspect;
            float top = (height - rollerHeight) / 2f;
            Place(_rollerLeft, edge - rollerWidth / 2f, top, rollerWidth, rollerHeight);
            Place(_rollerRight, edge + shown - rollerWidth / 2f, top, rollerWidth, rollerHeight);

            Root.style.opacity = 1f;
        }

        private static void Place(VisualElement roller, float left, float top, float width, float height)
        {
            IStyle s = roller.style;
            s.left = left;
            s.top = top;
            s.width = width;
            s.height = height;
        }
    }
}
