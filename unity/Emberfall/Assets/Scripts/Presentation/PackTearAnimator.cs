using System;
using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The tear: a sealed pack, a foil strip that pulls away, and the split that hands off to
    /// <see cref="PackRevealPanel"/>. Slice 3 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c> — the moment Sam actually asked for.
    ///
    /// <para><b>It ends where the reveal begins.</b> The spec's step 4 is "the cards fly out into
    /// <c>reveal-cards</c> — the existing panel's staggered schedule already does this", so this class
    /// deliberately stops at the split and calls back. There is exactly one card-arrival animation in
    /// this project and it is not here. Do not grow a second.</para>
    ///
    /// <para><b>Host-agnostic, the same contract <see cref="PackRevealPanel"/> established.</b> It
    /// queries <c>tear-*</c> out of whatever root it is handed and reports <see cref="IsBound"/> when
    /// the markup carried a stage. A host without one gets a no-op that hands straight through to the
    /// reveal, rather than a null to guard at the call site.</para>
    ///
    /// <para><b>UI Toolkit only, and no particle system</b> (spec §slice 3). Which means <b>no
    /// keyframes</b>: USS supports transitions, not <c>@keyframes</c>, so every beat here is a class
    /// toggle scheduled against a transition on the base rule — including the idle breath, which is a
    /// class flipped on an interval rather than a loop. Reaching for a Coroutine or an
    /// <see cref="UnityEngine.Update"/> lerp would be a frame loop to cancel on scene exit; an
    /// element's own <c>schedule</c> dies with the panel.</para>
    ///
    /// <para><b>The torn halves are BAKED, never drawn</b> (2026-09-03). <see cref="PackWrapperBaker"/>
    /// renders the intact face and both torn pieces from ONE source sprite across a shared procedural
    /// tear line, so files 4 and 5 of spec §6.1 were never art anybody had to produce — that table is
    /// corrected. Handing the baker the same face the tapped tile wears is why <see cref="Play"/>
    /// takes a sprite.</para>
    ///
    /// <para><b>What is still unbuilt degrades rather than waits.</b> The foil strip (§6.1 file 3) is
    /// a foil-coloured band and the burst (§6.2 file 6, <c>fx_pack_burst.png</c>) is a USS glow. The
    /// stage is authored so dropping either in is a background-image on an existing rule; a tear that
    /// waited for art would be a feature that could not be seen at all.
    /// <b>RETIREMENT TRIGGER:</b> when <c>fx_pack_burst.png</c> lands under
    /// <c>Assets/Art/VFX/Pack/</c>, this paragraph and the <c>tear-burst</c> placeholder rule in
    /// <c>packvault.uss</c> go together.</para>
    /// </summary>
    public sealed class PackTearAnimator
    {
        private const string HiddenClass = "tear-overlay--hidden";

        /// <summary>How far down a drag has to travel before it counts as a pull rather than a tap.
        /// Both gestures open the pack, so this only decides which one the player performed — it is
        /// not a threshold anything can fail.</summary>
        private const float PullDistancePx = 40f;

        /// <summary>The beat schedule, in ms from the tear starting. The strip leaves on the frame the
        /// gesture lands (so it has no entry here), the halves follow it apart, and the burst peaks
        /// between the two so it reads as the thing that pushed them rather than a flash afterwards.</summary>
        private const int BurstMs = 180;
        private const int SplitMs = 240;
        private const int HandOffMs = 620;

        /// <summary>The idle breath's half period. Long enough to read as breathing rather than
        /// pulsing, and it is a class flip because USS has no keyframes — see the type remarks.</summary>
        private const int BreathMs = 1400;

        private readonly VisualElement _overlay;
        private readonly VisualElement _pack;
        private readonly VisualElement _strip;
        private readonly VisualElement _burst;
        private readonly VisualElement _topHalf;
        private readonly VisualElement _bottomHalf;

        /// <summary>The intact sealed wrapper — the third view the crimp shader bakes, and the only
        /// one visible before the tear. It exists because the two pieces overlap and their torn edges
        /// are ALPHA: a sealed pack showing the pieces directly shows its own seam. It goes at the
        /// same moment the strip does.</summary>
        private readonly VisualElement _face;
        private readonly Label _hint;
        private readonly Button _skip;

        /// <summary>The face this tear is of, handed in by <see cref="Play"/> and consumed by the bake
        /// in <see cref="Tear"/>. Null means the set has no art.</summary>
        private Sprite _source;

        private IVisualElementScheduledItem _breath;
        private Action _onComplete;

        /// <summary>One tear at a time. The pack is already spent by the time this runs, so a second
        /// entry would not cost the player anything — it would just deal the same receipt twice.</summary>
        private bool _running;

        /// <summary>True once the pack has been struck, so the gesture cannot fire the beat twice on a
        /// pointer that both taps and drags.</summary>
        private bool _torn;

        private Vector3 _pressAt;

        /// <param name="root">Any element the <c>tear-*</c> tree sits under.</param>
        public PackTearAnimator(VisualElement root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            _overlay = root.Q<VisualElement>("tear-overlay");
            _pack = root.Q<VisualElement>("tear-pack");
            _strip = root.Q<VisualElement>("tear-strip");
            _burst = root.Q<VisualElement>("tear-burst");
            _topHalf = root.Q<VisualElement>("tear-pack-top");
            _bottomHalf = root.Q<VisualElement>("tear-pack-bottom");
            _face = root.Q<VisualElement>("tear-pack-face");
            _hint = root.Q<Label>("tear-hint");
            _skip = root.Q<Button>("btn-tear-skip");

            if (_pack != null)
            {
                _pack.RegisterCallback<PointerDownEvent>(OnPress);
                _pack.RegisterCallback<PointerUpEvent>(OnRelease);
            }

            if (_skip != null) _skip.clicked += Skip;

            // Hidden regardless of the markup, the same guard the reveal panel keeps: a host that
            // forgot the hidden class would otherwise sit a sealed pack over its own screen forever.
            Hide();
        }

        /// <summary>True when the host's markup actually carried a tear stage.</summary>
        public bool IsBound => _overlay != null && _pack != null;

        /// <summary>
        /// Run the tear, then call <paramref name="onComplete"/> — which is where the reveal goes.
        ///
        /// <para><b>An unbound host completes immediately rather than dropping the callback.</b> The
        /// pack is already open and its cards are already granted by the time this is called; a tear
        /// that swallowed the handoff would strand a player on a spent entitlement, which is the one
        /// failure on this screen that costs something real.</para>
        /// </summary>
        /// <param name="source">The pack face being torn — the SAME sprite the tile the player tapped
        /// is wearing, which is why the caller resolves it by shelf index. It is what
        /// <see cref="PackWrapperBaker"/> bakes the two torn halves from. Null is legal and means the
        /// set has no art: the halves stay flat plates and the beat still runs.</param>
        /// <param name="allowSkip">False on the gift's first pack (spec §8: "no skip on the gift's
        /// first pack; everything after it is skippable"). It hides the control — a disabled one would
        /// still take the Android back gesture through <c>emb-back</c>.</param>
        public void Play(PackOpenReceipt receipt, Sprite source, Action onComplete,
                         bool allowSkip = true)
        {
            if (!IsBound || receipt == null)
            {
                onComplete?.Invoke();
                return;
            }

            if (_running) return;
            _running = true;
            _torn = false;
            _onComplete = onComplete;
            _source = source;

            ResetStage();
            if (_skip != null) _skip.style.display = allowSkip ? DisplayStyle.Flex : DisplayStyle.None;
            if (_hint != null) _hint.text = "Tap or pull down to open";

            _overlay.RemoveFromClassList(HiddenClass);
            StartBreathing();
        }

        public void Hide()
        {
            StopBreathing();
            _overlay?.AddToClassList(HiddenClass);
        }

        // ── The gesture ─────────────────────────────────────────────────────────────────

        private void OnPress(PointerDownEvent evt)
        {
            if (_torn) return;
            _pressAt = evt.position;
            // Capture so a pull that leaves the pack's own rect still reports its release here; without
            // it a downward drag is lost the moment the pointer clears the element it started on.
            _pack.CapturePointer(evt.pointerId);
        }

        private void OnRelease(PointerUpEvent evt)
        {
            if (_torn) return;
            if (_pack.HasPointerCapture(evt.pointerId)) _pack.ReleasePointer(evt.pointerId);

            // A tap and a pull both open it; the distance only decides which one happened. Anything
            // else — a drag UP, a drag sideways — is still an open, because the pack is already spent
            // and refusing the gesture would leave the player poking a screen that owes them cards.
            float travelled = evt.position.y - _pressAt.y;
            Tear(travelled >= PullDistancePx);
        }

        private void Skip()
        {
            if (_torn) { Complete(); return; }
            Tear(false, immediate: true);
        }

        // ── The beat ────────────────────────────────────────────────────────────────────

        private void Tear(bool pulled, bool immediate = false)
        {
            if (_torn) return;
            _torn = true;
            StopBreathing();

            if (_hint != null) _hint.text = "";
            if (_skip != null) _skip.style.display = DisplayStyle.None;

            if (immediate)
            {
                Complete();
                return;
            }

            // THE BAKE GOES HERE, BEFORE ANYTHING SEPARATES. The crimp shader bakes three views of one
            // wrapper — the intact face and the two torn pieces, sharing one procedural tear line — so
            // the halves must be painted while they are still stacked and invisible. Painting them
            // after the split would swap the texture under a running transition.
            //
            // ApplyTornHalves assigns BOTH halves or NEITHER, so a failed bake leaves the flat
            // placeholder plates rather than one baked half beside one plate. The return value is
            // unchecked for exactly that reason — there is nothing to do about false that the
            // placeholder is not already doing.
            //
            // A null _source is the artless set, which is a normal state, not a fault.
            PackWrapperBaker.ApplyTornHalves(_topHalf, _bottomHalf, _source);

            // A pull earns a longer strip travel than a tap, because the player's own gesture already
            // moved it. Everything after the strip is identical either way.
            _pack.AddToClassList("tear-pack--opening");
            _face?.AddToClassList("tear-pack-face--gone");
            if (_strip != null)
            {
                _strip.AddToClassList("tear-strip--away");
                if (pulled) _strip.AddToClassList("tear-strip--pulled");
            }

            Schedule(() => _burst?.AddToClassList("tear-burst--fired"), BurstMs);
            Schedule(() =>
            {
                _topHalf?.AddToClassList("tear-pack-half--parted");
                _bottomHalf?.AddToClassList("tear-pack-half--parted");
            }, SplitMs);
            Schedule(Complete, HandOffMs);
        }

        /// <summary>Hand off to whatever is showing the cards. Idempotent — Skip and the scheduled
        /// hand-off can both reach it, and dealing the same receipt twice would build a second row of
        /// cards under the first.</summary>
        private void Complete()
        {
            if (!_running) return;
            _running = false;

            Action done = _onComplete;
            _onComplete = null;
            Hide();
            done?.Invoke();
        }

        // ── Stage ───────────────────────────────────────────────────────────────────────

        /// <summary>Put every element back to sealed. <see cref="Play"/> can run again on the next pack
        /// off the same shelf, and a stage left in its torn state would open the next pack already
        /// open.</summary>
        private void ResetStage()
        {
            _pack.RemoveFromClassList("tear-pack--opening");
            _strip?.RemoveFromClassList("tear-strip--away");
            _strip?.RemoveFromClassList("tear-strip--pulled");
            _burst?.RemoveFromClassList("tear-burst--fired");
            _topHalf?.RemoveFromClassList("tear-pack-half--parted");
            _bottomHalf?.RemoveFromClassList("tear-pack-half--parted");
            _face?.RemoveFromClassList("tear-pack-face--gone");
            _pack.RemoveFromClassList("tear-pack--breathe");
        }

        private void StartBreathing()
        {
            StopBreathing();
            // Every() rather than a chain of StartingIn()s: the interval is owned by the element, so it
            // stops with the panel and there is no cancellation to forget.
            _breath = _pack.schedule.Execute(() =>
            {
                if (_torn) return;
                _pack.ToggleInClassList("tear-pack--breathe");
            }).Every(BreathMs);
        }

        private void StopBreathing()
        {
            _breath?.Pause();
            _breath = null;
            _pack?.RemoveFromClassList("tear-pack--breathe");
        }

        /// <summary>Schedule against the overlay, so every pending beat dies with the panel on a scene
        /// change rather than firing into a destroyed tree.</summary>
        private void Schedule(Action action, int delayMs)
        {
            if (_overlay == null) { action(); return; }
            _overlay.schedule.Execute(() =>
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
