using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
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
            var reborn = new ScriptedClient(h.P0);
            h.Post(fresh, reborn.Frame(new HeartbeatMsg(), h.Id));
            Assert.That(fresh.Last<HeartbeatMsg>(), Is.Not.Null,
                "post-reconnect seq 1 must be accepted on the new connection");
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
            h.AdvanceAndTick(h.Options.DisconnectGraceMs + 1);

            Assert.That(h.Session.Ended, Is.False, "the intruder must not end or vacate anything");
            Assert.That(h.Session.StateVersion, Is.EqualTo(versionBefore));
            Assert.That(intruder.Frames, Is.Empty, "an unbound socket gets no answers at all");
        }

        [Test]
        public void Disconnect_Grace_Expiry_Concedes_The_Absent_Seat()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();

            h.Disconnect(0);
            h.AdvanceAndTick(h.Options.DisconnectGraceMs + 1);

            Assert.That(h.Session.Ended, Is.True);
            var end = h.Conn1.Last<MatchEndMsg>();
            Assert.That(end.Outcome, Is.EqualTo(MatchOutcome.Player1Win), "absent seat concedes");
            Assert.That(end.Reason, Is.EqualTo(MatchEndReason.Disconnect));
        }

        [Test]
        public void Reconnect_Within_Grace_Cancels_The_Concede()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            string token = h.Conn0.Last<MatchStartMsg>().ReconnectToken;

            h.Disconnect(0);
            h.AdvanceAndTick(h.Options.DisconnectGraceMs / 2);
            h.Session.Dispatch(new SessionCommand.Reconnect(new ReconnectMsg(h.P0, token, 0), new FakeConnection()));
            h.AdvanceAndTick(h.Options.DisconnectGraceMs);

            Assert.That(h.Session.Ended, Is.False, "a reconnected seat must not be conceded");
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
            var h = new SessionHarness(new ServerOptions());   // real 30s heartbeat timeout
            h.ConfirmMulligans();

            h.AdvanceAndTick(h.Options.HeartbeatTimeoutMs + 1);
            // Both seats were silent → both vacated; grace clocks now run. First-vacated concedes.
            h.AdvanceAndTick(h.Options.DisconnectGraceMs + 1);
            Assert.That(h.Session.Ended, Is.True, "silent seats fall to disconnect grace → concede");
        }

        private static int CountTimers(FakeConnection c)
        {
            int n = 0;
            foreach (var _ in c.MessagesOf<TurnTimerMsg>()) n++;
            return n;
        }
    }
}
