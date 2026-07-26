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
                // The hero-power RING, not the frame canvas it is drawn on. The canvas carries
                // 34% transparent margin for the weapon's stat bosses, so sizing this to the
                // canvas would put a click target on empty air and steal the bottom of the
                // weapon ring stacked directly above.
                float d = HeroSocketsView.FrameSize * HeroSocketsView.RingFraction;
                col.size = new Vector3(d, d, 0.1f);
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
