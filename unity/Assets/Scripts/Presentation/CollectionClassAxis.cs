using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The class filter, as ONE axis with two affordances: the nine-tab strip across the top edge,
    /// and the pairing chips offered while editing a deck.
    ///
    /// <para><b>They are not two filters.</b> Both controls write the same set, and every repaint is
    /// driven from that set rather than from whichever control was touched — so pressing a chip
    /// lights the matching tab, and a deck whose primary and secondary coincide cannot leave one
    /// chip's highlight stale. Splitting this out of CollectionController is what makes that
    /// invariant a property of a type instead of a convention four methods had to keep.</para>
    ///
    /// <para>Empty means unconstrained, the all-off convention the rest of the query bar uses. The
    /// axis never touches the wall itself: it raises <see cref="Changed"/> and the controller
    /// composes it with the query bar at its single ApplyQuery site, so neither surface clobbers the
    /// other (and, critically, a chip press does not reset the player's chosen sort).</para>
    /// </summary>
    public sealed class CollectionClassAxis
    {
        /// <summary>Raised when the selected set changes and the wall needs re-querying.</summary>
        public event Action Changed;

        private readonly HashSet<CardClass> _selected = new HashSet<CardClass>();
        private readonly HashSet<CardClass> _available = new HashSet<CardClass>();
        private readonly HashSet<CardClass> _parked = new HashSet<CardClass>();

        /// <summary>Every class tab by the class it stands for, bound once from the enum.</summary>
        private readonly Dictionary<CardClass, VisualElement> _tabs =
            new Dictionary<CardClass, VisualElement>();

        private Button _primaryChip, _secondaryChip, _neutralChip;
        private CardClass? _primary, _secondary;

        /// <summary>The selected classes, or empty for unconstrained. Read by ApplyQuery.</summary>
        public IReadOnlyCollection<CardClass> Selected => _selected;

        public bool IsConstrained => _selected.Count > 0;

        private bool Editing => _primary.HasValue;

        // ---- binding -------------------------------------------------------------------------

        /// <summary>
        /// Bind the nine-tab strip.
        ///
        /// <para>The strip never changes shape (Sam, 2026-08-18): every tab is on screen in every
        /// state, and narrowing deactivates the tabs it excludes rather than removing them. A strip
        /// that reflowed would move the tab under the player's thumb each time they opened a deck; a
        /// strip that only dims keeps a class in the position they learned it in, and says plainly
        /// that it is still a class, just not one of theirs here.</para>
        ///
        /// <para>The ENUM is the roster, not the UXML. Each tab is looked up by the name the class
        /// implies, so a tenth class shows up as a startup error naming the missing tab instead of
        /// silently having no way to be filtered for. The order stays authored, because it is an art
        /// arrangement of nine portraits and not something the enum has an opinion about.</para>
        /// </summary>
        public void BindStrip(VisualElement root)
        {
            var bar = root.Q<VisualElement>("class-tab-bar");
            if (bar == null)
            {
                Debug.LogError("[Collection] 'class-tab-bar' is missing from CollectionScreen.uxml, " +
                               "so the class strip cannot filter anything.");
                return;
            }

            foreach (CardClass klass in Enum.GetValues(typeof(CardClass)))
            {
                string tabName = "class-tab-" + CardClassTokens.Slug(klass);
                var tab = bar.Q<VisualElement>(tabName);
                if (tab == null)
                {
                    Debug.LogError($"[Collection] no '{tabName}' in the class strip, so {klass} cards " +
                                   "cannot be filtered for. Add the tab to CollectionScreen.uxml.");
                    continue;
                }

                CardClass captured = klass;
                tab.RegisterCallback<ClickEvent>(_ => Toggle(captured));
                _tabs[klass] = tab;
            }

            // BuildPool runs BEFORE this bind and has already told us what is available, but
            // SetAvailable could not paint anything because no tab existed yet. Paint now, or the
            // strip's first frame offers all nine as pressable regardless of the pool.
            if (ApplyAvailability()) Changed?.Invoke();
        }

        /// <summary>
        /// Bind the pairing chips — a shortcut onto the same axis, offered while editing: the deck's
        /// primary class, its secondary, and NEUTRAL, which together are the whole of what the pool
        /// holds then. Labels are written at runtime from the real class names, so a rename cannot
        /// drift.
        /// </summary>
        public void BindChips(Button primary, Button secondary, Button neutral)
        {
            _primaryChip = primary;
            _secondaryChip = secondary;
            _neutralChip = neutral;

            _primaryChip?.RegisterCallback<ClickEvent>(_ => TogglePairing(_primary));
            _secondaryChip?.RegisterCallback<ClickEvent>(_ => TogglePairing(_secondary));
            _neutralChip?.RegisterCallback<ClickEvent>(_ => TogglePairing(
                Editing ? CardClass.NEUTRAL : (CardClass?)null));
        }

        // ---- state ---------------------------------------------------------------------------

        /// <summary>
        /// Tell the axis which classes the current pool actually holds a card of.
        ///
        /// <para><b>Availability is read off the pool, never off what is currently visible.</b> The
        /// pool is the screen's scope — your collection, or an open deck's pairing plus neutral —
        /// while the visible set is that scope minus whatever facets are ticked. Deriving from the
        /// visible set would kill the Tempest tab the moment you ticked Legendary and owned no
        /// legendary Tempest, which is exactly when pressing it is how you would find that out.</para>
        ///
        /// <para>A class that goes unavailable while selected is also <b>deselected</b>, and that
        /// raises <see cref="Changed"/>. Leaving it set would filter the wall to a class the pool
        /// cannot contain — a blank wall, with the control that emptied it now greyed out and
        /// unpressable, and no way for the player to undo it.</para>
        /// </summary>
        public void SetAvailable(IEnumerable<CardClass> available)
        {
            _available.Clear();
            foreach (var c in available) _available.Add(c);
            if (ApplyAvailability()) Changed?.Invoke();
        }

        /// <summary>Paint the off-state and drop any now-impossible selection. Returns true when the
        /// selection actually changed, so exactly one caller decides whether to raise
        /// <see cref="Changed"/> — this runs from both the bind and every pool rebuild.</summary>
        private bool ApplyAvailability()
        {
            if (_tabs.Count == 0) return false;   // BuildPool runs before the bind

            bool selectionChanged = false;
            foreach (var pair in _tabs)
            {
                bool ok = _available.Contains(pair.Key);
                if (!ok && _selected.Remove(pair.Key)) selectionChanged = true;

                pair.Value.EnableInClassList("class-tab_art--off", !ok);
                pair.Value.pickingMode = ok ? PickingMode.Position : PickingMode.Ignore;
            }

            Repaint();
            return selectionChanged;
        }

        /// <summary>
        /// Enter deck-editing: park the browse selection, adopt the deck's pairing, relabel the chips.
        ///
        /// <para>A deck opens on its whole pairing, not on whatever class the player happened to be
        /// browsing — usually one this deck cannot play anyway, which would open the editor on a
        /// blank wall. The browse selection is PARKED rather than dropped so closing gives it back
        /// unchanged; <see cref="SetAvailable"/> would otherwise have deselected it for being out of
        /// pool.</para>
        /// </summary>
        public void BeginEditing(CardClass primary, CardClass secondary)
        {
            _parked.Clear();
            _parked.UnionWith(_selected);
            _selected.Clear();

            _primary = primary;
            _secondary = secondary;

            if (_primaryChip != null) _primaryChip.text = CardClassTokens.Label(primary);
            if (_secondaryChip != null) _secondaryChip.text = CardClassTokens.Label(secondary);
            if (_neutralChip != null) _neutralChip.text = CardClassTokens.Label(CardClass.NEUTRAL);
            Repaint();
        }

        /// <summary>Leave deck-editing and restore the browse selection parked by
        /// <see cref="BeginEditing"/>.</summary>
        public void EndEditing()
        {
            _primary = null;
            _secondary = null;
            _selected.Clear();
            _selected.UnionWith(_parked);
            Repaint();
        }

        /// <summary>Clear the axis — the query bar's Reset, which cannot reach a facet it does not
        /// hold. Does NOT raise <see cref="Changed"/>: Reset fires one ApplyQuery of its own, and
        /// two would query the wall twice for one press.</summary>
        public void Clear()
        {
            _selected.Clear();
            Repaint();
        }

        /// <summary>The one write to the axis, whichever control was pressed.</summary>
        public void Toggle(CardClass klass)
        {
            // A tab the pool has nothing behind is already picking-disabled; the guard is here too
            // because the pairing chips reach this by a different path.
            if (!_available.Contains(klass)) return;

            if (!_selected.Remove(klass)) _selected.Add(klass);
            Repaint();
            Changed?.Invoke();
        }

        private void TogglePairing(CardClass? klass)
        {
            if (!Editing || !klass.HasValue) return;
            Toggle(klass.Value);
        }

        /// <summary>
        /// Repaint every control from the set, not just the one that was pressed — see the type
        /// docstring for why that is the whole point of this class.
        /// </summary>
        public void Repaint()
        {
            bool constrained = IsConstrained;
            foreach (var pair in _tabs)
            {
                bool selected = _selected.Contains(pair.Key);
                bool available = _available.Contains(pair.Key);
                pair.Value.EnableInClassList("class-tab_art--on", selected);
                pair.Value.EnableInClassList("class-tab_art--muted", constrained && available && !selected);
            }

            if (!Editing) return;
            _primaryChip?.EnableInClassList("db-chip--on", _selected.Contains(_primary.Value));
            _secondaryChip?.EnableInClassList("db-chip--on", _selected.Contains(_secondary.Value));
            _neutralChip?.EnableInClassList("db-chip--on", _selected.Contains(CardClass.NEUTRAL));
        }
    }
}
