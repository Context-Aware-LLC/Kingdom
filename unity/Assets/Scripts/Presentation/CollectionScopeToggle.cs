using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The Owned / All scope control (#146).
    ///
    /// <para><b>Not a fourth filter chip.</b> The pairing chips and the query bar both narrow a pool;
    /// this decides which pool exists. That is why it is not a chip on <see cref="CardQueryBar"/> —
    /// the bar's chips are generated from the card enums and filter a pool, while this says which
    /// pool there is. It therefore asks the controller to REBUILD rather than re-query, and
    /// <see cref="CardQueryBar.Reset"/> deliberately leaves it alone: resetting the filters should
    /// not silently move you between your collection and the game.</para>
    ///
    /// <para><b>Offered only to an account that has an ownership record.</b> Ownership has three
    /// states, not two — no record at all (the wall shows the full pool and this row stays hidden), a
    /// record scoped to it, and a record showing everything anyway. This control is the visible
    /// source of truth for the second and third; it does not exist in the first.</para>
    ///
    /// <para><b>Per visit, and deliberately not static.</b> A remembered scope is exactly the shape of
    /// the PendingDeckId failure the screen already guards: a browse visit that silently shows the
    /// game because of a choice made on a previous one. You arrive at your collection; All is a thing
    /// you ask for.</para>
    /// </summary>
    public sealed class CollectionScopeToggle
    {
        /// <summary>Raised when the player switches pools. The controller rebuilds.</summary>
        public event Action Changed;

        private VisualElement _row;
        private Button _ownedButton, _allButton;
        private bool _hasRecord;

        /// <summary>True when the player has asked to see only what they own. Meaningless without an
        /// ownership record — read <see cref="OwnedOnly"/>, which is the predicate everything else
        /// uses, so the pool and the controls can never disagree about what is being shown.</summary>
        private bool _ownedScope = true;

        public bool OwnedOnly => _hasRecord && _ownedScope;

        /// <summary>
        /// Declare whether this account has an ownership record at all.
        ///
        /// <para><b>Call this the moment ownership is loaded, BEFORE the first pool build — not from
        /// <see cref="Bind"/>.</b> <see cref="OwnedOnly"/> is what the pool asks, and the pool is
        /// built before the UI is wired. Folding this into Bind means the first BuildPool sees
        /// no record, silently builds the FULL pool, and the player's first frame of their own
        /// collection is the whole game. It compiles and it looks plausible; it is wrong.</para>
        /// </summary>
        public void SetOwnershipRecord(bool hasRecord)
        {
            _hasRecord = hasRecord;
            Repaint();
        }

        public void Bind(VisualElement row, Button owned, Button all)
        {
            _row = row;
            _ownedButton = owned;
            _allButton = all;

            if (_row == null || _ownedButton == null || _allButton == null)
            {
                Debug.LogError("[Collection] the Owned/All scope row is missing from the UXML, so a " +
                               "player with an ownership record can only ever see what they own. " +
                               "Check CollectionScreen.uxml for 'scope-row', 'scope-owned', 'scope-all'.");
                return;
            }

            _row.EnableInClassList("col-scope--on", _hasRecord);
            Repaint();
            if (!_hasRecord) return;

            _ownedButton.RegisterCallback<ClickEvent>(_ => Set(true));
            _allButton.RegisterCallback<ClickEvent>(_ => Set(false));
        }

        private void Set(bool ownedOnly)
        {
            if (_ownedScope == ownedOnly) return;
            _ownedScope = ownedOnly;
            Repaint();
            Changed?.Invoke();
        }

        /// <summary>Force the scope without raising <see cref="Changed"/> — for the caller that is
        /// already rebuilding the pool itself (opening and closing the deck editor). Raising here
        /// would rebuild it twice for one transition.</summary>
        public void SetSilently(bool ownedOnly)
        {
            _ownedScope = ownedOnly;
            Repaint();
        }

        /// <summary>The scope as the player last set it, ignoring whether a record exists — what the
        /// deck editor parks on entry so closing restores exactly what was there.</summary>
        public bool RawScope => _ownedScope;

        // Both chips are written on every change so neither can retain stale state.
        public void Repaint()
        {
            _ownedButton?.EnableInClassList("db-chip--on", OwnedOnly);
            _allButton?.EnableInClassList("db-chip--on", !OwnedOnly);
        }
    }
}
