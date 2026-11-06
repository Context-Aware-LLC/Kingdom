using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Creates the Royal Council board and attaches its player input targets.</summary>
    [RequireComponent(typeof(BoardAnchors))]
    public sealed class ProceduralBoardSetup : MonoBehaviour
    {
        private void Awake()
        {
            var anchors = GetComponent<BoardAnchors>();
            if (RoyalCouncilBoard.TryCreate(transform, anchors) == null)
                throw new MissingReferenceException(
                    "The required Resources/Boards/RoyalCouncil prefab is missing.");
        }

        public void SetupClickables(PlayerId p0Id, PlayerId p1Id, InputRouter router)
        {
            if (router == null) return;
            var anchors = GetComponent<BoardAnchors>();
            AttachSeatClickables(anchors.p0, p0Id);
            AttachSeatClickables(anchors.p1, p1Id);
            anchors.Tabletop?.FitHitTargets(anchors, p0Id, p1Id);
        }

        private static void AttachSeatClickables(SeatAnchors seat, PlayerId ownerId)
        {
            if (seat.HeroPowerAnchor != null)
            {
                var hp = seat.HeroPowerAnchor.gameObject.AddComponent<ClickableHeroPower>();
                hp.Init(ownerId);
            }

            if (seat.HeroAnchor != null)
            {
                var hero = seat.HeroAnchor.gameObject.AddComponent<ClickableHero>();
                hero.Init(ownerId);
            }
        }
    }
}
