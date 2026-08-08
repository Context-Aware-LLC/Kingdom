using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. Decides WHEN a card reveal is up and WHICH card it is showing, from pointer events
    /// alone. No MonoBehaviour, no UnityEngine types, no clock of its own — every method takes the
    /// current time — so the whole trigger policy is exercisable from a headless Editor run
    /// (CardRevealVerify) instead of only by hand on a device.
    ///
    /// <para><b>Why touch is a trigger here and not a bolt-on.</b> The lift-and-scale reveal that
    /// already existed ran off <c>OnMouseEnter</c>/<c>OnMouseExit</c>, which never fire on touch —
    /// so on Android there was no way to read a card in hand at all. Hover cannot be made to work
    /// on touch, because a finger has no position until it presses. The two therefore need
    /// different triggers, and the only place they can be reconciled is here.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Mouse</b> — hovering for <see cref="HoverDelaySeconds"/> reveals; leaving hides.
    /// A mouse press does NOT arm a hold (see below).</item>
    /// <item><b>Touch</b> — pressing and holding for <see cref="HoldSeconds"/> reveals; on release
    /// the panel goes <i>sticky</i> and stays up until the player presses elsewhere or presses the
    /// same card again. A new press on any card clears the sticky and starts fresh.</item>
    /// </list>
    ///
    /// <para><b>Hold is armed for touch only, and that is load-bearing.</b> A press that becomes a
    /// read has to un-arm the selection the same press already made (<see cref="PressBecameRead"/>
    /// → <c>InputRouter.CancelSelection</c>), otherwise reading a card silently leaves it primed to
    /// be played by the next tap. If a mouse press also armed the hold, a merely slow click on
    /// desktop would deselect the card it just selected — a regression in the input path, traded
    /// for nothing, since desktop already reveals on hover without pressing at all.</para>
    ///
    /// <para>Hold outranks hover while both are satisfied: on a hybrid device the deliberate
    /// gesture should win over the incidental one.</para>
    /// </summary>
    internal sealed class HoverRevealModel
    {
        /// <summary>Long enough that sweeping the pointer across the fan on the way somewhere else
        /// does not strobe a panel on and off over every card it crosses.</summary>
        internal const float HoverDelaySeconds = 0.45f;

        /// <summary>Long enough to be unmistakably deliberate against a tap-to-select, short enough
        /// that a player who wants to read a card does not think the gesture failed.</summary>
        internal const float HoldSeconds = 0.35f;

        internal enum PointerKind { Mouse, Touch }
        internal enum RevealSource { None, Hover, Hold, Sticky }

        private bool       _hoverActive;
        private InstanceId _hoverKey;
        private float      _hoverEnteredAt;

        private bool        _pressActive;
        private InstanceId  _pressKey;
        private PointerKind _pressKind;
        private float       _pressDownAt;
        private bool        _pressConsumed;

        // Sticky reveal: after a touch hold-read is released the panel stays visible. Any new press
        // on a card clears sticky so the card can be played or re-read without lingering state.
        private bool       _sticky;
        private InstanceId _stickyTarget;

        /// <summary>True while a reveal should be on screen.</summary>
        internal bool Visible { get; private set; }

        /// <summary>The card the reveal is showing. Only meaningful while <see cref="Visible"/>.</summary>
        internal InstanceId Target { get; private set; }

        /// <summary>Which gesture is holding the reveal up.</summary>
        internal RevealSource Source { get; private set; }

        /// <summary>True once the live press has been reinterpreted as a read. Stays true until the
        /// press is released, so <see cref="Release"/> can report it exactly once.</summary>
        internal bool PressBecameRead => _pressConsumed;

        internal void PointerEnter(InstanceId key, float now)
        {
            _hoverActive    = true;
            _hoverKey       = key;
            _hoverEnteredAt = now;
            Resolve(now);
        }

        /// <summary>Ignores an exit for a card that is not the one being hovered. Unity delivers
        /// enter/exit per collider with no ordering guarantee between two cards, so a stale exit
        /// arriving after the neighbouring enter must not cancel the live hover.</summary>
        internal void PointerExit(InstanceId key, float now)
        {
            if (!_hoverActive || !_hoverKey.Equals(key)) return;
            _hoverActive = false;
            Resolve(now);
        }

        internal void PointerDown(InstanceId key, PointerKind kind, float now)
        {
            // Any new press cancels the sticky reveal. The card can now be played normally or held to
            // open a fresh reveal. Clearing here rather than in Release keeps the visible state
            // consistent from the very first frame of the new press.
            _sticky        = false;
            _stickyTarget  = default;
            _pressActive   = true;
            _pressKey      = key;
            _pressKind     = kind;
            _pressDownAt   = now;
            _pressConsumed = false;
            Resolve(now);
        }

        /// <summary>Ends the live press. Returns true when that press had become a read, which the
        /// caller must translate into cancelling the selection the press already made.</summary>
        internal bool Release(float now)
        {
            if (!_pressActive) return false;
            bool wasRead = _pressConsumed;
            if (wasRead && _pressKind == PointerKind.Touch)
            {
                // Promote the completed hold-read to a sticky reveal so the player can lift their
                // finger and still read at leisure. The card rises back into the fan, but the panel
                // stays on screen until the player presses elsewhere or presses the card again.
                _sticky       = true;
                _stickyTarget = _pressKey;
            }
            _pressActive   = false;
            _pressConsumed = false;
            Resolve(now);
            return wasRead;
        }

        /// <summary>Drives the two elapsed-time thresholds. Safe to call every frame.</summary>
        internal void Tick(float now) => Resolve(now);

        /// <summary>Drops everything without reporting a read — for a card leaving the board, a
        /// phase change, or any other reason the reveal must not survive.</summary>
        internal void Cancel()
        {
            _hoverActive   = false;
            _pressActive   = false;
            _pressConsumed = false;
            _sticky        = false;
            _stickyTarget  = default;
            Visible        = false;
            Source         = RevealSource.None;
            Target         = default;
        }

        private void Resolve(float now)
        {
            if (_pressActive && _pressKind == PointerKind.Touch
                && now - _pressDownAt >= HoldSeconds)
            {
                // Latched, not recomputed: once a press has been reinterpreted as a read it stays
                // one until release. Otherwise a reveal dismissed for any other reason would hand
                // the same press back to the play path mid-gesture.
                _pressConsumed = true;
                Visible = true;
                Target  = _pressKey;
                Source  = RevealSource.Hold;
                return;
            }

            if (_hoverActive && now - _hoverEnteredAt >= HoverDelaySeconds)
            {
                Visible = true;
                Target  = _hoverKey;
                Source  = RevealSource.Hover;
                return;
            }

            if (_sticky)
            {
                Visible = true;
                Target  = _stickyTarget;
                Source  = RevealSource.Sticky;
                return;
            }

            Visible = false;
            Source  = RevealSource.None;
            Target  = default;
        }
    }
}
