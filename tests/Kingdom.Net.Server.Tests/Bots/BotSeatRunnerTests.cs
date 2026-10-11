using System;
using System.IO;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Tests.Support;
using Kingdom.Net.Server.Transport;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    [TestFixture]
    // Not "Contract" — a real bot-vs-bot match at 500-2000ms human-pacing delay per intent takes
    // ~2m30s, millisecond-scale everywhere else in that stage. Gated instead by nightly.yml
    // (scheduled, not per-push) per Sam's ruling 2026-08-09 (ai-opponent-matchmaking-fallback memory).
    [Category("Nightly")]
    public sealed class BotSeatRunnerTests
    {
        // The brief's original 50s/60s budget assumed a short match; a real run of this exact
        // deterministic seed needs ~120 accepted intents to reach a decided outcome, and every one
        // of them pays BotSeatRunner's real 500-2000ms human-pacing delay (design doc §The bot
        // seat) — measured at ~156s wall-clock. 220s/230s leaves ~8 standard deviations of margin
        // over that (pacing is the only per-run randomness; the AI's decisions and the deck/seed are
        // deterministic, so the intent count itself never varies) without masking an actual hang.
        [Test]
        [CancelAfter(230_000)]
        public async Task Two_Bot_Seats_Play_A_Real_Match_To_A_Decided_Outcome()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var time = new SystemTimeSource();
            var replay = new InMemoryReplayStore();
            var opts = new ServerOptions
            {
                // No human is present to trip liveness/heartbeat/turn-clock paths; this test is about
                // whether two AI-driven seats can finish a game, not about the clock machinery.
                HeartbeatTimeoutMs = long.MaxValue / 4,
                TurnMs = long.MaxValue / 4,
            };
            var setup = PrebuiltDecks.EmberwardVsTidecaller();
            var session = new MatchSession(
                new MatchId("m-bot-vs-bot"), 0xB07UL, opts, time, db, setup, replay,
                new PlayerId("p0"), new PlayerId("p1"));
            var pump = new SessionPump(session);

            var seat0 = new BotSeatRunner("bot0", "Bot Zero", new CommandProcessor(db), db);
            var seat1 = new BotSeatRunner("bot1", "Bot One", new CommandProcessor(db), db);

            session.Start(seat0, seat1);
            seat0.Bind(pump);
            seat1.Bind(pump);

            await pump.RunTask.WaitAsync(TimeSpan.FromSeconds(220));

            Assert.That(session.Ended, Is.True);

            // session.Ended is set by BOTH a real decided outcome (EndMatch) and by VoidIfEmpty (both
            // seats disconnect, zero result) — a broken bridge that never actually played anything
            // would still pass the assertion above. Check the replay record to rule that out.
            var record = replay.Matches["m-bot-vs-bot"];
            Assert.That(record.Abandoned, Is.False);
            Assert.That(record.Outcome, Is.EqualTo(MatchOutcome.Player0Win).Or.EqualTo(MatchOutcome.Player1Win).Or.EqualTo(MatchOutcome.Draw));
        }
    }
}
