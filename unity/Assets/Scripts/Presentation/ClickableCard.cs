using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    // Attached to each card root GameObject by BoardController.GetOrCreate.
    //
    // A RAYCAST DESCRIPTOR, NOT A CLICK HANDLER (EF-048). It forwarded OnMouseDown to InputRouter
    // until the router became a press/drag/release machine; a Unity mouse message can only say "this
    // was clicked", which cannot express a gesture that begins on one object and ends on another.
    // InputRouter now raycasts once per pointer event and reads identity off whatever it hits, so all
    // this has to do is carry that identity and keep it current.
    //
    // Owner and zone are refreshed on every tick, so a hand→board move and a minion changing sides
    // (TAKE_CONTROL) don't require re-creating the component. The presenter outlives both.
    public sealed class ClickableCard : MonoBehaviour
    {
        public InstanceId    CardId    { get; private set; }
        public PlayerId      OwnerId   { get; private set; }
        public Zone          Zone      { get; private set; }
        public CardPresenter Presenter { get; private set; }

        public void Init(InstanceId cardId, PlayerId ownerId, Zone zone, CardPresenter presenter)
        {
            CardId    = cardId;
            OwnerId   = ownerId;
            Zone      = zone;
            Presenter = presenter;
        }

        // Called every tick by BoardController to keep owner and zone in sync.
        public void UpdateLocation(PlayerId ownerId, Zone zone)
        {
            OwnerId = ownerId;
            Zone    = zone;
        }
    }
}
