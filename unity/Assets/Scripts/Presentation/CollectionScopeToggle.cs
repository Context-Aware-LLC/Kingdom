using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The Owned / Unowned scope control (#146).
    ///
    /// <para><b>Not a fourth filter chip.</b> The class strip and the query bar both narrow a pool;
    /// this decides which pool exists. That is why it is not a chip on <see cref="CardQueryBar"/> —
    /// the bar's chips are generated from the card enums and filter a pool, while this says which
    /// pool there is. It therefore asks the controller to REBUILD rather than re-query, and
    /// <see cref="CardQueryBar.Reset"/> deliberately leaves it alone: resetting the filters should
    /// not silently move you between your collection and the game.</para>
    ///
    /// <para><b>Always on screen, whatever the ownership record says</b> (Sam, 2026-11-06), so the
    /// row never comes and goes by route. This control does not know about the record at all: the
    /// caller decides what "owned" means for a card. A superuser's all-at-limit record owns every
    /// card; a device with no verified record shows the Collection's retry state over an empty
    /// wall, where the toggle has nothing to scope.</para>
    ///
    /// <para><b>Two independent chips, never both off</b> (Sam, 2026-10-05). Owned alone is your
    /// collection, Unowned alone is what you are shopping for, and both together is the game —
    /// what the single All chip used to say. Pressing the only lit chip does nothing, because a
    /// wall with neither half has no meaning.</para>
    ///
    /// <para><b>Per visit, and deliberately not static.</b> A remembered scope is exactly the shape of
    /// the PendingDeckId failure the screen already guards: a browse visit that silently shows the
    /// game because of a choice made on a previous one. You arrive at your collection; Unowned is a
    /// thing you ask for.</para>
    /// </summary>
    public sealed class CollectionScopeToggle
    {
        /// <summary>Raised when the player switches pools. The controller rebuilds.</summary>
        public event Action Changed;

        private Button _ownedButton, _unownedButton;
        private readonly EventCallback<ClickEvent> _onOwned, _onUnowned;

        /// <summary>Which halves the player has asked to see. Read <see cref="Includes"/>, which is
        /// the predicate everything else uses, so the pool and the chips can never disagree about
        /// what is being shown.</summary>
        private bool _showOwned = true, _showUnowned;

        public CollectionScopeToggle()
        {
            _onOwned = _ => Set(!_showOwned, _showUnowned);
            _onUnowned = _ => Set(_showOwned, !_showUnowned);
        }

        public bool OwnedOnly => _showOwned && !_showUnowned;
        public bool UnownedOnly => _showUnowned && !_showOwned;

        /// <summary>Whether a card belongs in the pool, given whether the account owns a copy.</summary>
        public bool Includes(bool owned) => owned ? _showOwned : _showUnowned;

        /// <summary>Bind the two chips. Safe to call on every OnEnable: the callbacks are fixed
        /// delegates, unregistered from the previous buttons first, so a re-entered screen never
        /// toggles twice per tap (which would read as a chip that ignores you).</summary>
        public void Bind(Button owned, Button unowned)
        {
            _ownedButton?.UnregisterCallback(_onOwned);
            _unownedButton?.UnregisterCallback(_onUnowned);
            _ownedButton = owned;
            _unownedButton = unowned;

            if (_ownedButton == null || _unownedButton == null)
            {
                Debug.LogError("[Collection] the Owned/Unowned scope chips are missing from the UXML, " +
                               "so the player cannot change which pool the wall shows. " +
                               "Check CollectionScreen.uxml for 'scope-owned', 'scope-unowned'.");
                return;
            }

            _ownedButton.RegisterCallback(_onOwned);
            _unownedButton.RegisterCallback(_onUnowned);
            Repaint();
        }

        private void Set(bool owned, bool unowned)
        {
            if (!owned && !unowned) return;
            _showOwned = owned;
            _showUnowned = unowned;
            Repaint();
            Changed?.Invoke();
        }

        /// <summary>Force the scope without raising <see cref="Changed"/> — for the caller that is
        /// already rebuilding the pool itself (opening and closing the deck editor). Raising here
        /// would rebuild it twice for one transition.</summary>
        public void SetSilently(bool owned, bool unowned)
        {
            _showOwned = owned;
            _showUnowned = unowned;
            Repaint();
        }

        /// <summary>The scope as the player last set it — what the deck editor parks on entry so
        /// closing restores exactly what was there.</summary>
        public (bool owned, bool unowned) RawScope => (_showOwned, _showUnowned);

        // Both chips are written on every change so neither can retain stale state, and with the
        // same class a tap writes, so the default Owned is lit exactly as a chosen one is.
        public void Repaint()
        {
            _ownedButton?.EnableInClassList("db-chip--on", _showOwned);
            _unownedButton?.EnableInClassList("db-chip--on", _showUnowned);
        }
    }
}
