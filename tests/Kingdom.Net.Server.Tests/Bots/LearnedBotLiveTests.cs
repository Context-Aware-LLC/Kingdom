using System;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// Boots the REAL server with the REAL bot model and seats a human against it.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM THE UNIT TESTS. `LearnedBotSeatPolicyTests` proves the
    /// fallback wrapper behaves; `LearnedBotConfigTests` proves the configuration rules hold. Neither
    /// loads a model or plays a move — so between them they could both pass against a serving path
    /// that never serves. *Compiling is not running*, and neither is a green unit test.
    ///
    /// WHAT IT PROVES THAT NOTHING ELSE DOES. That the tracked H1 model in `tooling/data/bot-model-h1`
    /// loads inside the server, that ONNX Runtime's native library is found, and that the bot's
    /// first decision (its mulligan) came from the model: `/healthz` counts it as served, with no
    /// fallback to Greedy.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class LearnedBotLiveTests
    {
        private const string TokenA = "cccccccccccccccccccccccccccccccc";

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Human_Is_Seated_Against_A_Bot_That_Decides_With_The_Shipped_Model()
        {
            string replays = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "emberfall-learnedbot-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                BotRosterPath = SessionHarness.RepoFile("tooling", "data", "bot-roster.json"),
                AccountStore = new InMemoryAccountStore(new SystemTimeSource()),
                BotModelPath = SessionHarness.RepoFile("tooling", "data", "bot-model-h1"),
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
                    "a human must be seated against a model-backed bot within BotFillDelayMs");

                using var http = new HttpClient { BaseAddress = baseUri };
                long decisions = 0, fallbacks = 0;
                for (int attempt = 0; attempt < 50 && decisions == 0; attempt++)
                {
                    await Task.Delay(100);
                    using JsonDocument health = JsonDocument.Parse(await http.GetStringAsync("/healthz"));
                    JsonElement bot = health.RootElement.GetProperty("bot");
                    decisions = bot.GetProperty("decisions").GetInt64();
                    fallbacks = bot.GetProperty("fallbacks").GetInt64();
                }
                Assert.That(decisions, Is.GreaterThan(0), "the bot's mulligan must be the model's decision");
                Assert.That(fallbacks, Is.Zero, "no decision may have fallen back to Greedy");
            }
            finally
            {
                await built.App.StopAsync();
                try { System.IO.Directory.Delete(replays, recursive: true); } catch (System.IO.IOException) { }
            }
        }
    }
}
