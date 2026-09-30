using System;
using System.Collections.Generic;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The one-card first-run overlay: a title, one illustration, at most two lines, one <b>Got
    /// it</b>. Slice 4 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>Host-agnostic, on the contract <see cref="PackRevealPanel"/> established.</b> It
    /// queries its <c>tidbit-*</c> elements out of whatever root it is handed and reports
    /// <see cref="IsBound"/>, so the same overlay serves character creation and — once crafting and
    /// the store have routes — the tail of the gift.</para>
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
    /// <see cref="Tidbit"/>, which is why the two held entries cost nothing here.</para>
    /// </summary>
    public sealed class FirstRunTidbit
    {
        private const string HiddenClass = "tidbit-overlay--hidden";

        private readonly VisualElement _overlay;
        private readonly VisualElement _card;
        private readonly VisualElement _art;
        private readonly Label _title;
        private readonly Label _body;
        private readonly IUiSpriteProvider _sprites;

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
        /// False when nothing was shown — an unbound host, a trigger whose entries are all still
        /// held (<see cref="TidbitCatalog"/>), or a player who has seen them all.
        /// <paramref name="onDone"/> has already been called in every one of those cases, so a
        /// caller that only wants to continue can ignore the return value entirely.
        /// </returns>
        public bool Show(TidbitTrigger trigger, Action onDone = null)
            => Show(TidbitCatalog.For(trigger), onDone);

        /// <inheritdoc cref="Show(TidbitTrigger, Action)"/>
        public bool Show(IReadOnlyList<Tidbit> tidbits, Action onDone = null)
        {
            _queue.Clear();
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
            Render(_queue[0]);
            _overlay.RemoveFromClassList(HiddenClass);
            IsShowing = true;
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
            if (!IsShowing || _queue.Count == 0) return;

            // Recorded here rather than in Render: a beat is seen when the player says so.
            FirstRunMarkers.Mark(FirstRunMarkers.Tidbit(_queue[0].Id));
            _queue.RemoveAt(0);

            if (_queue.Count > 0)
            {
                Render(_queue[0]);
                return;
            }

            Hide();
            Action done = _onDone;
            _onDone = null;
            done?.Invoke();
        }

        private void Render(Tidbit tidbit)
        {
            _title.text = tidbit.Title;
            _body.text = tidbit.Body;

            if (_art == null) return;

            // RETIREMENT TRIGGER: none of Assets/Art/UI/Chrome/Tidbits/ exists yet (spec §6.5,
            // files 15-17). Until tidbit_characters.png lands there AND is registered in the
            // UI_Core Addressables group, this resolves to null and the card is title-and-copy —
            // which is a complete card, not a broken one. Delete this block's `else` branch only
            // when a missing illustration should become an error. AddressableUiSpriteProvider does
            // warn on the miss, so unlike PackArtProvider this absence is not silent in a build.
            Sprite sprite = _sprites?.Get(tidbit.ArtKey);
            _art.style.backgroundImage = sprite == null
                ? new StyleBackground(StyleKeyword.None)
                : new StyleBackground(sprite);
            _art.style.display = sprite == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void Hide()
        {
            IsShowing = false;
            _overlay?.AddToClassList(HiddenClass);
        }
    }
}
