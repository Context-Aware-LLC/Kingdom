using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Implements IBoardAnchors: maps (PlayerId, Zone, slotIndex) to named scene transforms.
    // Anchor data is authored in the editor (via Inspector) OR assigned at runtime by
    // ProceduralBoardSetup — both paths are supported.
    // Call SetPlayers(p0Id, p1Id) before the first AnchorFor query.
    public sealed class BoardAnchors : MonoBehaviour, IBoardAnchors
    {
        [Header("Seat 0 (bottom / local player)")]
        [SerializeField] internal SeatAnchors p0 = new SeatAnchors();

        [Header("Seat 1 (top / opponent)")]
        [SerializeField] internal SeatAnchors p1 = new SeatAnchors();

        private PlayerId _p0Id;
        private PlayerId _p1Id;
        private bool _playersSet;

        public void SetPlayers(PlayerId p0Id, PlayerId p1Id)
        {
            _p0Id = p0Id;
            _p1Id = p1Id;
            _playersSet = true;
        }

        // Re-solves this seat's ten hand anchors for the given hand count. Cheap and idempotent
        // (ten transform writes), so BoardController calls it every tick rather than tracking
        // the previous count — a stale fan is a worse failure than a redundant write.
        //
        // mulliganRow: this seat is choosing its mulligan, so its hand is laid out as the head-on
        // row (MulliganRowLayout, #401) instead of the fan. The row is solved against the live
        // camera, which the safe-area fit can move after the snapshot lands, so it is re-solved
        // every LateUpdate while it is up — the same reason UnearthChoiceView re-lays itself out.
        public void SetHandCount(PlayerId player, int count, bool mulliganRow = false)
        {
            SeatAnchors seat = ResolveSeat(player);
            if (mulliganRow)
            {
                _rowSeat  = seat;
                _rowCount = count;
                if (MulliganRowLayout.Apply(seat.HandSlots, count, Camera.main)) return;
            }
            if (_rowSeat == seat) _rowSeat = null;
            ApplyFan(seat, count);
        }

        /// <summary>True while <paramref name="player"/>'s hand is drawn as the mulligan row
        /// rather than the fan. BoardController reads this to decide whether hand cards take the
        /// hover lift, which the row has no room for.</summary>
        public bool IsMulliganRow(PlayerId player) => _rowSeat != null && _rowSeat == ResolveSeat(player);

        private SeatAnchors _rowSeat;
        private int _rowCount;

        private static void ApplyFan(SeatAnchors seat, int count)
        {
            // The row scales the slots; the fan never does, so it is the fan's job to undo it.
            if (seat.HandSlots != null)
                foreach (Transform slot in seat.HandSlots)
                    if (slot != null) slot.localScale = Vector3.one;
            HandFanLayout.Apply(seat.HandSlots, count, seat.FanMirrored);
        }

        private void LateUpdate()
        {
            if (_rowSeat == null) return;
            if (MulliganRowLayout.Apply(_rowSeat.HandSlots, _rowCount, Camera.main)) return;
            SeatAnchors seat = _rowSeat;   // the band collapsed under a camera change: back to the fan
            _rowSeat = null;
            ApplyFan(seat, _rowCount);
        }

        // Re-solves this seat's board-row anchors so the occupied slots center in the row rather
        // than pack against the leftmost ones (Sam, 2026-08-09). Same idempotent-every-tick shape
        // as SetHandCount, for the same reason: cheap, and a stale layout is worse than a
        // redundant write.
        public void SetBoardCount(PlayerId player, int count)
        {
            SeatAnchors seat = ResolveSeat(player);
            BoardRowLayout.Apply(seat.BoardSlots, count);
        }

        public Transform AnchorFor(PlayerId player, Zone zone, int slotIndex)
        {
            SeatAnchors seat = ResolveSeat(player);
            return zone switch
            {
                Zone.Hand    => SafeSlot(seat.HandSlots,  slotIndex, 10),
                Zone.Board   => SafeSlot(seat.BoardSlots, slotIndex, 7),
                Zone.Deck    => seat.DeckAnchor,
                Zone.Weapon  => seat.WeaponAnchor,
                _            => transform,
            };
        }

        // Artwork registration may place both seats above world y=0. Visual ownership must
        // follow the player mapping, never the current position of a painted fixture.
        public bool IsLocalPlayer(PlayerId player) => ResolveSeat(player) == p0;

        public Transform HeroAnchor(PlayerId player)      => ResolveSeat(player).HeroAnchor;
        public Transform HeroPowerAnchor(PlayerId player) => ResolveSeat(player).HeroPowerAnchor;
        public Transform ManaTrayAnchor(PlayerId player)  => ResolveSeat(player).ManaTrayAnchor;
        public Transform WeaponAnchor(PlayerId player)    => ResolveSeat(player).WeaponAnchor;

        private SeatAnchors ResolveSeat(PlayerId player)
        {
            if (!_playersSet)
            {
                Debug.LogWarning("[BoardAnchors] SetPlayers() not called yet; defaulting to P0.");
                return p0;
            }
            return (_p0Id == player) ? p0 : p1;
        }

        private static Transform SafeSlot(Transform[] slots, int index, int max)
        {
            if (slots == null || slots.Length == 0) return null;
            return slots[Mathf.Clamp(index, 0, max - 1)];
        }
    }
}
