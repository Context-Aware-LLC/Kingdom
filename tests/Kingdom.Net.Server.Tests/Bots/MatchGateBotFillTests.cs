using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class MatchGateBotFillTests
    {
        private const string TokenA = "cccccccccccccccccccccccccccccccc";

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Ticket_That_Times_Out_Is_Filled_By_A_Bot()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            pool.MarkReady(new ResolvedBotAccount(
                "ember_knight",
                AccountId.FromAuthenticatedAccount("player-ember_knight"),
                "ember_knight",
                new Decks.SeatDeck(CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck())));

            string replays = Path.Combine(Path.GetTempPath(), "emberfall-botfill-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions
                {
                    BotFillDelayMs = 200,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 100 },
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
                    ProtocolCodec.Encode(new HelloMsg(TokenA, ProtocolContract.Version), ProtocolCodec.PreMatch, 0),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                var buffer = new byte[64 * 1024];
                var recv = await human.ReceiveAsync(buffer, CancellationToken.None);
                var decoded = ProtocolCodec.Decode(buffer.AsSpan(0, recv.Count));
                Assert.That(decoded.Ok, Is.True);
                Assert.That(decoded.Message, Is.InstanceOf<MatchStartMsg>());
                Assert.That(built.Gate.LiveSessions, Is.EqualTo(1));
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }
    }
}
