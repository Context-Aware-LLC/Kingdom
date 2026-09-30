using System;
using System.Linq;
using System.Reflection;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PointerType = UnityEngine.UIElements.PointerType;

namespace Emberfall.Editor
{
    /// <summary>Real UI Toolkit event routing, capture and ghost checks, with production deck mutations.</summary>
    public static class CollectionDeckDragVerify
    {
        private static int _failed;
        public static void Run()
        {
            _failed = 0;
            var owner = ScriptableObject.CreateInstance<CollectionDeckDragVerifyOwner>();
            // Unity's own panel-test factory: real layout/events without a native GUI window or GPU.
            var panelType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel");
            var panel = (IPanel)panelType.GetMethod("CreateEditorPanel", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { owner });
            var host = new GameObject("CollectionDeckDragVerify");
            host.SetActive(false);
            var texture = new Texture2D(4, 4);
            CollectionDeckDrag drag = null;
            try
            {
                var root = panel.visualTree;
                root.style.width = 1200;
                root.style.height = 800;
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/UI/Collection/CollectionTheme.tss"));
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI/Collection/collection.uss"));
                var wall = new ScrollView();
                Place(wall, 20, 100, 550, 600);
                root.Add(wall);
                wall.contentContainer.style.height = 1600;
                var tile = new VisualElement { name = "wall-card" };
                Place(tile, 20, 50, 150, 210);
                wall.Add(tile);
                var deckArea = new VisualElement { name = "deck" };
                Place(deckArea, 700, 100, 350, 600);
                root.Add(deckArea);
                var row = new VisualElement { name = "deck-row" };
                Place(row, 10, 30, 300, 54);
                deckArea.Add(row);

                var db = CardDatabaseLoader.Load();
                CardDefinition card = db.AllCollectible().First(c => c.Class == CardClass.NEUTRAL && c.Rarity == Rarity.COMMON);
                var deck = new DeckList(CardClass.NIGHTVEIL, CardClass.TEMPEST);
                var controller = host.AddComponent<CollectionController>();
                Set(controller, "_deck", deck);
                Set(controller, "_db", db);
                Set(controller, "_drawerOpen", true);
                Set(controller, "_deckList", new ListView());
                var baker = host.AddComponent<CardFaceBaker>();
                Set(controller, "_wall", new CardWall(new ListView(),
                    AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Collection/CardRowTemplate.uxml"), baker));
                int clicks = 0;
                tile.RegisterCallback<ClickEvent>(_ => clicks++);
                row.RegisterCallback<ClickEvent>(_ => clicks++);
                bool sourceCurrent = true;
                CollectionDeckDrag.Source Resolve(VisualElement target)
                {
                    if (!sourceCurrent) return default;
                    if (target == tile || tile.Contains(target)) return new CollectionDeckDrag.Source(tile, card, false);
                    if (target == row || row.Contains(target)) return new CollectionDeckDrag.Source(row, card, true);
                    return default;
                }
                drag = new CollectionDeckDrag(root, root, wall, deckArea, Resolve,
                    source => (bool)Call(controller, "CanDropDeckCard", source), _ => texture,
                    source => Call(controller, source.FromDeck ? "RemoveCopy" : "AddCopy", source.Card), _ => { });

                root.panel.Pick(new Vector2(1, 30)); // Batchmode must lay out before reading coordinates.
                Require(wall.contentContainer.worldBound.height > wall.contentViewport.worldBound.height,
                    "scroll fixture has overflow (content=" + wall.contentContainer.worldBound.height
                    + ", viewport=" + wall.contentViewport.worldBound.height + ")");
                Vector2 start = tile.worldBound.center;
                Vector2 finish = row.worldBound.center;
                var originalParent = tile.parent;
                Down(root, start);
                Move(root, start + new Vector2(4, 0));
                Require(root.Q("deck-drag-preview") == null, "small movement remains a click");
                Up(root, start);
                Require(clicks == 1 && deck.TotalCount == 0, "ordinary clicks remain available (clicks=" + clicks + ")");

                Down(root, start);
                Move(root, finish);
                var ghost = root.Q("deck-drag-preview");
                Require(ghost != null && ghost.style.backgroundImage.value.texture == texture,
                    "drag shows the cached card artwork ghost");
                Require(tile.parent == originalParent, "source tile is never reparented");
                root.panel.Pick(finish);
                Require(Vector2.Distance(ghost.worldBound.center, finish) < 1f,
                    "artwork ghost is centered on the pointer");
                Require(root.Q("deck-drop-preview")?.resolvedStyle.display == DisplayStyle.Flex,
                    "valid deck drop is highlighted");
                Require(root.panel.Pick(finish) == row, "ghost and highlight do not intercept picking");
                Up(root, finish);
                Click(tile);
                Require(deck.CountOf(card.Id) == 1 && clicks == 1, "drop adds exactly one and suppresses trailing click");
                Require(root.Q("deck-drag-preview") == null && !root.HasPointerCapture(0), "drop cleans ghost and capture");

                Down(root, finish);
                Move(root, start);
                Require(root.Q("deck-drag-preview")?.style.backgroundImage.value.texture == texture,
                    "deck-row removal also previews the card artwork");
                Up(root, start);
                Require(deck.TotalCount == 0, "deck-to-wall drop removes one copy");

                Down(root, start);
                Move(root, new Vector2(1150, 70));
                Up(root, new Vector2(1150, 70));
                Require(deck.TotalCount == 0, "outside drop changes nothing");

                deck.Entries.Add(new DeckEntry(card.Id, 2));
                Down(root, start);
                Move(root, finish);
                Require(root.Q("deck-drop-preview")?.style.display.value == DisplayStyle.None,
                    "copy-capped target is not highlighted");
                Up(root, finish);
                Require(deck.TotalCount == 2, "copy cap rejects add");
                deck.Entries.Clear();
                Set(controller, "_owned", new NoCards());
                Down(root, start);
                Move(root, finish);
                Up(root, finish);
                Require(deck.TotalCount == 0, "ownership rejects unowned card");
                Set(controller, "_owned", null);

                Down(root, start);
                Move(root, finish);
                sourceCurrent = false;
                Up(root, finish);
                Require(deck.TotalCount == 0 && root.Q("deck-drag-preview") == null,
                    "recycled or invalidated source cancels safely");
                sourceCurrent = true;

                Down(root, start);
                Move(root, finish);
                using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
                { escape.target = root; root.SendEvent(escape); }
                Up(root, finish);
                Require(deck.TotalCount == 0 && root.Q("deck-drag-preview") == null, "Escape cancels without a drop");

                Down(root, start, true);
                Move(root, start + new Vector2(25, 2), true);
                Require(root.HasPointerCapture(1) && root.Q("deck-drag-preview") != null,
                    "sideways touch claims drag before ScrollView");
                Move(root, finish, true);
                Up(root, finish, true);
                Require(deck.TotalCount == 1, "touch drop adds exactly one");
                deck.Entries.Clear();

                Down(root, start, true);
                float scrollBefore = wall.scrollOffset.y;
                Move(root, start + new Vector2(2, -40), true);
                Require(wall.scrollOffset.y > scrollBefore, "vertical touch actually scrolls the wall");
                Move(root, finish, true);
                Require(root.Q("deck-drag-preview") == null, "vertical touch stays scrolling after moving sideways");
                Up(root, finish, true);
                Require(deck.TotalCount == 0, "scroll gesture never transfers a card");

                wall.scrollOffset = Vector2.zero;
                root.panel.Pick(start);
                start = tile.worldBound.center;
                using (var wheel = WheelEvent.GetPooled(new Event
                { type = EventType.ScrollWheel, mousePosition = start, delta = new Vector2(0, 5) }))
                { wheel.target = tile; tile.SendEvent(wheel); }
                Require(wall.scrollOffset.y > 0, "mouse wheel still scrolls with the drag handler installed");
                wall.scrollOffset = Vector2.zero;
                root.panel.Pick(start);
                start = tile.worldBound.center;

                Down(root, start);
                Move(root, finish);
                Require(root.Q("deck-drag-preview") != null, "cancellation fixture starts a real drag");
                drag.Cancel();
                Require(root.Q("deck-drag-preview") == null && !root.HasPointerCapture(0),
                    "screen/focus cancellation releases preview and capture");

                Down(root, start);
                Move(root, finish);
                row.CapturePointer(0);
                Move(root, finish + new Vector2(20, 0));
                Require(root.Q("deck-drag-preview") == null && deck.TotalCount == 0,
                    "external capture loss cancels without transferring");
                row.ReleasePointer(0);
                if (_failed != 0) throw new InvalidOperationException(_failed + " drag checks failed");
                Debug.Log("[CollectionDeckDragVerify] ALL PASS");
            }
            finally
            {
                drag?.Dispose();
                panel.Dispose();
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private sealed class NoCards : IOwnedCardCounts { public int CopiesOwned(int cardId) => 0; }
        private static void Place(VisualElement element, float x, float y, float width, float height)
        {
            element.style.position = Position.Absolute;
            element.style.left = x;
            element.style.top = y;
            element.style.width = width;
            element.style.height = height;
        }
        private static void Down(VisualElement root, Vector2 position, bool touch = false)
        {
            using var evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = position });
            Send(root, evt, position, touch, 1);
        }
        private static void Move(VisualElement root, Vector2 position, bool touch = false)
        {
            using var evt = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseDrag, button = 0, mousePosition = position });
            Send(root, evt, position, touch, 1);
        }
        private static void Up(VisualElement root, Vector2 position, bool touch = false)
        {
            using var evt = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = position });
            Send(root, evt, position, touch, 0);
        }
        private static void Send<T>(VisualElement root, PointerEventBase<T> evt, Vector2 position, bool touch, int buttons)
            where T : PointerEventBase<T>, new()
        {
            var type = typeof(PointerEventBase<T>);
            type.GetProperty("pointerId").SetValue(evt, touch ? 1 : 0);
            type.GetProperty("pointerType").SetValue(evt, touch ? PointerType.touch : PointerType.mouse);
            type.GetProperty("isPrimary").SetValue(evt, true);
            type.GetProperty("pressedButtons").SetValue(evt, buttons);
            var target = root.panel.GetCapturingElement(touch ? 1 : 0) as VisualElement ?? root.panel.Pick(position) ?? root;
            evt.target = target;
            target.SendEvent(evt);
        }
        private static void Click(VisualElement element)
        {
            using var evt = ClickEvent.GetPooled();
            typeof(PointerEventBase<ClickEvent>).GetProperty("pointerId").SetValue(evt, 0);
            evt.target = element;
            element.SendEvent(evt);
        }
        private static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Call(object target, string method, params object[] args) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool ok, string message)
        {
            if (!ok) _failed++;
            Debug.Log("[CollectionDeckDragVerify] " + (ok ? "PASS " : "FAIL ") + message);
        }
    }

    public sealed class CollectionDeckDragVerifyOwner : ScriptableObject { }
}
