using System;
using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.King;

namespace Kingdom.Net.Server.Telemetry
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
            internal Pending((int seat, List<int> kept, int replaced)? keeps,
                             (int seat, int defId, int turn)? play, int? heroPowerSeat,
                             (int seat, int unspent)? endTurn, BoardWatch watch,
                             FindingsObserver.Before findings)
            {
                Keeps = keeps;
                Play = play;
                HeroPowerSeat = heroPowerSeat;
                EndTurn = endTurn;
                Watch = watch;
                Findings = findings;
            }

            internal (int seat, List<int> kept, int replaced)? Keeps { get; }
            internal (int seat, int defId, int turn)? Play { get; }
            internal int? HeroPowerSeat { get; }
            /// <summary>The seat an <see cref="EndTurnIntent"/> ends, and the mana it left unspent.</summary>
            internal (int seat, int unspent)? EndTurn { get; }
            internal BoardWatch Watch { get; }
            internal FindingsObserver.Before Findings { get; }
        }

        /// <summary>
        /// Where every card was just before a command, which is what lets <see cref="MinionDeaths"/>
        /// tell a minion that died on the board from every other way a card reaches a graveyard —
        /// a spell resolving, a card destroyed in hand, a card burned off the deck.
        /// </summary>
        internal sealed class BoardWatch
        {
            /// <summary>The death clock before the command: anything stamped at or past it died in it.</summary>
            public int DeathClock;
            public readonly HashSet<InstanceId> OnBoard = new HashSet<InstanceId>();
            public readonly HashSet<InstanceId> InHandOrDeck = new HashSet<InstanceId>();
            /// <summary>The card this command plays, if it plays one: in hand before, possibly a
            /// minion on the board and dead again before the command is over.</summary>
            public InstanceId? Played;
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

        /// <summary>Board minions each seat has LOST. A seat's <see cref="SeatOutcome.EnemyMinionsDestroyed"/>
        /// is the other seat's entry here.</summary>
        private readonly int[] _minionsLost = new int[2];

        /// <summary>Cards each seat threw back at mulligan (<see cref="SeatOutcome.MulliganReplaced"/>).</summary>
        private readonly int[] _mulliganReplaced = new int[2];

        /// <summary>Accepted Hero Power uses per seat (<see cref="SeatOutcome.HeroPowersUsed"/>).</summary>
        private readonly int[] _heroPowersUsed = new int[2];

        /// <summary>Cards each seat drew into a full hand (<see cref="SeatOutcome.CardsBurned"/>).</summary>
        private readonly int[] _cardsBurned = new int[2];

        /// <summary>Turns the clock ended for each seat (<see cref="SeatOutcome.TurnsTimedOut"/>).</summary>
        private readonly int[] _turnsTimedOut = new int[2];

        /// <summary>Turns each seat ended with 10 mana unspent (<see cref="SeatOutcome.TurnsEndedWithTenUnspent"/>).</summary>
        private readonly int[] _turnsEndedWithTenUnspent = new int[2];

        /// <summary>Mana left at each of the seat's turn ends, summed (<see cref="SeatOutcome.UnspentMana"/>).</summary>
        private readonly int[] _unspentMana = new int[2];

        /// <summary>The Council's Findings read off each command's events.</summary>
        private readonly FindingsObserver _findings;

        /// <summary>Each seat's policy identity, written on its <c>match_seat</c> row.</summary>
        private readonly string?[] _policies = new string?[2];

        /// <param name="db">The card data the findings read a card's cost, rarity and type from.
        /// Null leaves those findings at none, which only a test that does not reach them should pass.</param>
        public MatchTelemetryRecorder(
            MatchId id, MatchSetup setup, ITelemetrySink? telemetry, MatchAttribution? attribution,
            ICardDatabase? db = null, string? p0Policy = null, string? p1Policy = null)
        {
            _policies[0] = p0Policy;
            _policies[1] = p1Policy;
            _findings = new FindingsObserver(db);
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
            _findings.Observe(state);
        }

        /// <summary>Read the mulligan keeps and the played card's def id while the hand still holds them.</summary>
        public Pending BeforeSubmit(GameState state, PlayerIntent intent)
            => new Pending(CaptureMulliganKeeps(state, intent), CapturePlay(state, intent),
                           CaptureHeroPower(state, intent), CaptureEndTurn(state, intent), Watch(state, intent),
                           FindingsObserver.Snapshot(state, intent));

        /// <summary>
        /// Every accepted command is a draw observation point. Run it BEFORE ending the match, so
        /// the last command's draws are still in the sample.
        /// </summary>
        /// <param name="events">The command's events, which are the only place a card burned from a
        /// full hand shows: it reaches the graveyard by the same path a milled card does. Null counts
        /// no burns.</param>
        /// <param name="byClock">The turn clock submitted this command, not the player.</param>
        public void AfterAccepted(GameState state, Pending pending,
                                  IReadOnlyList<GameEvent>? events = null, bool byClock = false)
        {
            if (pending.Keeps is { } k)
            {
                _mulliganKept[k.seat] = k.kept;
                _mulliganReplaced[k.seat] = k.replaced;
            }
            if (pending.Play is { } p) _cardsSeen[p.seat].NotePlayed(p.defId, p.turn);
            if (pending.HeroPowerSeat is int heroPowerSeat) _heroPowersUsed[heroPowerSeat]++;
            if (pending.EndTurn is { } end)
            {
                if (byClock) _turnsTimedOut[end.seat]++;
                if (end.unspent >= 10) _turnsEndedWithTenUnspent[end.seat]++;
                _unspentMana[end.seat] += end.unspent;
            }
            ObserveHands(state);
            for (int seat = 0; seat < 2; seat++) _minionsLost[seat] += MinionDeaths(state, pending.Watch, seat);
            if (events != null) CountBurns(state, events);
            _findings.After(state, pending.Findings, events);
        }

        /// <summary>A burned card goes to its owner's graveyard, so that is where its seat is read.</summary>
        private void CountBurns(GameState state, IReadOnlyList<GameEvent> events)
        {
            foreach (GameEvent e in events)
            {
                if (e.Kind != GameEventKind.CardDrawn || (e.Flags & EventFlags.Overdraw) == 0) continue;
                if (e.Source is not InstanceId burned) continue;
                for (int seat = 0; seat < 2; seat++)
                    if (state.Players[seat].Graveyard.Exists(c => c.Id == burned)) { _cardsBurned[seat]++; break; }
            }
        }

        /// <summary>
        /// <paramref name="seat"/>'s exact commission numbers so far (#421). Live: read it after any
        /// accepted command for the score as it builds, and at the end for the score that banks,
        /// which is the same numbers <see cref="SeatBlock"/> writes on the seat's outcome.
        /// </summary>
        internal FindingsObserver.Tally TallyFor(int seat)
            => new FindingsObserver.Tally(
                _findings.OverkillBest[seat],
                _findings.SelfDamage[seat],
                _unspentMana[seat],
                _findings.FatigueDamage[seat],
                _cardsBurned[seat],
                _findings.TollDamage[seat],
                _findings.StolenDamage[seat],
                _findings.HeroPowerDamage[seat]);

        internal static BoardWatch Watch(GameState state, PlayerIntent intent)
        {
            var watch = new BoardWatch { DeathClock = state.NextDeathSequence };
            if (intent is PlayCardIntent play) watch.Played = play.Card;
            foreach (PlayerState ps in state.Players)
            {
                foreach (CardInstance c in ps.Board) watch.OnBoard.Add(c.Id);
                foreach (CardInstance c in ps.Hand) watch.InHandOrDeck.Add(c.Id);
                foreach (CardInstance c in ps.Deck) watch.InHandOrDeck.Add(c.Id);
            }
            return watch;
        }

        /// <summary>
        /// How many board minions <paramref name="seat"/> lost to this command. Observational, like the rest of
        /// this class: it reads the two graveyards rather than the event stream, so it does not
        /// have to know which effects kill.
        ///
        /// <para>A graveyard is append-only in death order, so the cards this command sent there
        /// are the tail stamped at or past the clock read beforehand. One of them was a board minion
        /// if it was on a board before the command, or if it has a minion's Health and either is the
        /// card the command played or did not exist beforehand (a token summoned and killed in one
        /// resolution). That leaves out exactly the non-deaths: a resolved spell, a card destroyed
        /// in hand, a card burned off the deck.</para>
        /// </summary>
        internal static int MinionDeaths(GameState state, BoardWatch watch, int seat)
        {
            int died = 0;
            List<CardInstance> graveyard = state.Players[seat].Graveyard;
            for (int i = graveyard.Count - 1; i >= 0; i--)
            {
                CardInstance c = graveyard[i];
                if (c.DiedAtSequence < watch.DeathClock) break;

                bool arrivedThisCommand = c.Id == watch.Played || !watch.InHandOrDeck.Contains(c.Id);
                if (watch.OnBoard.Contains(c.Id) || (arrivedThisCommand && c.MaxHealth > 0))
                    died++;
            }
            return died;
        }

        /// <summary>
        /// The balance sample (telemetry-spec §1), then the per-card sample (§2) in one batched
        /// call: a reader tailing the file sees the result before the cards that explain it, and
        /// each has its own Emit so a failure in either cannot cost the other. Reads hero class out
        /// of live state, so call it before anything tears the match down.
        /// </summary>
        public void Complete(GameState state, DateTimeOffset endedAtUtc, MatchEndReason reason)
            => Complete(state, Summarize(state, endedAtUtc, reason));

        /// <summary>
        /// The <c>match_complete</c> row, built but not emitted — <c>MatchSession</c> reads the
        /// Council's Findings off it for the match-end message before the row is written. Built
        /// whether or not the match is attributed; only <see cref="Emit"/> drops unattributed rows.
        /// </summary>
        public MatchCompleteEvent Summarize(GameState state, DateTimeOffset endedAtUtc, MatchEndReason reason)
        {
            var outcome = state.Outcome;
            return new MatchCompleteEvent(
                _id, endedAtUtc, reason, outcome, state.TurnNumber, _attribution.Source,
                SeatBlock(state, 0, outcome), SeatBlock(state, 1, outcome));
        }

        /// <summary>
        /// Emit a row <see cref="Summarize"/> built, then the per-card sample. <paramref name="king"/>
        /// goes only to an <see cref="AchievementSink"/>, which banks the Favor; it stays off the row
        /// because the row is written to telemetry and the seating carries the players' names.
        /// The AchievementSink must therefore be the sink this recorder holds, not one wrapped inside
        /// another: <c>MatchGate</c>'s constructor enforces that whenever it is given a Favor store.
        /// </summary>
        public void Complete(GameState state, MatchCompleteEvent complete, KingSeating? king = null)
        {
            Emit(() =>
            {
                if (king is not null && _telemetry is AchievementSink favor) favor.MatchComplete(complete, king);
                else _telemetry.MatchComplete(complete);
            });
            Emit(() => _telemetry.CardsSeen(new CardSeenEvent(
                _id, complete.EndedAtUtc, _attribution.Source, CardRows(state, complete.Outcome))));
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
        private static (int seat, List<int> kept, int replaced)? CaptureMulliganKeeps(GameState state, PlayerIntent intent)
        {
            if (state.Phase != TurnPhase.Mulligan) return null;
            if (intent is not MulliganIntent && intent is not AutoConfirmIntent) return null;

            int seat = state.SeatOf(intent.Player);
            var replaced = intent as MulliganIntent;
            var hand = state.Players[seat].Hand;
            var kept = new List<int>();
            foreach (var card in hand)
            {
                bool thrown = false;
                if (replaced is not null)
                    foreach (var id in replaced.ToReplace)
                        if (id == card.Id) { thrown = true; break; }
                if (!thrown) kept.Add(card.DefinitionId);
            }
            // Counted from the hand, not from ToReplace: an id that is not in the hand replaces nothing.
            return (seat, kept, hand.Count - kept.Count);
        }

        /// <summary>The seat a <see cref="HeroPowerIntent"/> belongs to, or null for any other intent.</summary>
        private static int? CaptureHeroPower(GameState state, PlayerIntent intent)
        {
            if (intent is not HeroPowerIntent) return null;
            if (state.Outcome != MatchOutcome.InProgress) return null;
            return state.SeatOf(intent.Player);
        }

        /// <summary>The seat an <see cref="EndTurnIntent"/> belongs to and its unspent mana, or null
        /// for any other intent.</summary>
        private static (int seat, int unspent)? CaptureEndTurn(GameState state, PlayerIntent intent)
        {
            if (intent is not EndTurnIntent) return null;
            if (state.Outcome != MatchOutcome.InProgress) return null;
            int seat = state.SeatOf(intent.Player);
            return (seat, state.Players[seat].Mana.Available);
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
            FindingsObserver.Tally tally = TallyFor(seat);
            return new SeatOutcome(
                seat == 0 ? _attribution.P0 : _attribution.P1,
                @class,
                DeckIdentity.IdFor(@class),
                DeckIdentity.HashOf(deck),
                ResultFor(seat, outcome),
                _mulliganKept[seat],
                _minionsLost[1 - seat],
                _mulliganReplaced[seat],
                _heroPowersUsed[seat],
                state.Players[seat].Hero.CurrentHealth,
                seat == 0 ? state.FatigueCounter0 : state.FatigueCounter1,
                tally.CardsBurned,
                _turnsTimedOut[seat],
                _turnsEndedWithTenUnspent[seat],
                tally.SelfDamage,
                _findings.Overkills[seat],
                _findings.BigMinionsBounced[seat],
                _findings.LegendariesLost[seat],
                _findings.MostBulwarks[seat],
                state.Players[seat].Hero.CurrentHealth <= 0 ? _findings.FellTo[seat] : FinalBlow.None,
                _policies[seat],
                tally.OverkillBest,
                tally.UnspentMana,
                tally.FatigueDamage,
                tally.TollDamage,
                tally.StolenDamage,
                tally.HeroPowerDamage);
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
