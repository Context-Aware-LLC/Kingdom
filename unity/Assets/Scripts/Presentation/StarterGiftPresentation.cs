using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The starter grant, shown on the main menu's empty right side once the royal-grant tidbit has
    /// rolled shut (Sam, 2026-11-04): each item appears with its amount, holds, then shrinks and
    /// flies into the menu control it now lives behind — shards to Collection, gold to the gold total,
    /// packs to the vault.
    ///
    /// <para><b>Presentation only.</b> The server granted everything before the menu was read
    /// (<c>OnboardingGrantService</c>); this shows what arrived. The host decides what each landing
    /// does to its own labels, through <see cref="Beat.OnLanded"/>.</para>
    ///
    /// <para><b>The stage goes in just before <c>tidbit-overlay</c></b>, which is the host's last
    /// child on purpose (UI Toolkit has no z-index), so a card opened later still covers it.</para>
    ///
    /// <para><b>Timing is owned by the stage's scheduler</b>, so a scene change that drops the panel
    /// drops the sequence with it, and <c>onDone</c> never runs on a screen that is gone.</para>
    /// </summary>
    public sealed class StarterGiftPresentation
    {
        /// <summary>One item: what it looks like, how much, and where it flies.</summary>
        public sealed class Beat
        {
            /// <param name="caption">The name under the amount, already localized.</param>
            /// <param name="amount">The text over the item, already localized.</param>
            /// <param name="copies">How many of the item to show side by side; each flies on its own.</param>
            /// <param name="iconClass">A USS class that paints the item, or null when
            /// <paramref name="sprite"/> does.</param>
            /// <param name="sprite">Art for the item, or null. Takes precedence over the class.</param>
            /// <param name="target">Where the item flies. Null fades it out where it stands.</param>
            /// <param name="onLanded">Runs as the last copy reaches the target.</param>
            public Beat(string caption, string amount, int copies, string iconClass, Sprite sprite,
                VisualElement target, Action onLanded)
            {
                Caption = caption;
                Amount = amount;
                Copies = Math.Max(1, copies);
                IconClass = iconClass;
                Sprite = sprite;
                Target = target;
                OnLanded = onLanded;
            }

            public string Caption { get; }
            public string Amount { get; }
            public int Copies { get; }
            public string IconClass { get; }
            public Sprite Sprite { get; }
            public VisualElement Target { get; }
            public Action OnLanded { get; }
        }

        // The arrival and flight durations are also the transition-durations on .mm-gift-beat and
        // .mm-gift-item in mainmenu.uss. Change both together.
        private const int ArriveMs = 350;
        private const int HoldMs = 1300;
        private const int FlyMs = 650;
        private const int CopyStaggerMs = 90;
        private const int GapMs = 300;
        private const float LandedScale = 0.18f;

        /// <summary>The fan for several copies: a few degrees each side of upright.</summary>
        private const float FanDegreesPerCopy = 6f;

        private readonly VisualElement _stage;
        private readonly IReadOnlyList<Beat> _beats;
        private readonly Action _onDone;

        private StarterGiftPresentation(VisualElement host, IReadOnlyList<Beat> beats, Action onDone)
        {
            _beats = beats;
            _onDone = onDone;
            _stage = new VisualElement { name = "gift-stage", pickingMode = PickingMode.Ignore };
            _stage.AddToClassList("mm-gift-stage");

            VisualElement overlay = host.Q<VisualElement>("tidbit-overlay");
            if (overlay != null && overlay.parent == host) host.Insert(host.IndexOf(overlay), _stage);
            else host.Add(_stage);
        }

        /// <summary>Play every beat in order, then call <paramref name="onDone"/>. An empty list calls
        /// it at once.</summary>
        public static void Play(VisualElement host, IReadOnlyList<Beat> beats, Action onDone)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (beats == null || beats.Count == 0) { onDone?.Invoke(); return; }
            new StarterGiftPresentation(host, beats, onDone).PlayBeat(0);
        }

        private void PlayBeat(int index)
        {
            if (index >= _beats.Count)
            {
                _stage.RemoveFromHierarchy();
                _onDone?.Invoke();
                return;
            }

            Beat beat = _beats[index];
            var block = new VisualElement { pickingMode = PickingMode.Ignore };
            block.AddToClassList("mm-gift-beat");
            block.AddToClassList("mm-gift-beat--enter");

            var payload = new VisualElement { pickingMode = PickingMode.Ignore };
            payload.AddToClassList("mm-gift-payload");
            var items = new List<VisualElement>(beat.Copies);
            for (int i = 0; i < beat.Copies; i++)
            {
                VisualElement item = BuildItem(beat, i);
                payload.Add(item);
                items.Add(item);
            }
            block.Add(payload);

            var amount = new Label(beat.Amount) { pickingMode = PickingMode.Ignore };
            amount.AddToClassList("mm-gift-amount");
            block.Add(amount);
            var caption = new Label(beat.Caption) { pickingMode = PickingMode.Ignore };
            caption.AddToClassList("mm-gift-caption");
            block.Add(caption);

            _stage.Add(block);

            // One painted frame at the entrance pose, or the transition has nothing to run from.
            _stage.schedule.Execute(() => block.RemoveFromClassList("mm-gift-beat--enter")).StartingIn(30);
            _stage.schedule.Execute(() => Fly(beat, block, items, index)).StartingIn(ArriveMs + HoldMs);
        }

        private static VisualElement BuildItem(Beat beat, int copy)
        {
            var item = new VisualElement { pickingMode = PickingMode.Ignore };
            item.AddToClassList("mm-gift-item");
            if (beat.Copies > 1)
            {
                item.AddToClassList("mm-gift-item--fanned");
                float centre = (beat.Copies - 1) / 2f;
                item.style.rotate = new Rotate(new Angle((copy - centre) * FanDegreesPerCopy));
            }

            if (beat.Sprite != null) PackWrapperBaker.ApplyTo(item, beat.Sprite);
            else if (!string.IsNullOrEmpty(beat.IconClass)) item.AddToClassList(beat.IconClass);
            return item;
        }

        private void Fly(Beat beat, VisualElement block, List<VisualElement> items, int index)
        {
            block.AddToClassList("mm-gift-beat--leaving");

            for (int i = 0; i < items.Count; i++)
            {
                VisualElement item = items[i];
                _stage.schedule.Execute(() => Launch(item, beat.Target)).StartingIn(i * CopyStaggerMs);
            }

            int landing = (items.Count - 1) * CopyStaggerMs + FlyMs;
            _stage.schedule.Execute(() =>
            {
                beat.OnLanded?.Invoke();
                if (beat.Target != null) Pop(beat.Target);
                block.RemoveFromHierarchy();
            }).StartingIn(landing);
            _stage.schedule.Execute(() => PlayBeat(index + 1)).StartingIn(landing + GapMs);
        }

        /// <summary>Send one item at the target's centre, shrinking as it goes. Both bounds are in
        /// panel space and nothing between the stage and the item is scaled once the beat has
        /// arrived, so the difference is the translate.</summary>
        private static void Launch(VisualElement item, VisualElement target)
        {
            item.style.scale = new Scale(new Vector2(LandedScale, LandedScale));
            item.style.opacity = 0.35f;
            if (target == null) return;

            Vector2 from = item.worldBound.center;
            Vector2 to = target.worldBound.center;
            if (float.IsNaN(from.x) || float.IsNaN(to.x)) return;
            item.style.translate = new Translate(to.x - from.x, to.y - from.y);
        }

        /// <summary>The target swells briefly as the item lands in it. Inline, and reset to the
        /// sheet's values afterwards, so the target's own USS transitions come back untouched.</summary>
        private static void Pop(VisualElement target)
        {
            target.style.transitionProperty = new StyleList<StylePropertyName>(
                new List<StylePropertyName> { new StylePropertyName("scale") });
            target.style.transitionDuration = new StyleList<TimeValue>(
                new List<TimeValue> { new TimeValue(0.16f, TimeUnit.Second) });
            target.style.scale = new Scale(new Vector2(1.14f, 1.14f));
            target.schedule.Execute(() => target.style.scale = StyleKeyword.Null).StartingIn(170);
            target.schedule.Execute(() =>
            {
                target.style.transitionProperty = StyleKeyword.Null;
                target.style.transitionDuration = StyleKeyword.Null;
            }).StartingIn(380);
        }
    }
}
