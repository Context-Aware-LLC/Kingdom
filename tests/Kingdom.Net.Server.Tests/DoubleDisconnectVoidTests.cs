using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// Decision 24 — double-disconnect is a void, plus the stall guard that decision depends on
    /// (`dev-docs/contracts/gameplay-and-session.md:40`).
    ///
    /// The void is defined as **zero seats connected, any instant, no window**: nobody wins, nobody
    /// loses, no ranking change, no `MatchEndMsg`, and it is deliberately NOT a `MatchOutcome` — so
    /// there is no enum member and no golden-fixture rebaseline. The stall guard is the one
    /// qualifier: a seat only counts as vacant if the server was awake to observe it.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public class DoubleDisconnectVoidTests
    {
        // ── The void ─────────────────────────────────────────────────────────────

        [Test]
        public void Zero_Seats_Connected_Voids_The_Match_The_Instant_The_Last_Seat_Goes()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();

            h.Disconnect(0);
            Assert.That(h.Session.Ended, Is.False, "one absent seat is not a void — the match runs on");

            h.Disconnect(1);

            // No window, no tick required: the instant nobody is connected, the match is over.
            Assert.That(h.Session.Ended, Is.True,
                "zero seats connected ends the match immediately — no grace timer, no tick needed");
        }

        [Test]
        public void A_Void_Records_No_Outcome_And_Sends_No_MatchEnd()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            h.Disconnect(0);
            h.Disconnect(1);

            var rec = h.Replay.Matches[h.Id.Value];
            Assert.That(rec.Abandoned, Is.True, "a void abandons the journal entry");
            Assert.That(rec.Outcome, Is.Null, "nobody won and nobody lost — no outcome is recorded");

            Assert.That(h.Session.Host.State.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "the void never reaches the rules core: GameState.Outcome is hash-canonical and stays put");

            // There is nobody connected to receive a MatchEndMsg — that is the definition of the case.
            Assert.That(h.Conn0.MessagesOf<MatchEndMsg>(), Is.Empty);
            Assert.That(h.Conn1.MessagesOf<MatchEndMsg>(), Is.Empty);
        }

        [Test]
        public void A_Single_Absent_Seat_Never_Concedes_On_A_Timer()
        {
            // The superseded 60s disconnect grace is GONE (decision 23). One seat away means the
            // match keeps running; only the idle rule can force a concession, and it needs turns.
            var h = new SessionHarness();
            h.ConfirmMulligans();
            h.Disconnect(0);

            h.AdvanceAndTick(120_000);   // twice the old grace period

            Assert.That(h.Session.Ended, Is.False,
                "no disconnect-grace concede exists any more — absence alone never ends a match");
        }

        [Test]
        public void A_Reconnect_Restores_A_Vacant_Seat_And_Averts_The_Void()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            string token = h.Conn0.Last<MatchStartMsg>().ReconnectToken;

            h.Disconnect(0);
            h.Session.Dispatch(new SessionCommand.Reconnect(new ReconnectMsg(h.P0, token, 0), new FakeConnection()));

            // Seat 0 is back, so seat 1 leaving no longer empties the match.
            h.Disconnect(1);
            Assert.That(h.Session.Ended, Is.False, "one seat is still connected — not a void");
        }

        [Test]
        public void Liveness_Timeout_Can_Void_A_Match_When_Both_Seats_Go_Silent()
        {
            // Vacancy is set by a real socket close OR the liveness timeout. Both silent → both
            // vacant → void. Ticked at a realistic cadence so the stall guard stays out of it.
            var h = new SessionHarness(new ServerOptions());
            h.ConfirmMulligans();

            for (long t = 0; t < h.Options.HeartbeatTimeoutMs + 2_000 && !h.Session.Ended; t += 1_000)
                h.AdvanceAndTick(1_000);

            Assert.That(h.Session.Ended, Is.True, "two silent seats vacate and the match voids");
            Assert.That(h.Replay.Matches[h.Id.Value].Abandoned, Is.True, "silence voids — it does not concede");
            Assert.That(h.Replay.Matches[h.Id.Value].Outcome, Is.Null);
        }

        // ── The stall guard (Sam's catch) ────────────────────────────────────────

        [Test]
        public void A_Server_Stall_Does_Not_Vacate_Two_Healthy_Seats()
        {
            // THE regression this guard exists for: Fly suspends the machine on idle. On the first
            // tick after resume, both seats are past HeartbeatTimeoutMs in the same loop pass, both
            // vacate, and a zero-window void destroys a match in which both players were fine.
            var h = new SessionHarness(new ServerOptions());
            h.ConfirmMulligans();

            h.AdvanceAndTick(h.Options.HeartbeatTimeoutMs * 3);   // the server was asleep, not the players

            Assert.That(h.Session.Ended, Is.False,
                "a seat counts as vacant only if the server was awake to observe it (decision 24 rule 4)");
            Assert.That(h.Replay.Matches[h.Id.Value].Abandoned, Is.False, "a healthy match must not be voided");
        }

        [Test]
        public void A_Server_Stall_Does_Not_Burn_The_Turn_Clock()
        {
            // The same guard protects the turn clock, which would otherwise auto-end turns for time
            // the process spent suspended — and feed the idle-forfeit counter while it did.
            var h = new SessionHarness(new ServerOptions());
            h.ConfirmMulligans();
            var activeBefore = h.Session.Host.State.ActivePlayer;

            h.AdvanceAndTick(h.Options.TurnMs * 2);   // stalled clean past the turn deadline

            Assert.That(h.Session.Host.State.ActivePlayer, Is.EqualTo(activeBefore),
                "the turn must survive a stall — the player was never given their time");
            Assert.That(h.Session.Ended, Is.False);
        }

        [Test]
        public void Normal_Ticks_Are_Not_Mistaken_For_Stalls()
        {
            // The guard must not swallow ordinary time: ticked at ~1 Hz, the turn clock still fires.
            var h = new SessionHarness(new ServerOptions());
            h.ConfirmMulligans();
            var activeBefore = h.Session.Host.State.ActivePlayer;

            // Keep both seats alive so liveness never fires; only the turn clock should.
            for (long t = 0; t <= h.Options.TurnMs + 1_000; t += 1_000)
            {
                h.Time.Advance(1_000);
                h.Post(0, h.Client0.Frame(new HeartbeatMsg(), h.Id));
                h.Post(1, h.Client1.Frame(new HeartbeatMsg(), h.Id));
                h.Tick();
            }

            Assert.That(h.Session.Host.State.ActivePlayer, Is.Not.EqualTo(activeBefore),
                "real elapsed time still auto-ends a turn — the guard only skips genuine stalls");
        }
    }
}
