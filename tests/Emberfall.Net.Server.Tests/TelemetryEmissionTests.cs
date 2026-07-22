using System;
using System.Linq;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// C-G1-8 step 2 — the match actually emits (design/telemetry-spec.md §1).
    ///
    /// The condition exists because `telemetry-spec.md` was marked DISCHARGED on the strength of the
    /// file existing — true about the file, silent about the outcome. C4's exit bar is ≥200 tracked
    /// matches and **match one is unrecoverable**: a match that ends without a row is a permanently
    /// missing sample, not a deferred one. So these tests assert the EMIT, not the schema.
    ///
    /// Storage is step 3 and deliberately absent here — the sink is a seam, and everything below
    /// holds regardless of where the rows eventually land.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public class TelemetryEmissionTests
    {
        // ── The sample exists at all ─────────────────────────────────────────────

        [Test]
        public void A_Finished_Match_Emits_Exactly_One_MatchComplete()
        {
            var h = new SessionHarness(attributed: true);
            h.ConfirmMulligans();
            Concede(h, 1);

            Assert.That(h.Session.Ended, Is.True);
            Assert.That(h.Telemetry.Completes.Count, Is.EqualTo(1),
                "one finished match is exactly one row in the ≥200-match sample — no more, no less");
            Assert.That(h.Telemetry.Voids, Is.Empty);
        }

        [Test]
        public void The_Row_Carries_The_Fields_The_Exit_Bar_Is_Computed_From()
        {
            // Per-class win rate (§3) needs classId + result on both seats; the fast-kill prong needs
            // turnCount + endReason. If any of these is wrong the metric is wrong silently.
            var h = new SessionHarness(attributed: true);
            h.ConfirmMulligans();
            Concede(h, 1);

            var e = h.Telemetry.Completes.Single();
            Assert.That(e.Match, Is.EqualTo(h.Id));
            Assert.That(e.EndReason, Is.EqualTo(MatchEndReason.Concede));
            Assert.That(e.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(e.TurnCount, Is.EqualTo(h.Session.Host.State.TurnNumber));
            Assert.That(e.Source, Is.EqualTo(TelemetrySource.Ladder));

            Assert.That(e.P0.Result, Is.EqualTo(SeatResult.Win));
            Assert.That(e.P1.Result, Is.EqualTo(SeatResult.Loss));
            Assert.That(e.P0.Class, Is.EqualTo(CardClass.EMBERWARD));
            Assert.That(e.P1.Class, Is.EqualTo(CardClass.TIDECALLER));
        }

        [Test]
        public void Both_Seats_Are_Attributed_To_Their_Own_Account()
        {
            var h = new SessionHarness(attributed: true);
            h.ConfirmMulligans();
            Concede(h, 1);

            var e = h.Telemetry.Completes.Single();
            Assert.That(e.P0.Account, Is.EqualTo(SessionHarness.AccountFor(0)));
            Assert.That(e.P1.Account, Is.EqualTo(SessionHarness.AccountFor(1)));
            Assert.That(e.P0.Account, Is.Not.EqualTo(e.P1.Account), "the seats must not collapse into one player");
        }

        [Test]
        public void A_Timed_Out_Match_Is_Still_A_Sample()
        {
            // An idle forfeit is a real result with a real winner. Dropping it would bias the
            // win-rate sample toward whatever decks people stay awake for.
            var h = new SessionHarness(new ServerOptions(), attributed: true);
            h.ConfirmMulligans();

            for (long t = 0; t < h.Options.TurnMs * 6 && !h.Session.Ended; t += 1_000)
            {
                h.Time.Advance(1_000);
                h.Post(0, h.Client0.Frame(new HeartbeatMsg(), h.Id));
                h.Post(1, h.Client1.Frame(new HeartbeatMsg(), h.Id));
                h.Tick();
            }

            Assert.That(h.Session.Ended, Is.True, "the idle rule must have forced a concession");
            var e = h.Telemetry.Completes.Single();
            Assert.That(e.EndReason, Is.EqualTo(MatchEndReason.Timeout));
        }

        // ── Voids are separated, not silenced ────────────────────────────────────

        [Test]
        public void A_Void_Emits_A_Void_And_Never_A_MatchComplete()
        {
            // Decision 24: nobody won, nobody lost. A void leaking into the balance sample would
            // register as a result for a match that had none.
            var h = new SessionHarness(attributed: true);
            h.ConfirmMulligans();
            h.Disconnect(0);
            h.Disconnect(1);

            Assert.That(h.Telemetry.Completes, Is.Empty,
                "a void has no winner — it must never enter the win-rate sample");
            Assert.That(h.Telemetry.Voids.Count, Is.EqualTo(1),
                "but it must still be counted: a match that vanishes with no row at all is " +
                "indistinguishable from telemetry being broken");
            Assert.That(h.Telemetry.Voids.Single().Match, Is.EqualTo(h.Id));
        }

        // ── The unattributed guard ───────────────────────────────────────────────

        [Test]
        public void An_Unattributed_Match_Emits_Nothing()
        {
            // AccountId.Unattributed's own contract: "the telemetry emitter refuses to write a row
            // for this". A row keyed on an empty account is worse than a missing row — it silently
            // pollutes the per-class win rate and looks like real data.
            var h = new SessionHarness();   // default: no identity
            h.ConfirmMulligans();
            Concede(h, 1);

            Assert.That(h.Session.Ended, Is.True, "the match itself still ends normally");
            Assert.That(h.Telemetry.Completes, Is.Empty);
            Assert.That(h.Telemetry.Voids, Is.Empty);
        }

        // ── Mulligan keeps ───────────────────────────────────────────────────────

        [Test]
        public void Mulligan_Keeps_Are_Recorded_From_The_Pre_Mulligan_Hand()
        {
            // The reason this is captured at submit time: the core replaces discarded cards in
            // place, so after the mulligan resolves the hand can no longer say what was thrown back.
            var h = new SessionHarness(attributed: true);

            var hand0 = h.Session.Host.State.Players[0].Hand.ToList();
            var thrown = hand0[0];
            var keptDefs = hand0.Skip(1).Select(c => c.DefinitionId).ToList();

            h.Post(0, h.Client0.Intent(new MulliganIntent(h.P0, new[] { thrown.Id }), h.Id));
            h.Post(1, h.Client1.Intent(new MulliganIntent(h.P1, Array.Empty<InstanceId>()), h.Id));
            Concede(h, 1);

            var e = h.Telemetry.Completes.Single();
            Assert.That(e.P0.MulliganKept, Is.EqualTo(keptDefs),
                "the kept cards are the pre-mulligan hand minus what was thrown back");
            Assert.That(e.P0.MulliganKept, Does.Not.Contain(thrown.DefinitionId).Or.Count.LessThan(hand0.Count),
                "the thrown card must not survive into the keep list");
            Assert.That(e.P1.MulliganKept, Is.Not.Empty, "keeping everything still records a full keep");
        }

        [Test]
        public void An_Auto_Confirmed_Mulligan_Records_The_Whole_Hand_As_Kept()
        {
            // A timed-out mulligan keeps everything — that is what auto-confirm means, and it is
            // still a real data point about the opening hand.
            // BOTH guards off — the harness default, and both are needed. This test jumps the clock
            // past the mulligan deadline in ONE tick: the stall guard reads that as "the server was
            // asleep" and skips the auto-confirm entirely, and with only the stall guard disabled
            // the same jump puts both seats past HeartbeatTimeoutMs in one pass, vacating them and
            // VOIDING the match instead of finishing it. Real mulligan timeouts arrive on a ~1 Hz
            // tick, where neither fires. (Both failure modes hit in sequence, 2026-07-22.)
            var h = new SessionHarness(
                new ServerOptions { StallGuardMs = long.MaxValue / 4, HeartbeatTimeoutMs = long.MaxValue / 4 },
                attributed: true);
            var hand0 = h.Session.Host.State.Players[0].Hand.Select(c => c.DefinitionId).ToList();

            h.AdvanceAndTick(h.Options.MulliganMs + 1_000);
            Concede(h, 1);

            var e = h.Telemetry.Completes.Single();
            Assert.That(e.P0.MulliganKept, Is.EqualTo(hand0));
        }

        // ── Deck identity ────────────────────────────────────────────────────────

        [Test]
        public void Deck_Hash_Is_Content_Addressed_And_Order_Independent()
        {
            // Deck ORDER is a shuffle artifact, not deck identity: two draws of the same list must
            // land in the same archetype bucket.
            var list = new[] { "card_b", "card_a", "card_c" };
            var shuffled = new[] { "card_c", "card_b", "card_a" };
            Assert.That(DeckIdentity.HashOf(list), Is.EqualTo(DeckIdentity.HashOf(shuffled)));
            Assert.That(DeckIdentity.HashOf(list), Is.Not.EqualTo(DeckIdentity.HashOf(new[] { "card_a", "card_a", "card_c" })));
        }

        [Test]
        public void The_Two_Seats_Get_Distinct_Deck_Identities()
        {
            var h = new SessionHarness(attributed: true);
            h.ConfirmMulligans();
            Concede(h, 1);

            var e = h.Telemetry.Completes.Single();
            Assert.That(e.P0.DeckHash, Is.Not.EqualTo(e.P1.DeckHash), "Emberward and Tidecaller are different lists");
            Assert.That(e.P0.DeckId, Is.EqualTo("prebuilt-emberward"));
            Assert.That(e.P1.DeckId, Is.EqualTo("prebuilt-tidecaller"));
        }

        // ── Sample classification ────────────────────────────────────────────────

        [Test]
        public void Soak_Seats_Classify_As_AiSoak_And_Only_When_Both_Are_Synthetic()
        {
            var soak0 = AccountId.FromDeviceToken(TelemetrySources.SoakTokenSeat0);
            var soak1 = AccountId.FromDeviceToken(TelemetrySources.SoakTokenSeat1);
            var human = AccountId.FromDeviceToken("a-real-tester-device");

            Assert.That(TelemetrySources.Classify(soak0, soak1), Is.EqualTo(TelemetrySource.AiSoak));

            // A human paired against a soak seat is not an AI-vs-AI match. Filing it under ai_soak
            // would quietly delete a real ladder result from the human sample.
            Assert.That(TelemetrySources.Classify(human, soak1), Is.EqualTo(TelemetrySource.Ladder));
            Assert.That(TelemetrySources.Classify(soak0, human), Is.EqualTo(TelemetrySource.Ladder));
            Assert.That(TelemetrySources.Classify(human, human), Is.EqualTo(TelemetrySource.Ladder));
        }

        [Test]
        public void The_Soak_Tokens_Still_Match_What_NetSoak_Sends()
        {
            // Classification is derived from these exact strings. If NetSoak's SoakDevice() ever
            // drifts, every soak match silently reclassifies as ladder and pollutes the human
            // sample — a failure with no symptom until the numbers are already wrong.
            Assert.That(TelemetrySources.SoakTokenSeat0, Is.EqualTo("ai-soak-seat-0"));
            Assert.That(TelemetrySources.SoakTokenSeat1, Is.EqualTo("ai-soak-seat-1"));
        }

        // ── The emitter must never cost a match ──────────────────────────────────

        [Test]
        public void A_Throwing_Sink_Does_Not_Break_A_Finished_Match()
        {
            // The match is already over and correct by the time telemetry runs. Losing a sample
            // costs one row; an escaping exception would take down the session pump.
            var sink = new ThrowingSink();
            var h = new SessionHarness(attributed: true);
            var session = new MatchSession(
                new MatchId("m-throw"), SessionHarness.Seed, h.Options, h.Time, SessionHarness.Db,
                Emberfall.Core.PrebuiltDecks.EmberwardVsTidecaller(), new Replay.InMemoryReplayStore(),
                h.P0, h.P1, sink,
                new MatchAttribution(SessionHarness.AccountFor(0), SessionHarness.AccountFor(1), TelemetrySource.Ladder));

            var c0 = new FakeConnection();
            var c1 = new FakeConnection();
            session.Start(c0, c1);
            var client0 = new ScriptedClient(h.P0);
            session.Dispatch(new SessionCommand.Frame(c0, client0.Intent(new MulliganIntent(h.P0, Array.Empty<InstanceId>()), new MatchId("m-throw"))));
            var client1 = new ScriptedClient(h.P1);
            session.Dispatch(new SessionCommand.Frame(c1, client1.Intent(new MulliganIntent(h.P1, Array.Empty<InstanceId>()), new MatchId("m-throw"))));
            session.Dispatch(new SessionCommand.Frame(c1, client1.Intent(new ConcedeIntent(h.P1), new MatchId("m-throw"))));

            Assert.That(sink.WasCalled, Is.True, "the emit point ran");
            Assert.That(session.Ended, Is.True, "and the match still ended cleanly");
            Assert.That(c0.MessagesOf<MatchEndMsg>().Any(), Is.True, "the players still got their match-end");
        }

        private sealed class ThrowingSink : ITelemetrySink
        {
            public bool WasCalled;
            public void MatchComplete(MatchCompleteEvent e) { WasCalled = true; throw new InvalidOperationException("sink is down"); }
            public void MatchVoid(MatchVoidEvent e) { WasCalled = true; throw new InvalidOperationException("sink is down"); }
        }

        private static void Concede(SessionHarness h, int seat)
            => h.Post(seat, h.Client(seat).Intent(new ConcedeIntent(seat == 0 ? h.P0 : h.P1), h.Id));
    }
}
