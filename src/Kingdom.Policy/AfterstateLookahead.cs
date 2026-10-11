using System;
using System.Collections.Generic;
using System.Text;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;

namespace Kingdom.Policy
{
    /// <summary>
    /// One-ply lookahead from inside a seat: apply a candidate action to a COPY of the state the
    /// seat can actually see, and hand back the resulting position to be scored.
    ///
    /// WHY THIS EXISTS. The 2026-09-09 counterfactual study found the policy head and the value
    /// head disagree — the policy's argmax is the critic's top-ranked play only 29.4% of the time
    /// against a 25.3% random baseline, and at p >= 0.999 the played action wins a pairwise value
    /// comparison just 54.3% of the time. Every generation-2 update improved the critic and moved
    /// the policy not at all, so the learning is sitting in the head that does not choose. This is
    /// the falsifiable test of that: same weights, different head driving the decision.
    ///
    /// THE COPY IS A WIRE ROUND TRIP, deliberately. <see cref="CommandProcessor.Execute"/> mutates
    /// the state it is given, so each candidate needs its own. The engine exposes no clone and the
    /// canonical serializer is write-only, and a hand-written deep copy of game state is exactly
    /// the thing CLAUDE.md warns about — it drifts from the real shape silently and nothing fails
    /// until a hash moves. Encoding through <see cref="SnapshotMsg"/> and decoding back reuses the
    /// codec the client already trusts for every frame of every match, so a field this copy would
    /// drop is a field the client would already be losing.
    ///
    /// IT IS A PROJECTION, NOT THE TRUTH. A seat sees its own view, so a lookahead that resolves a
    /// draw reveals a hidden card as a blank rather than the real one. That is a fair constraint —
    /// it is the information the player has — but it does mean this diverges slightly from the
    /// offline study, which branched from authoritative state. Actions whose outcome depends on
    /// hidden information are evaluated on a view where that information is absent.
    /// </summary>
    internal static class AfterstateLookahead
    {
        /// <summary>
        /// An independent copy of <paramref name="state"/>, obtained by round-tripping the snapshot
        /// wire form. Returns null when the state does not survive the trip, which the caller must
        /// treat as "cannot evaluate this candidate" rather than as a value of zero.
        /// </summary>
        public static GameState? Copy(GameState state, MatchId match)
        {
            byte[] envelope = ProtocolCodec.Encode(SnapshotMsg.Of(state), match, 0UL);
            DecodeResult decoded = ProtocolCodec.Decode(envelope);
            if (!decoded.Ok || decoded.Message is not SnapshotMsg snapshot) return null;
            return snapshot.State;
        }

        /// <summary>
        /// Applies one candidate to a copy and reports the position it produces. A rejected intent
        /// is reported rather than thrown: the enumerator works from the authoritative state in
        /// some callers and a projection in others, and a candidate that is legal in one and not
        /// the other is a fact about this seam, not a crash.
        /// </summary>
        public static Afterstate? Apply(GameState state, PlayerIntent candidate, MatchId match, ICardDatabase db)
        {
            GameState? copy = Copy(state, match);
            if (copy is null) return null;
            var processor = new CommandProcessor(db);
            CommandResult result;
            try
            {
                result = processor.Execute(copy, candidate);
            }
            catch (Exception)
            {
                // Resolving against a projection can reach a card path that assumes authoritative
                // data. Losing one candidate is survivable; taking down a live match seat is not.
                return null;
            }
            if (!result.Accepted) return null;
            return new Afterstate(copy, copy.Outcome != MatchOutcome.InProgress);
        }

        public readonly record struct Afterstate(GameState State, bool Terminal);

        /// <summary>
        /// True when the position contains a card the exporter's encoder will refuse.
        ///
        /// THIS IS THE REAL COST OF LOOKING AHEAD FROM A PROJECTION, and it is larger than it
        /// sounds. A seat's own deck is redacted in its own view, so resolving any action that
        /// DRAWS produces a card with instance id 0 sitting in the actor's own hand. The encoder
        /// rejects that outright — `visible entity 'self_hand_4' must have a non-redacted instance
        /// id` — and it is right to: a blank in one's own hand never occurs in training, so any
        /// value the critic returned for such a position would be an extrapolation dressed as a
        /// measurement.
        ///
        /// So draws, and every mulligan, are simply not evaluable one ply ahead from inside a seat.
        /// The honest response is to skip those candidates and say what fraction were skipped, not
        /// to synthesise an id and quietly score an out-of-distribution position.
        /// </summary>
        public static bool HasRedactedVisibleEntity(GameState state, PlayerId actor)
        {
            foreach (PlayerState player in state.Players)
            {
                foreach (CardInstance card in player.Board) if (card.Id.Value == 0) return true;
                if (player.Weapon is not null && player.Weapon.Id.Value == 0) return true;
            }
            PlayerState self = state.Players[state.SeatOf(actor)];
            foreach (CardInstance card in self.Hand) if (card.Id.Value == 0) return true;
            return false;
        }

        /// <summary>Actor-relative terminal return: +1 win, -1 loss, 0 otherwise.</summary>
        public static double TerminalValue(GameState state, PlayerId actor) => state.Outcome switch
        {
            MatchOutcome.Player0Win => state.SeatOf(actor) == 0 ? 1.0 : -1.0,
            MatchOutcome.Player1Win => state.SeatOf(actor) == 1 ? 1.0 : -1.0,
            _ => 0.0,
        };
    }
}
