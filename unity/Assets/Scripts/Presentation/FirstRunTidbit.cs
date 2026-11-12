using System;
using System.Collections.Generic;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The first-look overlay, a proclamation scroll: a title, one illustration, an instruction and
    /// a dry line under it, one <b>Got it</b>. Slice 4 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, and since #403 the first-look
    /// lesson on every screen that has one.
    ///
    /// <para><b>Host-agnostic, on the contract <see cref="PackRevealPanel"/> established.</b> It
    /// queries its <c>tidbit-*</c> elements out of whatever root it is handed and reports
    /// <see cref="IsBound"/>, so the same overlay serves every screen that carries the markup.</para>
    ///
    /// <para><b>A host without the markup HANDS THROUGH; it never swallows the callback.</b> That is
    /// the same failure <see cref="PackTearAnimator"/> guards, and it matters for the same reason:
    /// every caller here is mid-journey, so an overlay that quietly does nothing AND quietly drops
    /// <c>onDone</c> strands the player on the screen that asked for it. <see cref="Show"/> returns
    /// false in that case and calls <c>onDone</c> anyway.</para>
    ///
    /// <para><b>It owns the "already seen" decision, so no host has to know the marker key shape.</b>
    /// A tidbit the player has dismissed is filtered out before anything is shown, and an entire
    /// sequence that filters to nothing is indistinguishable from an unregistered trigger — both
    /// hand straight through. Seen is recorded on <b>Got it</b>, not on display, matching the gift
    /// marker: a player who force-quits mid-card has not been told anything yet.</para>
    ///
    /// <para><b>It does not know what a pack or a class is.</b> Its entire input is a list of
    /// <see cref="Tidbit"/>.</para>
    ///
    /// <para><b>The scroll is <see cref="ProclamationScroll"/></b>, wrapped around the
    /// <c>tidbit-card</c> block every host carries, so the hosts' markup did not change.</para>
    ///
    /// <para><b><c>onDone</c> runs after the scroll has rolled shut</b>, not on the tap.
    /// <see cref="IsShowing"/> stays true until then.</para>
    /// </summary>
    public sealed class FirstRunTidbit
    {
        private const string HiddenClass = "tidbit-overlay--hidden";
        private const int StampDelayMs = 120;

        private readonly VisualElement _overlay;
        private readonly VisualElement _card;
        private readonly ProclamationScroll _scroll;
        private readonly VisualElement _art;
        private readonly Label _title;
        private readonly Label _body;
        private readonly IUiSpriteProvider _sprites;
        private NarratorVoice _voice;

        private readonly List<Tidbit> _queue = new List<Tidbit>();
        private Action _onDone;

        /// <param name="root">Any element the <c>tidbit-*</c> tree sits under.</param>
        /// <param name="sprites">Illustration source. Defaults to the same editor/addressable pair
        /// every other UI chrome consumer picks between.</param>
        public FirstRunTidbit(VisualElement root, IUiSpriteProvider sprites = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            _overlay = root.Q<VisualElement>("tidbit-overlay");
            _card = root.Q<VisualElement>("tidbit-card");
            _art = root.Q<VisualElement>("tidbit-art");
            _title = root.Q<Label>("tidbit-title");
            _body = root.Q<Label>("tidbit-body");

            if (sprites != null) _sprites = sprites;
#if UNITY_EDITOR
            else _sprites = new EditorUiSpriteProvider();
#else
            else _sprites = new AddressableUiSpriteProvider();
#endif

            Button done = root.Q<Button>("btn-tidbit-done");
            if (done != null) done.clicked += Advance;

            if (_card != null && _card.parent != null) _scroll = new ProclamationScroll(_card);
            _overlay?.RegisterCallback<DetachFromPanelEvent>(_ => Cancel());

            // Hidden regardless of what the markup said — a host that forgets the hidden class would
            // otherwise open its screen behind a dimmed backdrop with no way past it.
            Hide();
        }

        /// <summary>True when the host's markup actually carried a tidbit tree.</summary>
        public bool IsBound => _overlay != null && _card != null && _title != null && _body != null;

        /// <summary>True while a card is on screen. A host uses it to hold its own input off.</summary>
        public bool IsShowing { get; private set; }

        /// <summary>
        /// Show every tidbit registered for <paramref name="trigger"/> that this player has not
        /// already dismissed, one after another, then call <paramref name="onDone"/>.
        /// </summary>
        /// <returns>
        /// False when nothing was shown — an unbound host, a trigger with no entries in
        /// <see cref="TidbitCatalog"/>, or a player who has seen them all.
        /// <paramref name="onDone"/> has already been called in every one of those cases, so a
        /// caller that only wants to continue can ignore the return value entirely.
        /// </returns>
        public bool Show(TidbitTrigger trigger, Action onDone = null)
            => Show(TidbitCatalog.For(trigger), onDone);

        /// <inheritdoc cref="Show(TidbitTrigger, Action)"/>
        public bool Show(IReadOnlyList<Tidbit> tidbits, Action onDone = null)
        {
            Cancel();
            if (IsBound && tidbits != null)
                foreach (Tidbit tidbit in tidbits)
                    if (tidbit != null && !FirstRunMarkers.Has(FirstRunMarkers.Tidbit(tidbit.Id)))
                        _queue.Add(tidbit);

            if (_queue.Count == 0)
            {
                _onDone = null;
                onDone?.Invoke();
                return false;
            }

            _onDone = onDone;
            Render(_queue[0], stamp: false);
            _overlay.RemoveFromClassList(HiddenClass);
            IsShowing = true;
            _voice = NarratorVoice.Create();
            if (_scroll != null) _scroll.Open(PresentLine);
            else PresentLine();
            return true;
        }

        /// <summary>
        /// Dismiss the card on screen without recording it as seen, and without running
        /// <c>onDone</c>. For a host tearing its screen down underneath the overlay — the player has
        /// not read it, so they are owed it again.
        /// </summary>
        public void Cancel()
        {
            _queue.Clear();
            _onDone = null;
            Hide();
        }

        private void Advance()
        {
            // A tap while the scroll is still moving is a tap on a button that is half behind a
            // roller; it is not the player saying they have read it.
            if (!IsShowing || _queue.Count == 0 || _scroll?.IsMoving == true) return;
            _voice?.Stop();

            // Recorded here rather than in Render: a beat is seen when the player says so.
            FirstRunMarkers.Mark(FirstRunMarkers.Tidbit(_queue[0].Id));
            _queue.RemoveAt(0);

            if (_queue.Count > 0)
            {
                Render(_queue[0], stamp: true);
                return;
            }

            SealStamp.Clear(_scroll?.Root);
            if (_scroll != null) _scroll.Close(Finish);
            else Finish();
        }

        private void Finish()
        {
            Hide();
            Action done = _onDone;
            _onDone = null;
            done?.Invoke();
        }

        private void Render(Tidbit tidbit, bool stamp)
        {
            _title.text = tidbit.Title;
            _body.text = tidbit.Body;
            SealStamp.Clear(_scroll?.Root);
            if (stamp) PresentLine();

            if (_art == null) return;

            // RETIREMENT TRIGGER: none of Assets/Art/UI/Chrome/Tidbits/ exists yet (spec §6.5,
            // files 15-17, and one per TidbitCatalog entry since #403). Until a tidbit's .png lands
            // there AND is registered in the UI_Core Addressables group, its key resolves to null
            // and the card is title-and-copy —
            // which is a complete card, not a broken one. Delete this block's `else` branch only
            // when a missing illustration should become an error. AddressableUiSpriteProvider does
            // warn on the miss, so unlike PackArtProvider this absence is not silent in a build.
            Sprite sprite = _sprites?.Get(tidbit.ArtKey);
            _art.style.backgroundImage = sprite == null
                ? new StyleBackground(StyleKeyword.None)
                : new StyleBackground(sprite);
            _art.style.display = sprite == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void PresentLine()
        {
            if (!IsShowing || _queue.Count == 0) return;
            Stamp();
            // A tidbit without a recording is still a complete card.
            string voice = _queue[0].VoiceAddress;
            if (!string.IsNullOrEmpty(voice)) _voice?.Play(voice);
        }

        /// <summary>Every proclamation is sealed once it is open: the wax lands on the sheet's
        /// bottom-left, clear of the rollers and of <b>Got it</b>, a beat after the scroll stops. It
        /// goes on the wrapper, not the card — the card sits inside the clip, which would cut off
        /// the part of the seal that hangs over the edge.</summary>
        private void Stamp()
        {
            VisualElement wrapper = _scroll?.Root;
            SealStamp.Clear(wrapper);
            Sprite seal = _sprites?.Get(SealStamp.SpriteKey);
            if (seal == null || wrapper == null) return;
            Sprite tool = _sprites.Get(SealStamp.ToolSpriteKey);
            wrapper.schedule.Execute(() =>
            {
                // The player may have dismissed it, or the host torn it down, inside the delay.
                if (!IsShowing || _scroll.IsMoving) return;
                SealStamp.Play(wrapper, seal, tool, size: 170f, left: 130f, bottom: -30f);
            }).StartingIn(StampDelayMs);
        }

        private void Hide()
        {
            IsShowing = false;
            if (_voice != null) _voice.Close();
            _voice = null;
            _scroll?.Stop();
            SealStamp.Clear(_scroll?.Root);
            _overlay?.AddToClassList(HiddenClass);
        }
    }
}
