using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class BotFillEndToEndTests
    {
        private const string TokenA = "dddddddddddddddddddddddddddddddd";
        private const string TokenB = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        /// <summary>
        /// Locates repo-root files the same way <see cref="SessionHarness.CardDataDir()"/> locates
        /// <c>unity/Assets/CardData</c>: walk up from the test assembly's own output
        /// directory until the target is found, rather than hard-coding a fixed count of ".."
        /// segments against a build-output layout that can silently change (different SDK, a
        /// bin/obj nesting change, CI vs. local). Verified against this repo's actual build output:
        /// <c>TestContext.CurrentContext.TestDirectory</c> resolves (verified 2026-08-09) to
        /// <c>tests/Kingdom.Net.Server.Tests/bin/Debug/net10.0</c>, five directory levels below
        /// the repo root (net10.0 → Debug → bin → Kingdom.Net.Server.Tests → tests → root), so
        /// this walk finds <c>tooling/data/bot-roster.json</c> on its 6th check (loop index 5) —
        /// well inside the loop's 10-step budget, with headroom if the output layout ever grows
        /// deeper.
        /// </summary>
        private static string FindRepoFile(params string[] relativeSegments)
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(new[] { path }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(
                "Could not locate " + Path.Combine(relativeSegments) + " by walking up from the test directory.");
        }

        [Test]
        [CancelAfter(90_000)]
        public async Task A_Queued_Human_Is_Seated_Against_A_Real_Provisioned_Bot()
        {
            string rosterPath = FindRepoFile("tooling", "data", "bot-roster.json");

            string replays = Path.Combine(Path.GetTempPath(), "emberfall-botfill-e2e-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                BotRosterPath = rosterPath,
                AccountStore = new InMemoryAccountStore(new SystemTimeSource()),
                Options = new ServerOptions
                {
                    BotFillDelayMs = 300,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 150 },
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                    TurnMs = long.MaxValue / 4,
                },
            });

            await built.App.StartAsync();
            try
            {
                // Give the hosted BotAccountProvisioner a moment to run (it starts with the host).
                await Task.Delay(500);

                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var human = new ClientWebSocket();
                await human.ConnectAsync(wsUri, CancellationToken.None);
                await human.SendAsync(
                    ProtocolCodec.Encode(new HelloMsg(TokenA, ProtocolContract.Version), ProtocolCodec.PreMatch, 0),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                var buffer = new byte[64 * 1024];
                var recv = await human.ReceiveAsync(buffer, CancellationToken.None);
                var decoded = ProtocolCodec.Decode(buffer.AsSpan(0, recv.Count));
                Assert.That(decoded.Ok, Is.True);
                Assert.That(decoded.Message, Is.InstanceOf<MatchStartMsg>(),
                    "the human must be seated against a bot within BotFillDelayMs");
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Closes the one coverage gap flagged in review of the earlier tasks: every other bot-fill
        /// test (this file's above test, <c>MatchGateBotFillTests</c>) only asserts that a bot FILLED
        /// a seat — nothing anywhere exercises <c>MatchGate.TryBotFillTimedOutTickets</c>'s cleanup
        /// half, <c>seated.Pump.RunTask.ContinueWith(_ => bot.Release(), ...)</c>, which is the only
        /// thing standing between a finished match and a bot account stuck "busy" forever
        /// (<see cref="BotHandle.Release"/>'s own doc comment: "or the bot is permanently stuck busy
        /// and the pool silently shrinks").
        ///
        /// <para>This uses the SAME pre-seeded-<see cref="BotPool"/> fast path as
        /// <c>MatchGateBotFillTests</c> (not the real file-reading provisioner — that path is already
        /// proven by the test above; this one is purely about <see cref="Transport.MatchGate"/>'s
        /// post-match wiring) with exactly one ready bot, so a successful re-acquire after the match
        /// ends can only mean that one account came back. To reach "match ended" quickly without
        /// implementing a full scripted client, this configures very short mulligan/turn clocks and
        /// <see cref="ServerOptions.IdleForfeitAutoTurns"/> = 1: the human stub below never sends a
        /// mulligan or turn intent, so the very first clock-fired auto-ended turn immediately
        /// idle-forfeits it — same mechanism <see cref="MatchSession"/>'s clock/idle-forfeit tests
        /// already rely on, just tuned fast instead of realistic. The bot's own pacing (500-2000ms
        /// per <c>BotSeatRunner</c> decision, see <c>BotSeatRunnerTests</c>) can't beat these
        /// deadlines either, so whichever seat is first to act concedes — this test doesn't care
        /// which; it only cares that <em>a</em> match-end reaches the continuation.</para>
        /// </summary>
        [Test]
        [CancelAfter(30_000)]
        public async Task A_Bot_Released_When_Its_Match_Ends_Is_Reacquirable_From_The_Pool()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            pool.MarkReady(new ResolvedBotAccount(
                "ember_knight",
                AccountId.FromAuthenticatedAccount("player-ember_knight"),
                "ember_knight",
                new Decks.SeatDeck(CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck())));

            string replays = Path.Combine(Path.GetTempPath(), "emberfall-botfill-release-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions
                {
                    BotFillDelayMs = 200,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 100 },
                    MulliganMs = 100,
                    TurnMs = 100,
                    IdleForfeitAutoTurns = 1,
                    // Keep the human's silent socket from tripping the UNRELATED liveness-close path
                    // (it would still end the match, just via a different route than the one this
                    // test means to exercise).
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                },
            }, pool);

            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var human = new ClientWebSocket();
                await human.ConnectAsync(wsUri, CancellationToken.None);
                await human.SendAsync(
                    ProtocolCodec.Encode(new HelloMsg(TokenB, ProtocolContract.Version), ProtocolCodec.PreMatch, 0),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                var buffer = new byte[64 * 1024];
                var recv = await human.ReceiveAsync(buffer, CancellationToken.None);
                var decoded = ProtocolCodec.Decode(buffer.AsSpan(0, recv.Count));
                Assert.That(decoded.Ok, Is.True);
                Assert.That(decoded.Message, Is.InstanceOf<MatchStartMsg>(),
                    "the pool's only bot must fill the seat within BotFillDelayMs");

                // The pool has exactly one account and it was just handed out: acquiring again must
                // fail while the match is live. This is the "busy" half of the gap; the poll below
                // proves the "released" half.
                Assert.That(pool.TryAcquire(), Is.Null, "the bot must stay busy while its match is live");

                // Poll instead of a fixed sleep: we don't control the session's own ~1 Hz clock loop
                // (SessionPump.ClockAsync, real wall-clock via SystemTimeSource) from here, and a
                // fixed delay would either race it or pad the test with dead time.
                BotHandle? reacquired = null;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    reacquired = pool.TryAcquire();
                    if (reacquired is not null) break;
                    await Task.Delay(200);
                }

                Assert.That(reacquired, Is.Not.Null,
                    "MatchGate's pump.RunTask continuation must call bot.Release() once the match ends");
                Assert.That(reacquired!.DisplayName, Is.EqualTo("ember_knight"),
                    "the pool holds only one account, so reacquiring it proves the SAME account came back");
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }
    }
}
