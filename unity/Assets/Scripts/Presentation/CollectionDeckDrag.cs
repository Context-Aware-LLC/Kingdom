using System;
using Kingdom.Core.Cards;
using UnityEngine;
using UnityEngine.UIElements;
using PointerType = UnityEngine.UIElements.PointerType;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Collection-only transfer gesture. Like the design-system draggable, it owns a preview,
    /// target highlight and capture cleanup; dropping invokes a model command, never reparents
    /// a recycled ListView element. Root trickle handlers arbitrate before ScrollView sees moves.
    /// </summary>
    public sealed class CollectionDeckDrag : IDisposable
    {
        public readonly struct Source
        {
            public readonly VisualElement Element;
            public readonly CardDefinition Card;
            public readonly bool FromDeck;

            public Source(VisualElement element, CardDefinition card, bool fromDeck)
            {
                Element = element;
                Card = card;
                FromDeck = fromDeck;
            }
        }

        private readonly VisualElement _root;
        private readonly VisualElement _host;
        private readonly VisualElement _wall;
        private readonly VisualElement _deck;
        private readonly Func<VisualElement, Source> _resolve;
        private readonly Func<Source, bool> _canDrop;
        private readonly Func<CardDefinition, Texture2D> _face;
        private readonly Action<Source> _commit;
        private readonly Action<VisualElement> _cancelTap;
        private Source _source;
        private int _pointer = -1;
        private int _suppressedPointer = -1;
        private Vector2 _down;
        private bool _scrolling;
        private VisualElement _ghost;
        private VisualElement _highlight;

        public CollectionDeckDrag(VisualElement root, VisualElement host,
            VisualElement wall, VisualElement deck, Func<VisualElement, Source> resolve,
            Func<Source, bool> canDrop, Func<CardDefinition, Texture2D> face,
            Action<Source> commit, Action<VisualElement> cancelTap)
        {
            _root = root;
            _host = host;
            _wall = wall;
            _deck = deck;
            _resolve = resolve;
            _canDrop = canDrop;
            _face = face;
            _commit = commit;
            _cancelTap = cancelTap;
            root.RegisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            root.RegisterCallback<ClickEvent>(OnClick, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            root.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(OnNavigationCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || !evt.isPrimary || _pointer >= 0) return;
            _suppressedPointer = -1;
            Source source = _resolve(evt.target as VisualElement);
            if (source.Element == null || source.Card == null) return;
            _source = source;
            _pointer = evt.pointerId;
            _down = evt.position;
            source.Element.RegisterCallback<DetachFromPanelEvent>(OnSourceDetached);
            // Do not capture down: clicks and vertical touch scrolling still own an undecided press.
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != _pointer || _scrolling) return;
            if (!SourceIsCurrent()) { Cancel(); return; }
            if (_ghost == null)
            {
                Vector2 delta = (Vector2)evt.position - _down;
                bool touch = evt.pointerType == PointerType.touch;
                float threshold = touch ? 6f : 10f;
                if (delta.sqrMagnitude < threshold * threshold) return;
                _suppressedPointer = _pointer;
                _cancelTap(_source.Element);
                if (touch && Mathf.Abs(delta.y) >= Mathf.Abs(delta.x))
                {
                    _scrolling = true; // Once scrolling wins, a later sideways move cannot add a card.
                    return;
                }
                CreatePreview();
                _root.CapturePointer(_pointer);
            }
            // This runs ABOVE ScrollView's trickle handlers, avoiding the old capture-stealing race.
            evt.StopImmediatePropagation();
            PositionPreview(evt.position);
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (evt.pointerId != _pointer) return;
            bool dragging = _ghost != null;
            bool accepted = dragging && SourceIsCurrent() && IsOverTarget(evt.position) && _canDrop(_source);
            Source source = _source;
            if (dragging) evt.StopImmediatePropagation();
            EndGesture(dragging || _scrolling); // Clean up before a command refreshes either list.
            if (accepted) _commit(source);
        }

        private bool SourceIsCurrent()
        {
            Source current = _resolve(_source.Element);
            return current.Element == _source.Element && current.Card?.Id == _source.Card.Id
                && current.FromDeck == _source.FromDeck;
        }

        private bool IsOverTarget(Vector2 position)
        {
            VisualElement target = _source.FromDeck ? _wall : _deck;
            VisualElement picked = _root.panel?.Pick(position);
            // Real picking rejects obscured targets (drawers/modals) and clipped scroll contents.
            return picked != null && (picked == target || target.Contains(picked));
        }

        private void CreatePreview()
        {
            _highlight = new VisualElement { name = "deck-drop-preview", pickingMode = PickingMode.Ignore };
            _highlight.AddToClassList("collection-deck-drop-preview");
            _host.hierarchy.Add(_highlight);
            _ghost = new VisualElement { name = "deck-drag-preview", pickingMode = PickingMode.Ignore };
            _ghost.AddToClassList("collection-deck-drag-preview");
            float width = _source.FromDeck ? 220f : _source.Element.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 0f) width = 220f;
            _ghost.style.width = width;
            _ghost.style.height = width * CardWall.TileHeight / CardWall.TileWidth;
            Texture2D face = _face(_source.Card);
            if (face != null)
                _ghost.style.backgroundImage = new StyleBackground(Background.FromTexture2D(face));
            else
                _ghost.Add(new Label(_source.Card.Name) { pickingMode = PickingMode.Ignore });
            _host.hierarchy.Add(_ghost); // Outside either scroll viewport; source stays in its row.
        }

        private void PositionPreview(Vector2 position)
        {
            Vector2 local = _host.WorldToLocal(position);
            _ghost.style.left = local.x;
            _ghost.style.top = local.y;
            bool valid = IsOverTarget(position) && _canDrop(_source);
            _highlight.style.display = valid ? DisplayStyle.Flex : DisplayStyle.None;
            if (!valid) return;
            VisualElement target = _source.FromDeck ? _wall : _deck;
            Rect bounds = _host.WorldToLocal(target.worldBound);
            _highlight.style.left = bounds.x;
            _highlight.style.top = bounds.y;
            _highlight.style.width = bounds.width;
            _highlight.style.height = bounds.height;
        }

        private void OnClick(ClickEvent evt)
        {
            if (evt.pointerId == _suppressedPointer) evt.StopImmediatePropagation();
        }
        private void OnCancel(PointerCancelEvent evt) { if (evt.pointerId == _pointer) Cancel(); }
        private void OnCaptureOut(PointerCaptureOutEvent evt)
        {
            if (evt.target == _root && evt.pointerId == _pointer) Cancel();
        }
        private void OnWheel(WheelEvent evt) => Cancel();
        private void OnSourceDetached(DetachFromPanelEvent evt) => Cancel();
        private void OnRootDetached(DetachFromPanelEvent evt) { if (evt.target == _root) Cancel(); }
        private void OnGeometryChanged(GeometryChangedEvent evt) => Cancel();
        private void OnKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape || _pointer < 0) return;
            Cancel();
            evt.StopImmediatePropagation();
        }
        private void OnNavigationCancel(NavigationCancelEvent evt)
        {
            if (_pointer < 0) return;
            Cancel();
            evt.StopImmediatePropagation();
        }

        public void Cancel()
        {
            EndGesture(true);
        }

        private void EndGesture(bool suppressTap)
        {
            int pointer = _pointer;
            _pointer = -1; // Capture-out may synchronously re-enter this method.
            if (suppressTap && pointer >= 0) _suppressedPointer = pointer;
            if (_source.Element != null)
            {
                _source.Element.UnregisterCallback<DetachFromPanelEvent>(OnSourceDetached);
                if (suppressTap) _cancelTap(_source.Element);
            }
            _source = default;
            _scrolling = false;
            _ghost?.RemoveFromHierarchy();
            _highlight?.RemoveFromHierarchy();
            _ghost = null;
            _highlight = null;
            if (pointer >= 0 && _root.HasPointerCapture(pointer)) _root.ReleasePointer(pointer);
        }

        public void Dispose()
        {
            Cancel();
            _root.UnregisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerMoveEvent>(OnMove, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            _root.UnregisterCallback<ClickEvent>(OnClick, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            _root.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            _root.UnregisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            _root.UnregisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            _root.UnregisterCallback<NavigationCancelEvent>(OnNavigationCancel, TrickleDown.TrickleDown);
            _root.UnregisterCallback<DetachFromPanelEvent>(OnRootDetached);
            _root.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }
    }
}
