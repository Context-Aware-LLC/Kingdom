using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to the Hero anchor by ProceduralBoardSetup.SetupClickables. Forwards
    // OnMouseDown to InputRouter so heroes are valid click targets: attack the enemy
    // hero ("going face") with a selected minion, or aim a targeted spell at either hero.
    public sealed class ClickableHero : MonoBehaviour
    {
        public PlayerId OwnerId { get; private set; }

        private InputRouter _router;

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(1.15f, 1.15f, 0.1f); // matches HeroPlateView plate quad
            }
        }

        public void Init(PlayerId ownerId, InputRouter router)
        {
            OwnerId = ownerId;
            _router = router;
        }

        private void OnMouseDown() => _router?.OnHeroClicked(OwnerId);
    }
}
