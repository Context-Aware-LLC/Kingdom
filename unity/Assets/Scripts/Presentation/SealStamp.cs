using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Slams the crown wax seal (<c>Art/UI/Chrome/ui_seal_crown.png</c>) down onto an element: it
    /// drops in from above the screen, large and faint, squashes on impact while the target gives a
    /// small jolt, then settles. The game's recurring "this is official" beat; the herald's
    /// proclamation scroll (#403) is its first real consumer, the first-run tidbit card its first
    /// host.
    ///
    /// <para><b>Everything is inline style, nothing is in a sheet</b>, so any element on any screen
    /// can be stamped without its host linking a stylesheet. Each beat sets new values against an
    /// inline transition, which is the same "no keyframes, step between transitions" shape
    /// <see cref="PackTearAnimator"/> uses with classes. Beats are scheduled on the seal itself, so
    /// they die with it if the target leaves the panel mid-slam.</para>
    ///
    /// <para><b>No sound yet.</b> <c>onImpact</c> fires on the frame the wax lands; that is where
    /// the thunk goes once it exists.</para>
    /// </summary>
    public static class SealStamp
    {
        public const string SpriteKey = "ui_seal_crown";
        public const string SealClass = "emb-seal-stamp";

        // The beat schedule, in ms from the call.
        private const int DropMs = 150;    // fall from above to contact
        private const int SquashMs = 70;   // wax spreads, target jolts
        private const int SettleMs = 160;  // spring back to rest

        /// <summary>
        /// Stamp <paramref name="target"/>. The seal is added as its last child, absolutely placed, so
        /// the target's own layout does not move.
        /// </summary>
        /// <param name="size">Seal diameter in authored px.</param>
        /// <param name="left">Offsets of the seal's edges from the target's, as USS absolute
        /// placement: set one of left/right and one of top/bottom. Negative overhangs the target's
        /// edge. All null centres the seal.</param>
        /// <param name="tiltDegrees">Resting rotation. Null picks a random tilt, which is what makes a
        /// stamp look hand-pressed rather than printed.</param>
        /// <param name="onImpact">Called when the wax lands. The sound hook.</param>
        /// <param name="onDone">Called once the seal has settled.</param>
        /// <returns>The seal element, or null when there was nothing to stamp with — in which case
        /// both callbacks have already run, so a caller mid-sequence is never stranded.</returns>
        public static VisualElement Play(
            VisualElement target,
            Sprite seal,
            float size = 150f,
            float? left = null,
            float? top = null,
            float? right = null,
            float? bottom = null,
            float? tiltDegrees = null,
            Action onImpact = null,
            Action onDone = null)
        {
            if (target == null || seal == null)
            {
                onImpact?.Invoke();
                onDone?.Invoke();
                return null;
            }

            float tilt = tiltDegrees ?? UnityEngine.Random.Range(-14f, 14f);

            var stamp = new VisualElement { pickingMode = PickingMode.Ignore };
            stamp.AddToClassList(SealClass);
            IStyle s = stamp.style;
            s.position = Position.Absolute;
            bool placed = left.HasValue || right.HasValue || top.HasValue || bottom.HasValue;
            if (!placed)
            {
                s.left = Length.Percent(50);
                s.top = Length.Percent(50);
                s.marginLeft = -size / 2f;
                s.marginTop = -size / 2f;
            }
            if (left.HasValue) s.left = left.Value;
            if (right.HasValue) s.right = right.Value;
            if (top.HasValue) s.top = top.Value;
            if (bottom.HasValue) s.bottom = bottom.Value;
            s.width = size;
            s.height = size;
            s.backgroundImage = new StyleBackground(seal);
            s.unityBackgroundScaleMode = ScaleMode.ScaleToFit;

            // Start: well above its landing spot, big (close to the camera) and faint, tilted a
            // little past where it will come to rest.
            s.opacity = 0f;
            s.translate = new Translate(0, -size * 1.4f);
            s.scale = new Scale(new Vector2(2.6f, 2.6f));
            s.rotate = new Rotate(new Angle(tilt - 10f, AngleUnit.Degree));
            target.Add(stamp);

            StyleTranslate restingTarget = target.style.translate;

            // Drop. A frame's delay so the start values are laid out before the transition begins;
            // set in the same frame, UI Toolkit would jump straight to the end values.
            Beat(stamp, 16, () =>
            {
                Transition(stamp, DropMs, EasingMode.EaseIn);
                s.opacity = 1f;
                s.translate = new Translate(0, 0);
                s.scale = new Scale(Vector2.one);
                s.rotate = new Rotate(new Angle(tilt, AngleUnit.Degree));
            });

            // Impact: the wax squashes out and the target takes the hit.
            Beat(stamp, 16 + DropMs, () =>
            {
                Transition(stamp, SquashMs, EasingMode.EaseOut);
                s.scale = new Scale(new Vector2(1.14f, 0.88f));
                target.style.translate = new Translate(0, 4);
                onImpact?.Invoke();
            });

            // Settle, with a little overshoot so it reads as springy wax, not a sticker.
            Beat(stamp, 16 + DropMs + SquashMs, () =>
            {
                Transition(stamp, SettleMs, EasingMode.EaseOutBack);
                s.scale = new Scale(Vector2.one);
                target.style.translate = restingTarget;
            });

            Beat(stamp, 16 + DropMs + SquashMs + SettleMs, () => onDone?.Invoke());

            return stamp;
        }

        /// <summary>Remove every seal this class stamped on <paramref name="target"/>.</summary>
        public static void Clear(VisualElement target)
        {
            if (target == null) return;
            var seals = new List<VisualElement>();
            target.Query<VisualElement>(className: SealClass).ForEach(seals.Add);
            foreach (VisualElement seal in seals) seal.RemoveFromHierarchy();
        }

        private static void Transition(VisualElement element, int ms, EasingMode easing)
        {
            IStyle s = element.style;
            s.transitionProperty = new List<StylePropertyName>
            {
                new StylePropertyName("opacity"), new StylePropertyName("translate"),
                new StylePropertyName("scale"), new StylePropertyName("rotate"),
            };
            s.transitionDuration = new List<TimeValue> { new TimeValue(ms, TimeUnit.Millisecond) };
            s.transitionTimingFunction = new List<EasingFunction> { new EasingFunction(easing) };
        }

        private static void Beat(VisualElement host, int delayMs, Action action)
        {
            host.schedule.Execute(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SealStamp] beat threw ({ex.GetType().Name}: {ex.Message}).");
                }
            }).StartingIn(delayMs);
        }
    }
}
