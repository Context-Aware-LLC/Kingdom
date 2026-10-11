using System;
using UnityEngine;
using UnityEngine.UIElements;
using PointerType = UnityEngine.UIElements.PointerType;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The vault's one gesture: take a pack off the rack and drop it on the stage to open it.
    /// <b>Holding it over the rail on the way counts up</b> — one pack, two, three — capped at what
    /// the player holds and at <see cref="MaxBatch"/>, which is how a player with forty packs opens ten
    /// without forty drags (Sam, 2026-09-27). The rack slot's bubble counts DOWN as the held stack
    /// counts up, so the two always add to what the player owns. A drop anywhere but the stage, the
    /// rail included, sends the pack home: there is no parking (Sam, 2026-10-02).
    ///
    /// <para><b>A tap is still an open.</b> A tap on a rack slot opens one pack, so the screen
    /// never REQUIRES a drag — the phone-drag
    /// capture race (<see cref="CollectionDeckDrag"/>'s history) is the thing most likely to break
    /// this, and a broken drag must not mean an unopenable pack.</para>
    ///
    /// <para><b>Same arbitration as <see cref="CollectionDeckDrag"/></b>: every handler is a root
    /// TrickleDown, so the gesture decides before anything below it sees the move, and it captures on
    /// the ROOT, not the source, so a rebuilt rack cannot orphan the capture. Targets are hit by
    /// <c>worldBound</c>, not by picking — the ghost is picking-ignored and the stage's own children
    /// (its hint, the pack that tears) must count as the stage.</para>
    ///
    /// <para>This class decides nothing about packs. It reports a pack id and a count; the controller
    /// owns the balance and the open.</para>
    /// </summary>
    public sealed class PackRackDrag : IDisposable
    {
        /// <summary>The largest stack the rail will count to. The open is one request per pack
        /// (<see cref="PackVaultController"/>), so this bounds the wait as much as the reveal.</summary>
        public const int MaxBatch = 10;

        /// <summary>How long the pack has to sit on the rail for each +1.</summary>
        private const int TickMs = 320;

        /// <summary>What the finger went down on: a rack slot and its pack line.</summary>
        public readonly struct Source
        {
            public readonly VisualElement Element;
            public readonly string PackId;

            public Source(VisualElement element, string packId)
            {
                Element = element;
                PackId = packId;
            }

            public bool IsEmpty => Element == null || string.IsNullOrEmpty(PackId);
        }

        private readonly VisualElement _root;
        private readonly VisualElement _stage;
        private readonly VisualElement _rail;
        private readonly Func<VisualElement, Source> _resolve;
        private readonly Func<bool> _canStart;
        private readonly Func<string, int> _held;
        private readonly Func<string, Sprite> _art;
        private readonly Action<string, int> _open;

        private Source _source;
        private int _pointer = -1;
        private Vector2 _down;
        private int _count;
        private VisualElement _ghost;
        private Label _ghostBadge;

        /// <summary>The source slot's own bubble, counted down while the ghost counts up, and what it
        /// said before the lift so a drop on nothing can put it back.</summary>
        private Label _sourceBadge;
        private string _sourceBadgeText;
        private IVisualElementScheduledItem _tick;

        /// <param name="resolve">The pack under an event target, or an empty source.</param>
        /// <param name="held">How many unopened packs the player holds on a line.</param>
        /// <param name="open">Open this many of this line.</param>
        public PackRackDrag(VisualElement root, VisualElement stage, VisualElement rail,
            Func<VisualElement, Source> resolve, Func<bool> canStart, Func<string, int> held,
            Func<string, Sprite> art, Action<string, int> open)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _stage = stage;
            _rail = rail;
            _resolve = resolve;
            _canStart = canStart;
            _held = held;
            _art = art;
            _open = open;

            root.RegisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
        }

        private int Cap => Math.Max(0, Math.Min(_held(_source.PackId), MaxBatch));

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || !evt.isPrimary || _pointer >= 0) return;
            if (!_canStart()) return;
            Source source = _resolve(evt.target as VisualElement);
            if (source.IsEmpty) return;
            _source = source;
            _pointer = evt.pointerId;
            _down = evt.position;
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != _pointer) return;
            if (_ghost == null)
            {
                Vector2 delta = (Vector2)evt.position - _down;
                float threshold = evt.pointerType == PointerType.touch ? 6f : 10f;
                if (delta.sqrMagnitude < threshold * threshold) return;
                if (Cap < 1) { End(); return; }
                _count = 1;
                CreateGhost();
                _root.CapturePointer(_pointer);
            }
            evt.StopImmediatePropagation();
            Track(evt.position);
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (evt.pointerId != _pointer) return;
            Source source = _source;
            bool dragging = _ghost != null;
            bool toStage = dragging && Over(_stage, evt.position);
            int count = _count;
            // An open keeps the counted-down bubble: those packs are spent, and the rack redraws from
            // the new balance once the reveal is dismissed. Restoring it would flash the old number.
            if (toStage) _sourceBadge = null;
            End();

            if (!dragging)
            {
                // A tap. Only on the thing the finger went down on — a press that slid off it and back
                // under the threshold is still that thing, so the down target is the answer.
                _open(source.PackId, 1);
                return;
            }

            evt.StopImmediatePropagation();
            if (toStage) _open(source.PackId, count);
            // Anywhere else, the rail included, is a drop on nothing: the pack goes home.
        }

        private void Track(Vector2 position)
        {
            Vector2 local = _root.WorldToLocal(position);
            float w = _ghost.resolvedStyle.width, h = _ghost.resolvedStyle.height;
            // NaN before the first layout — an unresolved size is NaN, not 0.
            if (!(w > 0f)) w = 0f;
            if (!(h > 0f)) h = 0f;
            _ghost.style.left = local.x - (w * 0.5f);
            _ghost.style.top = local.y - (h * 0.5f);

            bool overStage = Over(_stage, position);
            bool overRail = !overStage && Over(_rail, position);
            _stage?.EnableInClassList("vault-stage--armed", overStage);
            _rail?.EnableInClassList("vault-rail--armed", overRail);

            if (overRail && _tick == null)
                _tick = _root.schedule.Execute(Tick).Every(TickMs).StartingIn(TickMs);
            else if (!overRail && _tick != null)
                StopTick();
        }

        private void Tick()
        {
            if (_ghost == null) { StopTick(); return; }
            if (_count >= Cap) return;
            _count++;
            UpdateBadge();
            // Pop, then settle: the transition on .vault-count runs out and back on one class toggle.
            Label badge = _ghostBadge;
            badge.AddToClassList("vault-count--bump");
            badge.schedule.Execute(() => badge.RemoveFromClassList("vault-count--bump")).StartingIn(110);
        }

        private void StopTick()
        {
            _tick?.Pause();
            _tick = null;
        }

        private void UpdateBadge()
        {
            if (_ghostBadge == null) return;
            _ghostBadge.text = _count.ToString();
            _ghostBadge.EnableInClassList("vault-count--max", _count >= Cap && Cap > 1);
            if (_sourceBadge != null)
                _sourceBadge.text = Math.Max(0, _held(_source.PackId) - _count).ToString();
        }

        private static bool Over(VisualElement target, Vector2 position)
            => target != null && target.panel != null && target.resolvedStyle.display != DisplayStyle.None
               && target.worldBound.Contains(position);

        private void CreateGhost()
        {
            _ghost = new VisualElement { name = "vault-drag-ghost", pickingMode = PickingMode.Ignore };
            _ghost.AddToClassList("vault-ghost");
            var face = new VisualElement { pickingMode = PickingMode.Ignore };
            face.AddToClassList("vault-mini");
            face.AddToClassList("vault-ghost__face");
            Sprite art = _art(_source.PackId);
            if (art != null)
            {
                // False means the baker fell back to the raw sprite — still art, so the class stands.
                PackWrapperBaker.ApplyTo(face, art);
                face.AddToClassList("vault-mini--art");
            }
            _ghost.Add(face);

            _ghostBadge = new Label { pickingMode = PickingMode.Ignore };
            _ghostBadge.AddToClassList("vault-count");
            _ghostBadge.AddToClassList("vault-ghost__count");
            _ghost.Add(_ghostBadge);

            _sourceBadge = _source.Element.Q<Label>(className: "vault-count");
            _sourceBadgeText = _sourceBadge?.text;
            UpdateBadge();

            _source.Element.AddToClassList("vault-source--lifted");
            // hierarchy.Add: outside any content container, and last, so it paints over everything.
            _root.hierarchy.Add(_ghost);
        }

        private void OnCancel(PointerCancelEvent evt) { if (evt.pointerId == _pointer) End(); }

        private void OnCaptureOut(PointerCaptureOutEvent evt)
        {
            if (evt.target == _root && evt.pointerId == _pointer && _ghost != null) End();
        }

        private void OnRootDetached(DetachFromPanelEvent evt) { if (evt.target == _root) End(); }

        /// <summary>Drop the gesture without doing anything. Safe to call at any time, and
        /// re-entrant: releasing capture can raise capture-out synchronously.</summary>
        public void End()
        {
            int pointer = _pointer;
            _pointer = -1;
            StopTick();
            if (_sourceBadge != null) _sourceBadge.text = _sourceBadgeText;
            _sourceBadge = null;
            _sourceBadgeText = null;
            _source.Element?.RemoveFromClassList("vault-source--lifted");
            _source = default;
            _ghost?.RemoveFromHierarchy();
            _ghost = null;
            _ghostBadge = null;
            _count = 0;
            _stage?.RemoveFromClassList("vault-stage--armed");
            _rail?.RemoveFromClassList("vault-rail--armed");
            if (pointer >= 0 && _root.HasPointerCapture(pointer)) _root.ReleasePointer(pointer);
        }

        public void Dispose()
        {
            End();
            _root.UnregisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerMoveEvent>(OnMove, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            _root.UnregisterCallback<DetachFromPanelEvent>(OnRootDetached);
        }
    }
}
