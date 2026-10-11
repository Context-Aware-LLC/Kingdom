using System;
using System.Collections.Generic;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The bulk Reduce: every copy held past a deck cap, what breaking them all down would pay, and
    /// one button that does it.
    ///
    /// <para><b>Not a MonoBehaviour and it decides nothing</b> — the same shape as
    /// <see cref="CraftModal"/>, for the same reasons. Every row and the total come from
    /// <see cref="Crafting.Spares"/>, which is the function the server runs against the ledger, so
    /// the number drawn here is the number paid unless the collection moved in between; and then the
    /// receipt, not this panel, is what the host reports.</para>
    ///
    /// <para><b>Legendaries are out until the box is ticked</b> (Sam, 2026-10-11), and the box
    /// resets every time the panel opens: a second Legendary is the copy a player is likeliest to
    /// regret, so the opt-in is never remembered for them.</para>
    /// </summary>
    public sealed class SparesPanel
    {
        private readonly VisualElement _panel;
        private readonly VisualElement _scrim;
        private readonly ScrollView _list;
        private readonly Label _summary;
        private readonly Label _total;
        private readonly Label _note;
        private readonly Label _status;
        private readonly Toggle _legendary;
        private readonly Button _reduce;
        private readonly Button _close;
        private bool _submitting;
        private bool _hasSpares;

        public bool IsOpen { get; private set; }

        public bool IncludeLegendary => _legendary != null && _legendary.value;

        /// <summary>Raised when the player commits. The argument is <see cref="IncludeLegendary"/>. The host destroys; this never does.</summary>
        public event Action<bool> ReduceRequested;

        /// <summary>Raised when the Legendary box changes, so the host can redraw from a fresh <see cref="Crafting.Spares"/>.</summary>
        public event Action IncludeLegendaryChanged;

        public SparesPanel(VisualElement rootPlate)
        {
            _panel = rootPlate.Q<VisualElement>("spares-panel");
            _scrim = rootPlate.Q<VisualElement>("spares-scrim");
            _list = rootPlate.Q<ScrollView>("spares-list");
            _summary = rootPlate.Q<Label>("spares-summary");
            _total = rootPlate.Q<Label>("spares-total");
            _note = rootPlate.Q<Label>("spares-note");
            _status = rootPlate.Q<Label>("spares-status");
            _legendary = rootPlate.Q<Toggle>("spares-legendary");
            _reduce = rootPlate.Q<Button>("spares-reduce");
            _close = rootPlate.Q<Button>("spares-close");

            _legendary?.RegisterValueChangedCallback(e =>
            {
                // `:checked` is a pseudo-state, not a class, so the fill is driven explicitly — the
                // same pattern as .ms-toggle--on.
                _legendary.EnableInClassList("spares-toggle--on", e.newValue);
                if (IsOpen && !_submitting) IncludeLegendaryChanged?.Invoke();
            });
            _reduce?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_submitting || !_hasSpares) return;
                SetWorking();
                ReduceRequested?.Invoke(IncludeLegendary);
            });

            // Same dismissal rules as the craft modal: the scrim and Close dismiss, the body does not.
            _scrim?.RegisterCallback<ClickEvent>(_ => Close());
            _panel?.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            _close?.RegisterCallback<ClickEvent>(_ => Close());

            Close();
        }

        /// <summary>True when every element this panel binds was found — a false is a broken plate.</summary>
        public bool IsBound =>
            _panel != null && _scrim != null && _list != null && _summary != null && _total != null &&
            _note != null && _status != null && _legendary != null && _reduce != null && _close != null;

        /// <summary>Show the panel with Legendaries out. The host follows with <see cref="Show"/>.</summary>
        public void Open()
        {
            if (!IsBound) return;
            _submitting = false;
            _legendary.SetValueWithoutNotify(false);
            _legendary.EnableInClassList("spares-toggle--on", false);
            IsOpen = true;
            _panel.style.display = DisplayStyle.Flex;
            _scrim.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Draw the offer. <paramref name="message"/> is what the last commit reported, or empty; it
        /// stays up until the next draw so a finished Reduce is not answered by a silent empty list.
        /// </summary>
        public void Show(IReadOnlyList<SpareCopies> spares, string message = "")
        {
            if (!IsBound) return;
            _submitting = false;
            _close.SetEnabled(true);
            _legendary.SetEnabled(true);

            int copies = 0;
            long payout = 0;
            _list.Clear();
            for (int i = 0; i < spares.Count; i++)
            {
                copies += spares[i].Copies;
                payout += spares[i].Payout;
                _list.Add(Row(spares[i]));
            }

            _hasSpares = copies > 0;
            _total.text = payout.ToString();
            _summary.text = _hasSpares
                ? $"{copies} spare {(copies == 1 ? "copy" : "copies")} across {spares.Count} {(spares.Count == 1 ? "card" : "cards")}"
                : "No spare copies";
            _note.text = _hasSpares
                ? "Spares are copies past what a deck can play. This cannot be undone."
                : "Nothing here is past what a deck can play.";
            _reduce.SetEnabled(_hasSpares);
            _status.text = message;
            _status.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>The server is deciding. Nothing on the panel moves until the host draws again.</summary>
        public void SetWorking()
        {
            if (!IsBound) return;
            _submitting = true;
            _close.SetEnabled(false);
            _legendary.SetEnabled(false);
            _reduce.SetEnabled(false);
            _status.style.display = DisplayStyle.None;
        }

        public void Close()
        {
            if (_submitting) return;
            IsOpen = false;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        private static VisualElement Row(SpareCopies spare)
        {
            var row = new VisualElement();
            row.AddToClassList("spares-row");

            var name = new Label(spare.Card.Name);
            name.AddToClassList("spares-row__name");
            name.AddToClassList(CardClassTokens.RarityToken(spare.Card.Rarity, "craft-rarity--"));
            row.Add(name);

            var copies = new Label($"{spare.Copies} spare");
            copies.AddToClassList("spares-row__copies");
            row.Add(copies);

            var payout = new Label(spare.Payout.ToString());
            payout.AddToClassList("spares-row__payout");
            row.Add(payout);
            return row;
        }
    }
}
