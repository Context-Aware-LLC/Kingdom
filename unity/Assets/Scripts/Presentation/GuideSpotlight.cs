using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The first-run guide's dim (Sam, 2026-11-06): the whole screen darkened except the one
    /// element a new player is being led to, with a gold ring around it that breathes with the
    /// guided button's own pulse. Used by the main menu (vault, then Collection) and the
    /// Collection (Filters, then Decks).
    ///
    /// <para><b>Four shades, not a scrim with a hole.</b> UI Toolkit has no z-index and no cutout,
    /// so a scrim over the screen would cover the button too. The shades are placed around the
    /// target's rect, which leaves it uncovered, lit, and the only thing a tap can reach.</para>
    ///
    /// <para><b>Placed every frame while shown</b>, not on <c>GeometryChangedEvent</c>, because
    /// the menu's entrance and the guide pulse move the target by <c>translate</c> and
    /// <c>scale</c>, which change its world rect without a layout pass.</para>
    ///
    /// <para><b>Under the tidbit, over everything else</b>: the layer is inserted just before
    /// <c>tidbit-overlay</c>, so a lesson card still opens over the dim, and anything added to the
    /// root at runtime after it (the settings gear, a modal) stays above and live.</para>
    /// </summary>
    public sealed class GuideSpotlight
    {
        private const float RingPad = 5f;
        private const float ShadeAlpha = 0.6f;
        private const float FadeSeconds = 0.35f;
        /// <summary>The same breath as the guided buttons (GuidePulseHalfMs on both screens).</summary>
        private const float BreathSeconds = 1.1f;
        private static readonly Color RingDim = new Color(232f / 255f, 196f / 255f, 104f / 255f, 0.5f);
        private static readonly Color RingBright = new Color(1f, 222f / 255f, 132f / 255f, 1f);

        private readonly VisualElement _layer;
        private readonly VisualElement[] _shades = new VisualElement[4];
        private readonly VisualElement _ring;
        private VisualElement _target;
        private IVisualElementScheduledItem _follow;
        private Rect _placed;

        public GuideSpotlight(VisualElement screenRoot)
        {
            _layer = new VisualElement { name = "guide-spotlight", pickingMode = PickingMode.Ignore };
            _layer.style.position = Position.Absolute;
            _layer.style.left = 0;
            _layer.style.top = 0;
            _layer.style.right = 0;
            _layer.style.bottom = 0;
            _layer.style.display = DisplayStyle.None;
            _layer.style.opacity = 0f;
            _layer.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("opacity") };
            _layer.style.transitionDuration = new List<TimeValue> { new TimeValue(FadeSeconds, TimeUnit.Second) };
            _layer.style.transitionTimingFunction = new List<EasingFunction> { new EasingFunction(EasingMode.EaseOut) };

            for (int i = 0; i < _shades.Length; i++)
            {
                // Pickable on purpose: a tap on the dim lands on a shade and goes nowhere.
                var shade = new VisualElement { name = "guide-spotlight-shade" };
                shade.style.position = Position.Absolute;
                shade.style.backgroundColor = new Color(6f / 255f, 8f / 255f, 12f / 255f, ShadeAlpha);
                _shades[i] = shade;
                _layer.Add(shade);
            }

            _ring = new VisualElement { name = "guide-spotlight-ring", pickingMode = PickingMode.Ignore };
            _ring.style.position = Position.Absolute;
            SetRingBorder(3f, 12f, RingDim);
            _ring.style.transitionProperty = new List<StylePropertyName>
            {
                new StylePropertyName("border-top-color"), new StylePropertyName("border-right-color"),
                new StylePropertyName("border-bottom-color"), new StylePropertyName("border-left-color"),
            };
            _ring.style.transitionDuration = new List<TimeValue> { new TimeValue(BreathSeconds, TimeUnit.Second) };
            _ring.style.transitionTimingFunction = new List<EasingFunction> { new EasingFunction(EasingMode.EaseInOut) };
            _layer.Add(_ring);

            VisualElement tidbit = screenRoot?.Q<VisualElement>("tidbit-overlay");
            VisualElement host = tidbit?.parent ?? screenRoot;
            if (host == null) return;
            if (tidbit != null) host.Insert(host.IndexOf(tidbit), _layer);
            else host.Add(_layer);
        }

        /// <summary>The element left lit, or null while the dim is down.</summary>
        public VisualElement Target => _target;

        /// <summary>True when <paramref name="element"/> is the lit target or inside it.</summary>
        public bool Covers(VisualElement element)
            => _target != null && element != null && (element == _target || _target.Contains(element));

        /// <summary>Dim everything but <paramref name="target"/>. Null takes the dim down.</summary>
        public void Show(VisualElement target)
        {
            Hide();
            if (target == null || _layer.parent == null) return;
            _target = target;
            _placed = default;
            _layer.style.display = DisplayStyle.Flex;
            Place();
            _follow = _layer.schedule.Execute(Place).Every(16);
            // A frame later, so the fade runs from the displayed state rather than from none.
            _layer.schedule.Execute(() => { if (_target != null) _layer.style.opacity = 1f; });
        }

        public void Hide()
        {
            _follow?.Pause();
            _follow = null;
            _target = null;
            _layer.style.opacity = 0f;
            _layer.style.display = DisplayStyle.None;
            SetBright(false);
        }

        /// <summary>The ring's half of the breath; the host flips it with its button's pulse.</summary>
        public void SetBright(bool bright) => SetRingColor(bright ? RingBright : RingDim);

        private void Place()
        {
            if (_target == null) return;
            if (_target.panel == null) { Hide(); return; }
            Rect hole = _layer.WorldToLocal(_target.worldBound);
            float w = _layer.layout.width, h = _layer.layout.height;
            if (float.IsNaN(hole.x) || float.IsNaN(hole.y) || float.IsNaN(hole.width)
                || float.IsNaN(hole.height) || float.IsNaN(w) || float.IsNaN(h)) return;
            if (hole == _placed) return;
            _placed = hole;

            float x0 = Mathf.Clamp(hole.xMin, 0f, w), x1 = Mathf.Clamp(hole.xMax, 0f, w);
            float y0 = Mathf.Clamp(hole.yMin, 0f, h), y1 = Mathf.Clamp(hole.yMax, 0f, h);
            Put(_shades[0], 0f, 0f, w, y0);              // above
            Put(_shades[1], 0f, y1, w, h - y1);          // below
            Put(_shades[2], 0f, y0, x0, y1 - y0);        // left
            Put(_shades[3], x1, y0, w - x1, y1 - y0);    // right
            Put(_ring, hole.xMin - RingPad, hole.yMin - RingPad,
                hole.width + 2f * RingPad, hole.height + 2f * RingPad);
        }

        private static void Put(VisualElement element, float x, float y, float width, float height)
        {
            element.style.left = x;
            element.style.top = y;
            element.style.width = Mathf.Max(0f, width);
            element.style.height = Mathf.Max(0f, height);
        }

        private void SetRingBorder(float width, float radius, Color color)
        {
            _ring.style.borderTopWidth = width;
            _ring.style.borderRightWidth = width;
            _ring.style.borderBottomWidth = width;
            _ring.style.borderLeftWidth = width;
            _ring.style.borderTopLeftRadius = radius;
            _ring.style.borderTopRightRadius = radius;
            _ring.style.borderBottomLeftRadius = radius;
            _ring.style.borderBottomRightRadius = radius;
            SetRingColor(color);
        }

        private void SetRingColor(Color color)
        {
            _ring.style.borderTopColor = color;
            _ring.style.borderRightColor = color;
            _ring.style.borderBottomColor = color;
            _ring.style.borderLeftColor = color;
        }
    }
}
