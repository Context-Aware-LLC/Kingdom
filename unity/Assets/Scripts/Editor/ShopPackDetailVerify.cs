using System;
using System.Linq;
using System.Reflection;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    /// <summary>Exercises the store's real picking, click bubbling and deselection with its shipped art.</summary>
    public static class ShopPackDetailVerify
    {
        [MenuItem("Kingdom/Verify/Shop Pack Detail")]
        public static void Run()
        {
            int failed = 0;
            foreach (int width in new[] { 2400, 1440 })
            {
                foreach (string sample in new[] { "pack", "art gutter", "shell gutter", "quantity", "purchase", "scrim" })
                    Check(width, 1f, sample, ref failed);
            }
            // The same hit area must follow the shell during its translated/scaled flight.
            Check(2400, 0.65f, "pack", ref failed);
            Check(2400, 0.65f, "art gutter", ref failed);
            if (failed != 0) throw new InvalidOperationException($"Shop pack detail: {failed} click checks failed.");
            Debug.Log("[ShopPackDetailVerify] ALL PASS (14 click checks)");
        }

        private static void Check(int width, float scale, string sample, ref int failed)
        {
            using var fixture = new Fixture(width, scale);
            Vector2 point = fixture.Point(sample);
            bool shouldClose = sample == "art gutter" || sample == "shell gutter" || sample == "scrim";
            Click(fixture.Root, point);
            bool closing = (bool)typeof(ShopPanelController)
                .GetField("_detailClosing", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(fixture.Controller);
            bool quantityChanged = sample != "quantity" || fixture.Quantity.Q<Label>().text == "2";
            bool passed = closing == shouldClose && quantityChanged;
            if (!passed) failed++;
            Debug.Log($"[ShopPackDetailVerify] {(passed ? "PASS" : "FAIL")} {width}px scale={scale} {sample}: " +
                $"closing={closing}, expected={shouldClose}, quantityChanged={quantityChanged}, point={point}");
        }

        private sealed class Fixture : IDisposable
        {
            private readonly ShopPackDetailVerifyOwner _owner;
            private readonly IPanel _panel;
            private readonly VisualElement _art;
            private readonly VisualElement _shell;
            private readonly VisualElement _actions;
            public VisualElement Root { get; }
            public VisualElement Quantity { get; }
            public ShopPanelController Controller { get; }

            public Fixture(int width, float scale)
            {
                _owner = ScriptableObject.CreateInstance<ShopPackDetailVerifyOwner>();
                // Same real, windowless UI Toolkit panel used by CollectionDeckDragVerify.
                var panelType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel");
                _panel = (IPanel)panelType.GetMethod("CreateEditorPanel", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { _owner });
                Root = _panel.visualTree;
                Root.style.width = width;
                Root.style.height = 1080;
                Root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/UI/Common/theme.tss"));
                AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Shop/ShopScreen.uxml").CloneTree(Root);
                // A null gateway keeps this check entirely offline and cannot purchase anything.
                Controller = new ShopPanelController(Root, null, new PackArtProvider(), () => { }, () => { });
                Controller.Open();
                _panel.Pick(Vector2.zero);
                var slot = Root.Q(className: "shop-pack__art-slot");
                if (slot == null || slot.resolvedStyle.backgroundImage.sprite == null)
                    throw new InvalidOperationException("The shipped pack artwork did not load.");
                Click(Root, slot.worldBound.center);
                var overlay = Root.Q("pack-detail-overlay");
                if (overlay.ClassListContains("pack-detail-overlay--hidden"))
                    throw new InvalidOperationException("Clicking the real pack tile did not open its detail.");

                _shell = Root.Q("pack-detail-shell");
                _art = Root.Q("pack-detail-art");
                _actions = Root.Q("pack-detail-actions");
                Quantity = Root.Q("pack-detail-quantity");
                // Sample a settled frame, or a known frame of the flight, without waiting on a timer.
                _shell.RemoveFromClassList("pack-detail-shell--animating");
                _shell.style.opacity = 1;
                _shell.style.scale = new Scale(new Vector3(scale, scale, 1));
                _shell.style.translate = new Translate(scale == 1f ? 0 : -120, scale == 1f ? 0 : 45);
                _actions.style.visibility = Visibility.Visible;
                Quantity.style.visibility = Visibility.Visible;
                _panel.Pick(Vector2.zero);
                _panel.Pick(Vector2.zero); // Include the purchase row's geometry callback.
                if (!(_art.contentRect.height > 0) || !(_actions.worldBound.width > 0))
                    throw new InvalidOperationException("Pack detail fixture did not lay out.");
            }

            public Vector2 Point(string sample)
            {
                Rect box = _art.contentRect;
                Vector2 source = _art.resolvedStyle.backgroundImage.sprite.rect.size;
                float paintedWidth = box.height * source.x / source.y;
                if (!(paintedWidth < box.width))
                    throw new InvalidOperationException("The portrait pack must leave horizontal art gutters.");
                switch (sample)
                {
                    case "pack":
                        // Just inside the painted edge, rather than only its forgiving center.
                        return _art.LocalToWorld(box.center + new Vector2(paintedWidth / 2 - 4, 0));
                    case "art gutter":
                        return _art.LocalToWorld(box.center + new Vector2(paintedWidth / 2 + 4, 0));
                    case "shell gutter":
                        return new Vector2(_shell.worldBound.xMin + 8, _art.worldBound.center.y);
                    case "quantity":
                        return Quantity.Query<Button>().ToList().Last().worldBound.center;
                    case "purchase":
                        return _actions.Query<Button>().ToList().First().worldBound.center;
                    default:
                        return new Vector2(_shell.worldBound.xMax + 20, _art.worldBound.center.y);
                }
            }

            public void Dispose()
            {
                (_panel as IDisposable)?.Dispose();
                UnityEngine.Object.DestroyImmediate(_owner);
            }
        }

        private static void Click(VisualElement root, Vector2 position)
        {
            using (var down = PointerDownEvent.GetPooled(new Event
                   { type = EventType.MouseDown, button = 0, mousePosition = position }))
                Send(root, down, position, 1);
            using (var up = PointerUpEvent.GetPooled(new Event
                   { type = EventType.MouseUp, button = 0, mousePosition = position }))
                Send(root, up, position, 0);
        }

        private static void Send<T>(VisualElement root, PointerEventBase<T> evt, Vector2 position, int buttons)
            where T : PointerEventBase<T>, new()
        {
            var type = typeof(PointerEventBase<T>);
            type.GetProperty("pointerId").SetValue(evt, 0);
            type.GetProperty("pointerType").SetValue(evt, UnityEngine.UIElements.PointerType.mouse);
            type.GetProperty("isPrimary").SetValue(evt, true);
            type.GetProperty("pressedButtons").SetValue(evt, buttons);
            var target = root.panel.GetCapturingElement(0) as VisualElement ?? root.panel.Pick(position) ?? root;
            evt.target = target;
            target.SendEvent(evt);
        }
    }

    public sealed class ShopPackDetailVerifyOwner : ScriptableObject { }
}
