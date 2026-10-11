using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    // Attached to the Hero anchor by ProceduralBoardSetup.SetupClickables.
    //
    // A raycast descriptor for InputRouter's press/drag/release machine (EF-048). A hero is both a
    // drag SOURCE (our own, swinging an equipped weapon) and a drag TARGET (going face with a minion,
    // or aiming a spell at either hero) — which is exactly the pair a click machine could not
    // distinguish without a mode, and a gesture with a beginning and an end can.
    public sealed class ClickableHero : MonoBehaviour
    {
        public PlayerId OwnerId { get; private set; }

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(HeroPlateView.PlateW, HeroPlateView.PlateH, 0.1f);
            }
        }

        public void Init(PlayerId ownerId) => OwnerId = ownerId;
    }
}
