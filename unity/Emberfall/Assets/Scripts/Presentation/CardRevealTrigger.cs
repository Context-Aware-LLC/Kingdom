using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. Per-card pointer relay for the reveal layer. Sits on the card ROOT, alongside
    /// ClickableCard and HandCardHover, because that is where the BoxCollider is.
    ///
    /// <para>Only enter / exit / down are relayed. Release is polled by
    /// <see cref="CardRevealController"/> rather than taken from <c>OnMouseUp</c> — a card played
    /// from hand has its collider destroyed mid-press, and the up event that never arrives would
    /// strand the panel on screen.</para>
    ///
    /// <para>Attached to every card the local player can legitimately read: their own hand, and
    /// both boards. Hidden cards (the opponent's projected hand) never reach this component at all
    /// — BoardController's card-back path skips the definition bind, so there is nothing to
    /// describe and no way for the reveal to leak hidden information.</para>
    /// </summary>
    internal sealed class CardRevealTrigger : MonoBehaviour
    {
        internal InstanceId CardId { get; private set; }

        /// <summary>The click handler on the same root, so the controller can hand a completed read
        /// back to InputRouter to undo whatever the same press already did. Null in modes that run
        /// without an InputRouter at all (replay playback), where there is nothing to undo.</summary>
        internal ClickableCard Clickable { get; private set; }

        private CardRevealController _controller;
        private CardDefinition       _def;
        private ICardLiveState       _live;

        internal void Init(InstanceId cardId, CardDefinition def, ICardLiveState live,
                           ClickableCard clickable, CardRevealController controller)
        {
            CardId      = cardId;
            _def        = def;
            _live       = live;
            Clickable   = clickable;
            _controller = controller;
            _controller?.Register(this);
        }

        /// <summary>Composed on demand rather than cached: stats and keywords change under a
        /// minion while the panel is up (a buff resolving mid-hold is the case that matters), and a
        /// panel showing what was true when the finger landed is a panel that lies.</summary>
        internal CardRevealContent BuildContent() => CardRevealContent.Build(_def, _live);

        /// <summary>Raises this card out of the fan for a touch read. No-op on a board card, whose
        /// hover component is disabled and which is not clipped by the screen edge anyway.</summary>
        internal void SetLift(bool lifted)
        {
            if (_hover == null) _hover = GetComponent<HandCardHover>();
            if (_hover != null) _hover.SetExternalReveal(lifted);
        }

        private HandCardHover _hover;

        private void OnMouseEnter() => _controller?.OnPointerEnter(this);
        private void OnMouseExit()  => _controller?.OnPointerExit(this);
        private void OnMouseDown()  => _controller?.OnPointerDown(this);

        private void OnDestroy() => _controller?.Unregister(this);
    }
}
