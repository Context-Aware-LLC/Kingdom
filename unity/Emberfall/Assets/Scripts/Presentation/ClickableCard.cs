using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to each card root GameObject by BoardController.GetOrCreate.
    // Forwards OnMouseDown to InputRouter; zone is kept current on every tick
    // so hand→board transitions don't require re-creating the component.
    public sealed class ClickableCard : MonoBehaviour
    {
        public InstanceId    CardId    { get; private set; }
        public PlayerId      OwnerId   { get; private set; }
        public Zone          Zone      { get; private set; }
        public CardPresenter Presenter { get; private set; }

        private InputRouter _router;

        public void Init(InstanceId cardId, PlayerId ownerId, Zone zone,
                         CardPresenter presenter, InputRouter router)
        {
            CardId    = cardId;
            OwnerId   = ownerId;
            Zone      = zone;
            Presenter = presenter;
            _router   = router;
        }

        // Called every tick by BoardController to keep zone in sync (hand → board transitions).
        public void UpdateZone(Zone zone) => Zone = zone;

        private void OnMouseDown() => _router?.OnCardClicked(this);
    }
}
