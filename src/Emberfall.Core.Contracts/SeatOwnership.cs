namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// How many seats a <c>MatchRunner</c> drives, and which one it renders from
    /// (dev-docs/contracts/wire-protocol.md:14
    /// — Architect-ruled 2026-07-17). This RETIRES the emergent "same session instance at both
    /// indices + inert remote source" invariant B3's Reality-Checker pass found load-bearing in
    /// three places, whose violation was a mirrored board rather than a crash.
    ///
    /// The axis is deliberately "how many seats does this runner drive", NOT "which seat is local":
    /// a <see cref="Both"/> hot-seat legitimately drives both seats and "the local seat" is
    /// meaningless there, which is why an earlier <c>localSeat: int</c> shape (defaulting to 0)
    /// was rejected — it would have broken hot-seat's seat-1 mulligan.
    ///
    /// UnityEngine-free and pure: the whole point is that <see cref="ViewSeat"/>, <see cref="Owns"/>,
    /// and <see cref="PickMulligan"/> are unit-testable from <c>dotnet test</c>, so the seat-1
    /// networked path stops being an untested coincidence.
    /// </summary>
    public abstract record SeatOwnership
    {
        /// <summary>Hot-seat / vs-AI: one runner drives both seats. The DEFAULT, and bit-identical
        /// to the pre-ruling behaviour (view seat 0, owns both, mulligan 0-then-1).</summary>
        public static readonly SeatOwnership Both = new BothSeats();

        /// <summary>Networked: this runner drives exactly <paramref name="seat"/> (0 or 1); the
        /// other index carries an inert remote session whose source never fires.</summary>
        public static SeatOwnership Single(int seat) => new SingleSeat(seat);

        /// <summary>Which session index drives presentation (was the hardcoded <c>_sessions[0]</c>).</summary>
        public abstract int ViewSeat { get; }

        /// <summary>Whether index <paramref name="seat"/> receives the <c>OnSnapshot</c> fan-out to
        /// its intent source. <see cref="Both"/> owns both; <see cref="Single"/> owns only its seat,
        /// so a non-inert remote source can never be fed a stream for a seat it does not own.</summary>
        public abstract bool Owns(int seat);

        /// <summary>The mulligan-phase seat to poll: <see cref="Both"/> keeps the client-serialized
        /// <c>0-then-1</c> walk; <see cref="Single"/> returns its own seat until that seat's mulligan
        /// is done (then -1), which is what makes a simultaneous networked mulligan buildable — each
        /// client drives only its own confirm instead of seat 1 idling on seat 0 (RC-F11).</summary>
        public abstract int PickMulligan(GameState state);

        private static bool MulliganDone(GameState s, int seat) => seat == 0 ? s.Mulligan0Done : s.Mulligan1Done;

        private sealed record BothSeats : SeatOwnership
        {
            public override int ViewSeat => 0;
            public override bool Owns(int seat) => true;
            public override int PickMulligan(GameState state)
            {
                if (!state.Mulligan0Done) return 0;
                if (!state.Mulligan1Done) return 1;
                return -1;
            }
        }

        private sealed record SingleSeat : SeatOwnership
        {
            private readonly int _seat;
            public SingleSeat(int seat)
            {
                if (seat is not (0 or 1))
                    throw new System.ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0 or 1.");
                _seat = seat;
            }

            public override int ViewSeat => _seat;
            public override bool Owns(int seat) => seat == _seat;
            public override int PickMulligan(GameState state) => MulliganDone(state, _seat) ? -1 : _seat;
        }
    }
}
