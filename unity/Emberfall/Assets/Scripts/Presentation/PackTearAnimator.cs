using System;
using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The tear: the pack the player tapped comes apart WHERE IT ALREADY IS, on the shelf, and hands
    /// off to <see cref="PackRevealPanel"/>. Slice 3 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>IN PLACE, NOT ON A STAGE</b> (Sam, 2026-09-03, replacing the original overlay). The
    /// first build put a full-screen stage over the shelf, re-rendered the pack into it at a fixed
    /// 260dp, and asked the player to tap a SECOND time — so the object they touched vanished and a
    /// smaller copy of it appeared somewhere else, wearing a foil strip that was flat placeholder
    /// colour. Three faults with one cause: a stage is a second view of a thing that was already on
    /// screen. There is no stage now. The tile is the pack, the tap that spends it is the tap that
    /// opens it, and its own art is what tears. <b>Do not reintroduce an overlay to hold a beat</b> —
    /// anything this needs to draw goes inside the tile.</para>
    ///
    /// <para><b>It ends where the reveal begins.</b> The spec's step 4 is "the cards fly out into
    /// <c>reveal-cards</c> — the existing panel's staggered schedule already does this", so this class
    /// deliberately stops at the split and calls back. There is exactly one card-arrival animation in
    /// this project and it is not here. Do not grow a second.</para>
    ///
    /// <para><b>UI Toolkit only, and no particle system</b> (spec §slice 3). Which means <b>no
    /// keyframes</b>: USS supports transitions, not <c>@keyframes</c>, so every beat here is a class
    /// toggle scheduled against a transition on the base rule. Reaching for a Coroutine or an
    /// <see cref="UnityEngine.Update"/> lerp would be a frame loop to cancel on scene exit; an
    /// element's own <c>schedule</c> dies with the panel.</para>
    ///
    /// <para><b>The torn halves are BAKED, never drawn</b> (2026-09-03). <see cref="PackWrapperBaker"/>
    /// renders the intact face and both torn pieces from ONE source sprite across a shared procedural
    /// tear line, so files 4 and 5 of spec §6.1 were never art anybody had to produce — that table is
    /// corrected. Handing the baker the same sprite the tile is already wearing is why
    /// <see cref="Play"/> takes one.</para>
    ///
    /// <para><b>A failed bake degrades to a fade, and that branch is load-bearing.</b> The two pieces
    /// only reconstruct the pack because they carry the wrapper's art; with nothing to paint them
    /// with, clearing the tile's own background to make room for them would blank the pack instead of
    /// tearing it. So a set with no art, or a bake that could not run, gets the tile fading out under
    /// the reveal — which is honest, and keeps the hand-off on the same clock either way.</para>
    /// </summary>
    public sealed class PackTearAnimator
    {
        /// <summary>The beat schedule, in ms from the tear starting. The burst peaks between the
        /// recoil and the split so it reads as the thing that pushed the pieces apart rather than a
        /// flash afterwards.</summary>
        private const int BurstMs = 120;
        private const int SplitMs = 180;
        private const int HandOffMs = 560;

        /// <summary>One tear at a time. The pack is already spent by the time this runs, so a second
        /// entry would not cost the player anything — it would just deal the same receipt twice.</summary>
        private bool _running;

        private Action _onComplete;

        /// <summary>
        /// Tear <paramref name="face"/> apart, then call <paramref name="onComplete"/> — which is
        /// where the reveal goes.
        ///
        /// <para><b>A missing face completes immediately rather than dropping the callback.</b> The
        /// pack is already open and its cards are already granted by the time this is called; a tear
        /// that swallowed the hand-off would strand a player on a spent entitlement, which is the one
        /// failure on this screen that costs something real.</para>
        /// </summary>
        /// <param name="face">The tapped tile's own face element — the one already wearing the sealed
        /// wrapper. Everything this draws is added to it and never to the screen.</param>
        /// <param name="source">The pack art <paramref name="face"/> was painted from, which
        /// <see cref="PackWrapperBaker"/> bakes the two torn pieces out of. Null is legal and means
        /// the set has no art; see the type remarks on what that degrades to.</param>
        public void Play(PackOpenReceipt receipt, VisualElement face, Sprite source, Action onComplete)
        {
            if (receipt == null || face == null)
            {
                onComplete?.Invoke();
                return;
            }

            if (_running) return;
            _running = true;
            _onComplete = onComplete;

            face.AddToClassList("vault-pack__face--tearing");

            // THE BAKE GOES HERE, BEFORE ANYTHING SEPARATES. The crimp shader bakes three views of one
            // wrapper — the intact face and the two torn pieces, sharing one procedural tear line — so
            // the pieces must be painted while they are still stacked and covering. Painting them
            // after the split would swap the texture under a running transition.
            //
            // ApplyTornHalves assigns BOTH pieces or NEITHER, so there is no state where one baked
            // piece sits beside one blank plate.
            var top = new VisualElement();
            top.AddToClassList("tear-half");
            top.AddToClassList("tear-half--top");
            top.pickingMode = PickingMode.Ignore;

            var bottom = new VisualElement();
            bottom.AddToClassList("tear-half");
            bottom.AddToClassList("tear-half--bottom");
            bottom.pickingMode = PickingMode.Ignore;

            bool baked = PackWrapperBaker.ApplyTornHalves(top, bottom, source);

            if (!baked)
            {
                // No pieces to part. Fade the whole tile instead — see the type remarks.
                Schedule(face, () => face.AddToClassList("vault-pack__face--spent"), SplitMs);
                Schedule(face, Complete, HandOffMs);
                return;
            }

            var burst = new VisualElement();
            burst.AddToClassList("tear-burst");
            burst.pickingMode = PickingMode.Ignore;

            // Burst first so it sits UNDER both pieces: it is the light coming out of the pack, not a
            // flash over it.
            face.Add(burst);
            face.Add(top);
            face.Add(bottom);

            // The pieces now carry the picture, so the tile's own copy of it has to go in the same
            // frame — two intact wrappers, one behind the other, would show through the gap the
            // moment they part. Cleared rather than faded because the pieces are already covering it.
            face.style.backgroundImage = StyleKeyword.None;

            Schedule(face, () => burst.AddToClassList("tear-burst--fired"), BurstMs);
            Schedule(face, () =>
            {
                top.AddToClassList("tear-half--parted");
                bottom.AddToClassList("tear-half--parted");
            }, SplitMs);
            Schedule(face, Complete, HandOffMs);
        }

        /// <summary>Hand off to whatever is showing the cards. Idempotent — dealing the same receipt
        /// twice would build a second row of cards under the first.
        ///
        /// <para>Nothing is torn down here. The reveal covers the shelf immediately, and dismissing it
        /// re-reads the shelf and rebuilds every tile from scratch — so the torn tile is discarded by
        /// the refresh rather than restored, and a player who glimpses the shelf behind the reveal
        /// sees the pack they just opened lying open.</para></summary>
        private void Complete()
        {
            if (!_running) return;
            _running = false;

            Action done = _onComplete;
            _onComplete = null;
            done?.Invoke();
        }

        /// <summary>Schedule against the tile, so every pending beat dies with the shelf on a scene
        /// change or a re-render rather than firing into a detached tree.</summary>
        private static void Schedule(VisualElement host, Action action, int delayMs)
        {
            if (host == null) { action(); return; }
            host.schedule.Execute(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PackTear] beat threw ({ex.GetType().Name}: {ex.Message}).");
                }
            }).StartingIn(delayMs);
        }
    }
}
