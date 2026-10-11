using System;
using System.Collections.Generic;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The bulk Reduce: every copy held past a deck cap, what breaking them down would pay, and one
    /// button that does it.
    ///
    /// <para><b>The list edits like a deck</b> (Sam, 2026-10-11). It opens with every spare copy
    /// selected, and each tap on a line takes one copy back out of what will be broken down, the
    /// way a tap on a deck line takes a copy out of the deck. A line tapped down to nothing stays
    /// listed and dimmed, and the next tap puts all of its copies back — there is no wall here to
    /// add from, so the line itself is the way back.</para>
    ///
    /// <para><b>Not a MonoBehaviour and it prices nothing</b> — the same shape as
    /// <see cref="CraftModal"/>. The rows come from <see cref="Crafting.Spares"/>; the only thing
    /// decided here is how many of each the player left in, which can never exceed what a row
    /// offered. The server holds every count to the same ceiling.</para>
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

        private IReadOnlyList<SpareCopies> _spares = Array.Empty<SpareCopies>();

        /// <summary>
        /// Copies of each card left in, by card id. Kept across redraws while the panel is open, so
        /// ticking the Legendary box does not undo the lines the player already took copies out of.
        /// </summary>
        private readonly Dictionary<int, int> _selected = new Dictionary<int, int>();

        public bool IsOpen { get; private set; }

        public bool IncludeLegendary => _legendary != null && _legendary.value;

        /// <summary>
        /// Raised when the player commits, with card id to copies for every line that still has a
        /// copy in. The host destroys; this never does.
        /// </summary>
        public event Action<IReadOnlyDictionary<int, int>> ReduceRequested;

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
            _reduce?.RegisterCallback<ClickEvent>(_ => Commit());

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

        /// <summary>Show the panel with Legendaries out and nothing taken back. The host follows with <see cref="Show"/>.</summary>
        public void Open()
        {
            if (!IsBound) return;
            _submitting = false;
            _selected.Clear();
            _legendary.SetValueWithoutNotify(false);
            _legendary.EnableInClassList("spares-toggle--on", false);
            IsOpen = true;
            _panel.style.display = DisplayStyle.Flex;
            _scrim.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Draw the offer. <paramref name="message"/> is what the last commit reported, or empty.
        ///
        /// <para><paramref name="reduced"/> says a commit just landed, and it matters: whatever is
        /// spare now is exactly what the player took back out, so those lines come up with nothing
        /// selected rather than re-armed for the next tap of the button.</para>
        /// </summary>
        public void Show(IReadOnlyList<SpareCopies> spares, string message = "", bool reduced = false)
        {
            if (!IsBound) return;
            _submitting = false;
            _close.SetEnabled(true);
            _legendary.SetEnabled(true);
            _spares = spares ?? Array.Empty<SpareCopies>();

            // A line seen before keeps the player's count, clamped to what is spare now; a new line
            // comes in whole. Lines that are gone take their counts with them.
            var carried = new Dictionary<int, int>(_selected);
            _selected.Clear();
            _list.Clear();
            for (int i = 0; i < _spares.Count; i++)
            {
                SpareCopies spare = _spares[i];
                int selected = reduced ? 0
                    : carried.TryGetValue(spare.Card.Id, out int kept) ? Math.Min(kept, spare.Copies)
                    : spare.Copies;
                _selected[spare.Card.Id] = selected;
                _list.Add(Row(spare));
            }

            DrawTotals();
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

        private void Commit()
        {
            if (_submitting) return;
            var cards = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> line in _selected)
                if (line.Value > 0) cards[line.Key] = line.Value;
            if (cards.Count == 0) return;
            SetWorking();
            ReduceRequested?.Invoke(cards);
        }

        private VisualElement Row(SpareCopies spare)
        {
            var row = new VisualElement();
            row.AddToClassList("spares-row");

            var name = new Label(spare.Card.Name) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("spares-row__name");
            name.AddToClassList(CardClassTokens.RarityToken(spare.Card.Rarity, "craft-rarity--"));
            row.Add(name);

            var copies = new Label { pickingMode = PickingMode.Ignore };
            copies.AddToClassList("spares-row__copies");
            row.Add(copies);

            var payout = new Label { pickingMode = PickingMode.Ignore };
            payout.AddToClassList("spares-row__payout");
            row.Add(payout);

            DrawRow(row, copies, payout, spare);
            row.RegisterCallback<ClickEvent>(_ =>
            {
                if (_submitting) return;
                int selected = _selected.TryGetValue(spare.Card.Id, out int now) ? now : 0;
                _selected[spare.Card.Id] = selected > 0 ? selected - 1 : spare.Copies;
                DrawRow(row, copies, payout, spare);
                DrawTotals();
                _status.style.display = DisplayStyle.None;
            });
            return row;
        }

        private void DrawRow(VisualElement row, Label copies, Label payout, SpareCopies spare)
        {
            int selected = _selected[spare.Card.Id];
            copies.text = $"{selected} of {spare.Copies}";
            // The row's own payout is for all its copies, so the part left in is that share of it.
            payout.text = (spare.Payout / spare.Copies * selected).ToString();
            row.EnableInClassList("spares-row--kept", selected == 0);
        }

        private void DrawTotals()
        {
            int copies = 0;
            int cards = 0;
            long payout = 0;
            for (int i = 0; i < _spares.Count; i++)
            {
                int selected = _selected[_spares[i].Card.Id];
                if (selected == 0) continue;
                cards++;
                copies += selected;
                payout += (long)_spares[i].Payout / _spares[i].Copies * selected;
            }

            _total.text = payout.ToString();
            _reduce.SetEnabled(copies > 0);
            if (_spares.Count == 0)
            {
                _summary.text = "No spare copies";
                _note.text = "Nothing here is past what a deck can play.";
                return;
            }
            _summary.text = copies > 0
                ? $"{copies} spare {(copies == 1 ? "copy" : "copies")} across {cards} {(cards == 1 ? "card" : "cards")}"
                : "Nothing selected";
            _note.text = "Tap a card to keep one more copy. Reducing cannot be undone.";
        }
    }
}
