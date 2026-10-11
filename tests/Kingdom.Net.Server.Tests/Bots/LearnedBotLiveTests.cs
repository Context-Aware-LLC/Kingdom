using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// Boots the REAL server with a REAL checkpoint and seats a human against it.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM THE UNIT TESTS. `LearnedBotSeatPolicyTests` proves the
    /// fallback wrapper behaves; `LearnedBotConfigTests` proves the configuration rules hold. Neither
    /// starts a Python scorer, loads a model, or plays a move — so between them they could both pass
    /// against a serving path that never serves. *Compiling is not running*, and neither is a green
    /// unit test.
    ///
    /// WHY IT IS SKIPPED BY DEFAULT. A real checkpoint and its RL export live outside the repo (on
    /// `F:` on the development box) and inference needs a Python environment. A test that demanded
    /// those would fail on every machine that does not have them, which is how a useful check becomes
    /// a disabled one. Set both variables and it runs:
    ///
    ///   EMBERFALL_BOT_CHECKPOINT=F:\EmberfallData\runs\...\best-model.pt
    ///   EMBERFALL_BOT_RL_EXPORT=F:\EmberfallData\exports\...
    ///
    /// WHAT IT PROVES THAT NOTHING ELSE DOES. That the extracted `Kingdom.Policy` assembly loads
    /// inside the server, that the scorer subprocess starts under the server's own lifetime, that a
    /// bot seat backed by a checkpoint reaches `MatchStart` — and, most usefully, that the seat is
    /// still identified as the learned policy rather than having quietly degraded to Greedy on its
    /// first decision.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class LearnedBotLiveTests
    {
        private const string TokenA = "cccccccccccccccccccccccccccccccc";

        private static string FindRepoFile(params string[] relativeSegments)
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(new[] { path }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(
                "could not locate " + Path.Combine(relativeSegments) + " from the test directory");
        }

        [Test]
        [CancelAfter(180_000)]
        public async Task A_Human_Is_Seated_Against_A_Bot_Running_The_Configured_Checkpoint()
        {
            string? checkpoint = Environment.GetEnvironmentVariable("EMBERFALL_BOT_CHECKPOINT");
            string? export = Environment.GetEnvironmentVariable("EMBERFALL_BOT_RL_EXPORT");
            if (string.IsNullOrWhiteSpace(checkpoint) || string.IsNullOrWhiteSpace(export))
                Assert.Ignore(
                    "set EMBERFALL_BOT_CHECKPOINT and EMBERFALL_BOT_RL_EXPORT to run the live "
                    + "learned-bot check; it needs a model and a Python environment outside the repo");

            string replays = Path.Combine(
                Path.GetTempPath(), "emberfall-learnedbot-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                BotRosterPath = FindRepoFile("tooling", "data", "bot-roster.json"),
                AccountStore = new InMemoryAccountStore(new SystemTimeSource()),
                BotCheckpointPath = checkpoint,
                BotRlExportPath = export,
                Options = new ServerOptions
                {
                    BotFillDelayMs = 300,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 150 },
                    // The scorer loads a model on first use, and a bot seat that blew a heartbeat
                    // while Python started would fail this test for a reason that has nothing to do
                    // with whether serving works.
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                    TurnMs = long.MaxValue / 4,
                },
            });

            await built.App.StartAsync();
            try
            {
                await Task.Delay(1500);

                var baseUri = new Uri(built.App.Urls.First());
                using var human = new ClientWebSocket();
                await human.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"), CancellationToken.None);
                await human.SendAsync(
                    ProtocolCodec.Encode(
                        new HelloMsg(TokenA, ProtocolContract.Version), ProtocolCodec.PreMatch, 0),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                var buffer = new byte[64 * 1024];
                var recv = await human.ReceiveAsync(buffer, CancellationToken.None);
                var decoded = ProtocolCodec.Decode(buffer.AsSpan(0, recv.Count));

                Assert.That(decoded.Ok, Is.True, "the server must answer the hello");
                Assert.That(decoded.Message, Is.InstanceOf<MatchStartMsg>(),
                    "a human must be seated against a checkpoint-backed bot within BotFillDelayMs");
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }
    }
}
