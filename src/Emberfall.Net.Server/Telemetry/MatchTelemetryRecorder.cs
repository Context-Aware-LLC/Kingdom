using System;
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// What one match tells telemetry (telemetry-spec §1 and §2), kept apart from the wire so that
    /// anything driving the rules core produces the same rows: <c>MatchSession</c> for a live match,
    /// and NetSoak's direct capture, which plays on <c>LocalMatchHost</c> with no session at all.
    ///
    /// <para><b>Everything here is OBSERVED from outside the rules core</b>, at four moments the
    /// driver owns: after the opening deal, before a submit, after an accepted submit, and at the
    /// end. The order matters and is the caller's to keep. <see cref="BeforeSubmit"/> has to run
    /// before the core sees the intent, because afterwards the hand has been rewritten and the
    /// answer is gone.</para>
    /// </summary>
    public sealed class MatchTelemetryRecorder
    {
        /// <summary>What <see cref="BeforeSubmit"/> read and <see cref="AfterAccepted"/> records.
        /// Held aside rather than stored, so a REJECTED intent records nothing.</summary>
        public readonly struct Pending
        {
            internal Pending((int seat, List<int> kept)? keeps, (int seat, int defId, int turn)? play)
            {
                Keeps = keeps;
                Play = play;
            }

            internal (int seat, List<int> kept)? Keeps { get; }
            internal (int seat, int defId, int turn)? Play { get; }
        }

        private readonly MatchId _id;
        private readonly MatchSetup _setup;
        private readonly ITelemetrySink _telemetry;
        private readonly MatchAttribution _attribution;

        /// <summary>
        /// Card definition ids each seat KEPT at mulligan (telemetry-spec §1 `mulliganKept`).
        /// Captured at submit time because it is unrecoverable afterwards: the core replaces
        /// discarded cards in place, so the post-mulligan hand cannot say what was thrown back.
        /// </summary>
        private readonly List<int>[] _mulliganKept = { new List<int>(), new List<int>() };

        /// <summary>
        /// Per-seat `card_seen` accumulators (telemetry-spec §2). Fed by <see cref="ObserveHands"/>
        /// after the opening deal and after every accepted command; read once, at match end.
        /// </summary>
        private readonly CardSeenLedger[] _cardsSeen = { new CardSeenLedger(), new CardSeenLedger() };

        public MatchTelemetryRecorder(
            MatchId id, MatchSetup setup, ITelemetrySink? telemetry, MatchAttribution? attribution)
        {
            _id = id;
            _setup = setup;
            _telemetry = telemetry ?? NullTelemetrySink.Instance;
            _attribution = attribution ?? MatchAttribution.None;
        }

        /// <summary>
        /// The `inDeck` denominator, and the opening hands — which are the only draws that happen
        /// before any command exists to observe after (telemetry-spec §2).
        /// </summary>
        public void AfterDeal(GameState state)
        {
            _cardsSeen[0].NoteDeck(_setup.P0Deck);
            _cardsSeen[1].NoteDeck(_setup.P1Deck);
            ObserveHands(state);
        }

        /// <summary>Read the mulligan keeps and the played card's def id while the hand still holds them.</summary>
        public Pending BeforeSubmit(GameState state, PlayerIntent intent)
            => new Pending(CaptureMulliganKeeps(state, intent), CapturePlay(state, intent));

        /// <summary>
        /// Every accepted command is a draw observation point. Run it BEFORE ending the match, so
        /// the last command's draws are still in the sample.
        /// </summary>
        public void AfterAccepted(GameState state, Pending pending)
        {
            if (pending.Keeps is { } k) _mulliganKept[k.seat] = k.kept;
            if (pending.Play is { } p) _cardsSeen[p.seat].NotePlayed(p.defId, p.turn);
            ObserveHands(state);
        }

        /// <summary>
        /// The balance sample (telemetry-spec §1), then the per-card sample (§2) in one batched
        /// call: a reader tailing the file sees the result before the cards that explain it, and
        /// each has its own Emit so a failure in either cannot cost the other. Reads hero class out
        /// of live state, so call it before anything tears the match down.
        /// </summary>
        public void Complete(GameState state, DateTimeOffset endedAtUtc, MatchEndReason reason)
        {
            var outcome = state.Outcome;
            Emit(() => _telemetry.MatchComplete(new MatchCompleteEvent(
                _id, endedAtUtc, reason, outcome, state.TurnNumber, _attribution.Source,
                SeatBlock(state, 0, outcome), SeatBlock(state, 1, outcome))));
            Emit(() => _telemetry.CardsSeen(new CardSeenEvent(
                _id, endedAtUtc, _attribution.Source, CardRows(state, outcome))));
        }

        /// <summary>Decision 24: the match ended with no result.</summary>
        public void Void(GameState state, DateTimeOffset endedAtUtc)
            => Emit(() => _telemetry.MatchVoid(new MatchVoidEvent(
                _id, endedAtUtc, state.TurnNumber, _attribution.Source,
                _attribution.P0, _attribution.P1)));

        /// <summary>
        /// The card definition ids this intent would KEEP, if it is a mulligan resolution.
        /// <c>AutoConfirmIntent</c> keeps the whole hand — that is what auto-confirm means, and a
        /// timed-out mulligan is still a real data point about what the opening hand was.
        /// </summary>
        private static (int seat, List<int> kept)? CaptureMulliganKeeps(GameState state, PlayerIntent intent)
        {
            if (state.Phase != TurnPhase.Mulligan) return null;
            if (intent is not MulliganIntent && intent is not AutoConfirmIntent) return null;

            int seat = state.SeatOf(intent.Player);
            var replaced = intent as MulliganIntent;
            var kept = new List<int>();
            foreach (var card in state.Players[seat].Hand)
            {
                bool thrown = false;
                if (replaced is not null)
                    foreach (var id in replaced.ToReplace)
                        if (id == card.Id) { thrown = true; break; }
                if (!thrown) kept.Add(card.DefinitionId);
            }
            return (seat, kept);
        }

        /// <summary>
        /// The def id behind a <see cref="PlayCardIntent"/>, resolved from the seat's hand while the
        /// card is still in it. Null for anything else, and for an instance id that is not in that
        /// seat's hand — a spoofed or stale id is the core's business to reject, and telemetry must
        /// not record a play the core is about to refuse.
        /// </summary>
        private static (int seat, int defId, int turn)? CapturePlay(GameState state, PlayerIntent intent)
        {
            if (intent is not PlayCardIntent play) return null;
            if (state.Outcome != MatchOutcome.InProgress) return null;

            int seat = state.SeatOf(play.Player);
            foreach (var card in state.Players[seat].Hand)
                if (card.Id == play.Card)
                    return (seat, card.DefinitionId, state.TurnNumber);
            return null;
        }

        /// <summary>
        /// Mark everything currently in either hand as drawn (telemetry-spec §2 `drawnThisMatch`).
        /// Cheap and unconditional: two ≤10-card lists, and skipping it when nothing looks like a
        /// draw would mean re-deriving which effects draw — exactly the coupling to card behaviour
        /// this observational design avoids.
        /// </summary>
        private void ObserveHands(GameState state)
        {
            for (int seat = 0; seat < 2; seat++)
                _cardsSeen[seat].NoteHand(state.Players[seat].Hand);
        }

        /// <summary>
        /// Two guards around every emit, both load-bearing.
        ///
        /// <para><b>Unattributed matches are dropped, not written blank.</b> A row keyed on an empty
        /// account is worse than a missing row: it silently pollutes the per-class win-rate sample
        /// the C4 exit bar is judged on, and it looks like real data. <see cref="Identity.AccountId"/>'s
        /// own contract says the emitter refuses these — this is where that promise is kept.</para>
        ///
        /// <para><b>A throwing sink never kills a finished match.</b> The match is already over and
        /// correct by the time we get here; losing a telemetry row costs one sample, while an
        /// escaping exception would take down the session pump and, with it, whatever else it was
        /// about to do.</para>
        /// </summary>
        private void Emit(System.Action emit)   // qualified: Contracts has its own `Action` (a card verb)
        {
            if (!_attribution.IsAttributed) return;
            try { emit(); }
            catch (Exception) { /* a dropped sample is not worth a dropped match */ }
        }

        private SeatOutcome SeatBlock(GameState state, int seat, MatchOutcome outcome)
        {
            var deck = seat == 0 ? _setup.P0Deck : _setup.P1Deck;
            var @class = state.Players[seat].Hero.Class;
            return new SeatOutcome(
                seat == 0 ? _attribution.P0 : _attribution.P1,
                @class,
                DeckIdentity.IdFor(@class),
                DeckIdentity.HashOf(deck),
                ResultFor(seat, outcome),
                _mulliganKept[seat]);
        }

        /// <summary>
        /// Both seats' `card_seen` rows, seat 0 first (telemetry-spec §2).
        ///
        /// <para><b>A draw is not a win for either seat.</b> `matchWon` is the win-when-drawn
        /// NUMERATOR, and a draw is a match nobody won — crediting it to both would inflate every
        /// card in it past the 58% centralization threshold §3 gates on.</para>
        /// </summary>
        private IReadOnlyList<CardSeenRow> CardRows(GameState state, MatchOutcome outcome)
        {
            var rows = new List<CardSeenRow>();
            for (int seat = 0; seat < 2; seat++)
            {
                rows.AddRange(_cardsSeen[seat].Rows(
                    seat == 0 ? _attribution.P0 : _attribution.P1,
                    state.Players[seat].Hero.Class,
                    ResultFor(seat, outcome) == SeatResult.Win,
                    _mulliganKept[seat]));
            }
            return rows;
        }

        private static SeatResult ResultFor(int seat, MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Player0Win => seat == 0 ? SeatResult.Win : SeatResult.Loss,
            MatchOutcome.Player1Win => seat == 1 ? SeatResult.Win : SeatResult.Loss,
            _ => SeatResult.Draw,
        };
    }
}
