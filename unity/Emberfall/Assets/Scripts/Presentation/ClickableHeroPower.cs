using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to the HeroPower anchor by ProceduralBoardSetup.SetupClickables.
    // Forwards OnMouseDown to InputRouter; InputRouter enqueues HeroPowerIntent([])
    // and the engine resolves the target via DefaultTargets (Phase C step 5 will
    // add a proper targeting cursor for spells like Searing Bolt).
    public sealed class ClickableHeroPower : MonoBehaviour
    {
        public PlayerId OwnerId { get; private set; }

        private InputRouter _router;

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                // Matches HeroPlateView.DiscSize — the 0.9Ø disc of §1.1's hero cluster. Was
                // 0.85 x 1.05, a card-shaped box left over from the placeholder layout, which
                // now overlaps the weapon disc stacked 0.9 above it.
                col.size = new Vector3(0.9f, 0.9f, 0.1f);
            }
        }

        public void Init(PlayerId ownerId, InputRouter router)
        {
            OwnerId = ownerId;
            _router = router;
        }

        private void OnMouseDown() => _router?.OnHeroPowerClicked(OwnerId);
    }
}
