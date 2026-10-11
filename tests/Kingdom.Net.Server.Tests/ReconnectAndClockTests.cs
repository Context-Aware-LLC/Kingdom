using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>Reconnect (design §8) + clocks (design §7) through the one Dispatch door.</summary>
    [TestFixture]
    [Category("Contract")]
    public class ReconnectAndClockTests
    {
        // ── Reconnect ────────────────────────────────────────────────────────────

        [Test]
        public void Reconnect_With_Valid_Token_Gets_Snapshot_And_A_Fresh_Seq_Baseline()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            string token = h.Conn0.Last<MatchStartMsg>().ReconnectToken;

            h.Disconnect(0);

            var fresh = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(
                new ReconnectMsg(h.P0, token, 0), fresh));

            Assert.That(fresh.Last<SnapshotMsg>().VerifyIntegrity(), Is.True,
                "reconnect answers with the current projected snapshot (§8)");
            Assert.That(fresh.Last<TurnTimerMsg>(), Is.Not.Null, "and the live turn deadline");

            // Seq baseline reset (RC F7): a brand-new client legitimately restarts at seq 1 —
            // posted via the NEW connection, which is now the seat's identity.
            var rekindle = new ScriptedClient(h.P0);
            h.Post(fresh, rekindle.Frame(new HeartbeatMsg(), h.Id));
            Assert.That(fresh.Last<HeartbeatMsg>(), Is.Not.Null,
                "post-reconnect seq 1 must be accepted on the new connection");
        }

        [Test]
        public void Reconnect_Preserves_Chooser_Offer_Order_And_Default_Denies_The_Opponent()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            string token0 = h.Conn0.Last<MatchStartMsg>().ReconnectToken;
            string token1 = h.Conn1.Last<MatchStartMsg>().ReconnectToken;
            h.Session.Host.State.PendingChoice = new PendingChoice
            {
                Chooser = h.P0,
                Kind = PendingChoiceKind.Unearth,
                Source = h.Session.Host.State.Players[0].Hero.Id,
                ChoiceSequence = 701,
                Options = { FixtureCard.Id("hidden_offer_third"), FixtureCard.Id("hidden_offer_first"), FixtureCard.Id("hidden_offer_second") },
            };

            h.Disconnect(0);
            var chooserConnection = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(
                new ReconnectMsg(h.P0, token0, 0), chooserConnection));
            SnapshotMsg chooser = chooserConnection.Last<SnapshotMsg>();

            Assert.That(chooser.VerifyIntegrity(), Is.True);
            Assert.That(chooser.State.PendingChoice, Is.Not.Null);
            Assert.That(chooser.State.PendingChoice!.Options,
                Is.EqualTo(FixtureCard.Ids("hidden_offer_third", "hidden_offer_first", "hidden_offer_second")));
            Assert.That(chooser.State.PendingChoice.ChoiceSequence, Is.EqualTo(701UL));
            Assert.That(chooser.State.PendingChoice.Source,
                Is.EqualTo(h.Session.Host.State.PendingChoice.Source));

            h.Disconnect(1);
            var opponentConnection = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(
                new ReconnectMsg(h.P1, token1, 0), opponentConnection));
            SnapshotMsg opponent = opponentConnection.Last<SnapshotMsg>();

            Assert.That(opponent.VerifyIntegrity(), Is.True);
            Assert.That(opponent.State.PendingChoice, Is.Not.Null);
            Assert.That((opponent.State.PendingChoice!.Chooser, opponent.State.PendingChoice.Kind),
                Is.EqualTo((h.P0, PendingChoiceKind.Unearth)),
                "the opponent receives only public Unearth-in-progress metadata");
            Assert.That(opponent.State.PendingChoice.Options, Is.Empty);
            Assert.That((opponent.State.PendingChoice.Source.Value,
                    opponent.State.PendingChoice.ChoiceSequence),
                Is.EqualTo((0, 0UL)));
        }

        [Test]
        public void Reconnect_Token_Is_Seat_Bound_And_Garbage_Is_Refused()
        {
            var h = new SessionHarness();
            string token0 = h.Conn0.Last<MatchStartMsg>().ReconnectToken;
            h.Disconnect(1);

            // Seat 0's token cannot resume seat 1 (state-sync §7.2).
            var thief = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(new ReconnectMsg(h.P1, token0, 0), thief));
            Assert.That(thief.CloseCode, Is.EqualTo(1008));
            Assert.That(thief.Frames, Is.Empty, "no snapshot for a refused reconnect");

            var guesser = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(new ReconnectMsg(h.P1, "deadbeef", 0), guesser));
            Assert.That(guesser.CloseCode, Is.EqualTo(1008));
        }

        [Test]
        public void An_Unbound_Connection_Can_Neither_Inject_Frames_Nor_Vacate_A_Seat()
        {
            // The forged-reconnect regression: a socket that never passed the token check must not
            // be able to poison a live seat's policy state or disconnect it (SessionCommand doc).
            var h = new SessionHarness();
            h.ConfirmMulligans();
            ulong versionBefore = h.Session.StateVersion;
            var intruder = new FakeConnection();
            var forger = new ScriptedClient(h.P0);

            for (int i = 0; i < 5; i++)
                h.Post(intruder, forger.Intent(new ConcedeIntent(h.P0), h.Id));
            h.Session.Dispatch(new SessionCommand.Disconnected(intruder));
            h.AdvanceAndTick(1_000);   // short: this test is about the intruder, not the turn clock

            Assert.That(h.Session.Ended, Is.False, "the intruder must not end or vacate anything");
            Assert.That(h.Session.StateVersion, Is.EqualTo(versionBefore));
            Assert.That(intruder.Frames, Is.Empty, "an unbound socket gets no answers at all");
        }

        // The disconnect-grace concede was DELETED by decision 23 and its double-disconnect half
        // defined by decision 24. What replaced it lives in DoubleDisconnectVoidTests: absence alone
        // never ends a match (the idle rule does), and zero seats connected is a void.

        [Test]
        public void Reconnect_Restores_The_Seat_Without_Disturbing_The_Match()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            string token = h.Conn0.Last<MatchStartMsg>().ReconnectToken;

            h.Disconnect(0);
            h.AdvanceAndTick(120_000);
            h.Session.Dispatch(new SessionCommand.Reconnect(new ReconnectMsg(h.P0, token, 0), new FakeConnection()));

            Assert.That(h.Session.Ended, Is.False, "a seat may be away as long as it likes and come back");
        }

        // ── Clocks ───────────────────────────────────────────────────────────────

        [Test]
        public void Mulligan_Timeout_AutoConfirms_Unconfirmed_Seats_Through_The_Core()
        {
            var h = new SessionHarness();
            h.AdvanceAndTick(h.Options.MulliganMs + 1);

            Assert.That(h.Session.Host.State.Phase, Is.Not.EqualTo(TurnPhase.Mulligan));
            var rec = h.Replay.Matches[h.Id.Value];
            int autoConfirms = 0;
            foreach (var i in rec.Intents) if (i is AutoConfirmIntent) autoConfirms++;
            Assert.That(autoConfirms, Is.EqualTo(2), "synthetic intents ride the replay log (§9)");
            Assert.That(h.Conn0.Last<TurnTimerMsg>(), Is.Not.Null, "turn clock armed + broadcast");
        }

        [Test]
        public void Turn_Timeout_Fires_Rope_Then_AutoEnds_The_Turn()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            var firstActive = h.Session.Host.State.ActivePlayer;
            int timersBefore = CountTimers(h.Conn0);

            h.AdvanceAndTick(h.Options.TurnMs - h.Options.RopeMs + 1);   // into the rope window
            Assert.That(CountTimers(h.Conn0), Is.EqualTo(timersBefore + 1), "rope re-broadcast (§7)");
            Assert.That(h.Session.Host.State.ActivePlayer, Is.EqualTo(firstActive), "turn not ended yet");

            h.AdvanceAndTick(h.Options.RopeMs + 1);                      // past the deadline
            Assert.That(h.Session.Host.State.ActivePlayer, Is.Not.EqualTo(firstActive),
                "expiry auto-ends the turn via synthetic EndTurnIntent");
        }

        [Test]
        public void Turn_Timeout_AutoConfirms_A_Pending_Choice_For_Its_Chooser()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            GameState state = h.Session.Host.State;
            PlayerId active = state.ActivePlayer;
            int chooserSeat = 1 - state.SeatOf(active);
            PlayerId chooser = state.Players[chooserSeat].Id;
            int option = state.Players[chooserSeat].Deck[0].DefinitionId;
            int handBefore = state.Players[chooserSeat].Hand.Count;
            state.PendingChoice = new PendingChoice
            {
                Chooser = chooser,
                Kind = PendingChoiceKind.Unearth,
                Source = state.Players[chooserSeat].Hero.Id,
                ChoiceSequence = 702,
                Options = { option },
            };

            h.AdvanceAndTick(h.Options.TurnMs + 1);

            Assert.That(state.PendingChoice, Is.Null,
                "the expired turn clock must resolve the choice instead of repeatedly rejecting EndTurn");
            Assert.That(state.Players[chooserSeat].Hand.Count, Is.EqualTo(handBefore + 1));
            Assert.That(state.Players[chooserSeat].Hand[handBefore].DefinitionId, Is.EqualTo(option),
                "Unearth auto-confirm deterministically takes Options[0]");
            Assert.That(state.ActivePlayer, Is.EqualTo(active),
                "resolving the choice is the deadline's continuation; the next clock tick may end the turn");

            var intents = h.Replay.Matches[h.Id.Value].Intents;
            Assert.That(intents[intents.Count - 1], Is.EqualTo(new AutoConfirmIntent(chooser)),
                "the timer must submit for PendingChoice.Chooser, which need not be ActivePlayer");
        }

        [Test]
        public void Two_Consecutive_Auto_Turns_Idle_Forfeit_The_Seat()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            var firstActive = h.Session.Host.State.ActivePlayer;
            int firstSeat = h.Session.Host.State.SeatOf(firstActive);

            // Let every turn expire: firstActive auto-ends turns 1 and 3 → 2 consecutive → forfeit.
            for (int i = 0; i < 3 && !h.Session.Ended; i++)
                h.AdvanceAndTick(h.Options.TurnMs + 1);

            Assert.That(h.Session.Ended, Is.True);
            var end = h.Conn1.Last<MatchEndMsg>();
            Assert.That(end.Reason, Is.EqualTo(MatchEndReason.Timeout));
            Assert.That(end.Outcome,
                Is.EqualTo(firstSeat == 0 ? MatchOutcome.Player1Win : MatchOutcome.Player0Win),
                "the idle seat loses (§7)");
        }

        [Test]
        public void A_Voluntary_Intent_Resets_The_Idle_Forfeit_Run()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            var firstActive = h.Session.Host.State.ActivePlayer;
            int firstSeat = h.Session.Host.State.SeatOf(firstActive);

            h.AdvanceAndTick(h.Options.TurnMs + 1);   // firstActive auto-end #1
            h.AdvanceAndTick(h.Options.TurnMs + 1);   // other seat auto-end #1

            // firstActive plays a VOLUNTARY end-turn — the run resets (RC F11).
            h.Post(firstSeat, h.Client(firstSeat).Intent(new EndTurnIntent(firstActive), h.Id));
            h.AdvanceAndTick(h.Options.TurnMs + 1);   // other seat auto-end #2 → other seat forfeits first
            Assert.That(h.Session.Ended, Is.True, "the OTHER seat reached two consecutive auto-ends");
            Assert.That(h.Conn0.Last<MatchEndMsg>().Outcome,
                Is.EqualTo(firstSeat == 0 ? MatchOutcome.Player0Win : MatchOutcome.Player1Win),
                "the seat that acted voluntarily must not be the forfeiter");
        }

        [Test]
        public void Heartbeat_Silence_Vacates_The_Seat()
        {
            // One silent seat vacates and the OTHER keeps playing — the single-absence case.
            // (Both silent is a void, covered in DoubleDisconnectVoidTests.) Ticked at ~1 Hz so
            // the decision-24 stall guard correctly reads this as real elapsed time.
            var h = new SessionHarness(new ServerOptions());
            h.ConfirmMulligans();

            for (long t = 0; t <= h.Options.HeartbeatTimeoutMs + 1_000; t += 1_000)
            {
                h.Time.Advance(1_000);
                h.Post(1, h.Client1.Frame(new HeartbeatMsg(), h.Id));   // seat 1 stays alive
                h.Tick();
            }

            Assert.That(h.Conn0.CloseCode, Is.EqualTo(1001),
                "a silent seat's socket is CLOSED, not left quietly believing it is live");
            Assert.That(h.Session.Ended, Is.False, "one seat away does not end the match");
        }

        private static int CountTimers(FakeConnection c)
        {
            int n = 0;
            foreach (var _ in c.MessagesOf<TurnTimerMsg>()) n++;
            return n;
        }
    }
}
