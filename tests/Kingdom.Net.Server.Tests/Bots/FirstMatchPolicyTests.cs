using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using Kingdom.Policy;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// The first-match bot policy (launch item 5, Sam 2026-10-26): a signed-in newcomer's first
    /// match is against Greedy, their second against H1, and from the third on the ordinary queue.
    /// The property the gate tests hold above all is the one the ruling names: a newcomer is never
    /// paired with a human, whether a human is queued beside them, the bots are all busy, or their
    /// count cannot be read.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class FirstMatchPolicyTests
    {
        // ── The policy itself ───────────────────────────────────────────────────

        [TestCase(0, BotOpponent.Greedy)]
        [TestCase(1, BotOpponent.SecondMatch)]
        public void A_Newcomer_Is_Owed_A_Fixed_Bot(int completed, BotOpponent expected)
            => Assert.That(FirstMatchPolicy.For(completed), Is.EqualTo(expected));

        [TestCase(2)]
        [TestCase(50)]
        public void From_The_Third_Match_On_It_Is_The_Ordinary_Queue(int completed)
            => Assert.That(FirstMatchPolicy.For(completed), Is.Null);

        // ── What counts as a completed match ───────────────────────────────────

        private static readonly AccountId Human = AccountId.FromAuthenticatedAccount("player-human");
        private static readonly AccountId Other = AccountId.FromAuthenticatedAccount("player-other");
        private static readonly AccountId Bot = AccountId.FromAuthenticatedAccount("player-bot");

        private static RankedMatchResult Result(
            string match, AccountId p0, AccountId p1,
            MatchOutcome outcome = MatchOutcome.Player0Win,
            TelemetrySource source = TelemetrySource.BotFill,
            MatchEndReason reason = MatchEndReason.Win)
            => new RankedMatchResult(new MatchId(match), DateTimeOffset.UtcNow, p0, "a", p1, "b", outcome, source, reason);

        [Test]
        public async Task A_Match_Counts_For_The_Human_Seat_And_Never_For_The_Bot()
        {
            var store = new InMemoryCompletedMatchStore();
            var ledger = new CompletedMatchLedger(store, a => a == Bot);

            ledger.Record(Result("m1", Human, Bot));

            Assert.That(await ledger.CountAsync(Human, 2), Is.EqualTo(1));
            Assert.That(await ledger.CountAsync(Bot, 2), Is.EqualTo(0));
        }

        [Test]
        public async Task A_Match_Counts_Before_Its_Row_Is_Written()
        {
            // The play-again race: a player who requeues straight off the result screen must not
            // be dealt Greedy twice because the write is still queued.
            var store = new InMemoryCompletedMatchStore();
            var ledger = new CompletedMatchLedger(store);

            ledger.Record(Result("m1", Human, Bot));

            Assert.That(await store.FirstMatchIdsAsync(Human.Value, 2), Is.Empty, "nothing is written yet");
            Assert.That(await ledger.CountAsync(Human, 2), Is.EqualTo(1));

            await ledger.WriteAsync(Human, "m1", DateTimeOffset.UtcNow);
            Assert.That(await store.FirstMatchIdsAsync(Human.Value, 2), Is.EqualTo(new[] { "m1" }));
            Assert.That(await ledger.CountAsync(Human, 2), Is.EqualTo(1), "written and pending are one match");
        }

        [Test]
        public async Task A_Write_That_Fails_Still_Counts_On_This_Server()
        {
            var ledger = new CompletedMatchLedger(new ThrowingStore(failReads: false));

            ledger.Record(Result("m1", Human, Bot));
            await ledger.WriteAsync(Human, "m1", DateTimeOffset.UtcNow);

            Assert.That(await ledger.CountAsync(Human, 2), Is.EqualTo(1));
        }

        [Test]
        public async Task Concedes_And_Timeouts_Count_And_Unfinished_Or_Soak_Matches_Do_Not()
        {
            var ledger = new CompletedMatchLedger(new InMemoryCompletedMatchStore());

            ledger.Record(Result("concede", Human, Bot, MatchOutcome.Player1Win, reason: MatchEndReason.Concede));
            Assert.That(await ledger.CountAsync(Human, 5), Is.EqualTo(1));

            ledger.Record(Result("unfinished", Human, Bot, MatchOutcome.InProgress));
            ledger.Record(Result("soak", Human, Bot, source: TelemetrySource.AiSoak));
            Assert.That(await ledger.CountAsync(Human, 5), Is.EqualTo(1));

            ledger.Record(Result("ladder", Human, Other, source: TelemetrySource.Ladder));
            Assert.That(await ledger.CountAsync(Human, 5), Is.EqualTo(2));
            Assert.That(await ledger.CountAsync(Other, 5), Is.EqualTo(1));
        }

        [Test]
        public async Task The_Same_Match_Twice_Counts_Once_And_The_Count_Is_Capped()
        {
            var store = new InMemoryCompletedMatchStore();
            var ledger = new CompletedMatchLedger(store);
            await store.RecordAsync(Human.Value, "old1", DateTimeOffset.UtcNow.AddDays(-2));
            await store.RecordAsync(Human.Value, "old2", DateTimeOffset.UtcNow.AddDays(-1));

            ledger.Record(Result("old1", Human, Bot));
            ledger.Record(Result("old1", Human, Bot));

            Assert.That(await ledger.CountAsync(Human, 5), Is.EqualTo(2));
            Assert.That(await ledger.CountAsync(Human, 1), Is.EqualTo(1));
        }

        // ── Which policy the pool seats ─────────────────────────────────────────

        [Test]
        public void The_Pool_Seats_The_Policy_It_Is_Asked_For()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db,
                policyFactory: _ => new NamedPolicy("champion"),
                secondMatchFactory: _ => new NamedPolicy("second-match"));
            MarkReady(pool, "ember_knight");

            AssertSeats(pool, BotOpponent.Default, "champion");
            AssertSeats(pool, BotOpponent.Greedy, GreedyAi.PolicyVersion);
            AssertSeats(pool, BotOpponent.SecondMatch, "second-match");
        }

        [Test]
        public void Without_A_Second_Match_Model_The_Second_Match_Is_Greedy_Not_The_Champion()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db, policyFactory: _ => new NamedPolicy("champion"));
            MarkReady(pool, "ember_knight");

            AssertSeats(pool, BotOpponent.SecondMatch, GreedyAi.PolicyVersion);
        }

        private static void AssertSeats(BotPool pool, BotOpponent opponent, string identity)
        {
            BotHandle? handle = pool.TryAcquire(opponent);
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle!.Seat.PolicyIdentity, Is.EqualTo(identity));
            handle.Release();
        }

        [Test]
        public void The_Shipped_Second_Match_Model_Is_H1()
        {
            // The ruling pins H1 (978b89297989) for game 2, never the moving champion. A promotion
            // that copies a new model over the wrong directory fails here.
            string manifest = SessionHarness.RepoFile("tooling", "data", "bot-model-h1", "bot-model.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            Assert.That(doc.RootElement.GetProperty("checkpointSha256").GetString(), Does.StartWith("978b89297989"));
        }

        // ── The gate: newcomers never meet a human ──────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Two_Newcomers_Queued_Together_Each_Play_Greedy_And_Not_Each_Other()
        {
            var env = new GateEnv(readyBots: 2);
            await env.RunAsync(async uri =>
            {
                using var a = await env.QueueAsync(uri, "sub-a");
                using var b = await env.QueueAsync(uri, "sub-b");
                await ExpectAsync<MatchStartMsg>(a);
                await ExpectAsync<MatchStartMsg>(b);

                Assert.That(env.Gate!.LiveSessions, Is.EqualTo(2), "two bot matches, not one human pairing");
                Assert.That(env.Pool.Acquired, Is.EqualTo(new[] { BotOpponent.Greedy, BotOpponent.Greedy }));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Player_With_One_Completed_Match_Plays_The_Second_Match_Bot()
        {
            var env = new GateEnv(readyBots: 1);
            await env.SeedCompletedAsync("sub-a", 1);
            await env.RunAsync(async uri =>
            {
                using var a = await env.QueueAsync(uri, "sub-a");
                await ExpectAsync<MatchStartMsg>(a);
                Assert.That(env.Pool.Acquired, Is.EqualTo(new[] { BotOpponent.SecondMatch }));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Players_With_Two_Completed_Matches_Use_The_Ordinary_Queue()
        {
            var env = new GateEnv(readyBots: 2);
            await env.SeedCompletedAsync("sub-a", 2);
            await env.SeedCompletedAsync("sub-b", 3);
            await env.RunAsync(async uri =>
            {
                using var a = await env.QueueAsync(uri, "sub-a");
                using var b = await env.QueueAsync(uri, "sub-b");
                await ExpectAsync<MatchStartMsg>(a);
                await ExpectAsync<MatchStartMsg>(b);

                Assert.That(env.Gate!.LiveSessions, Is.EqualTo(1), "the two veterans were paired with each other");
                Assert.That(env.Pool.Acquired, Is.Empty);
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Newcomer_Waits_For_A_Free_Bot_Rather_Than_Meet_A_Queued_Human()
        {
            var env = new GateEnv(readyBots: 0);
            await env.SeedCompletedAsync("sub-vet", 2);
            await env.RunAsync(async uri =>
            {
                using var newcomer = await env.QueueAsync(uri, "sub-new");
                using var veteran = await env.QueueAsync(uri, "sub-vet");

                // Several wakes pass with a human sitting in the queue and no bot free.
                await Task.Delay(800);
                Assert.That(env.Gate!.LiveSessions, Is.Zero, "the newcomer must not be paired with the veteran");

                MarkReady(env.Inner, "ember_knight");
                await ExpectAsync<MatchStartMsg>(newcomer);
                Assert.That(env.Gate.LiveSessions, Is.EqualTo(1));
                Assert.That(env.Pool.Acquired, Is.EqualTo(new[] { BotOpponent.Greedy }));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Count_That_Cannot_Be_Read_Seats_The_Player_As_A_Newcomer()
        {
            var env = new GateEnv(readyBots: 1, store: new ThrowingStore(failReads: true));
            await env.RunAsync(async uri =>
            {
                using var a = await env.QueueAsync(uri, "sub-a");
                await ExpectAsync<MatchStartMsg>(a);
                Assert.That(env.Pool.Acquired, Is.EqualTo(new[] { BotOpponent.Greedy }));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Finishing_The_First_Match_Makes_The_Next_Queue_The_Second_Match_Bot()
        {
            // End to end through the real ranked sink: the first match ends by idle forfeit (the
            // stub client never acts), and the very next queue, with the row possibly still being
            // written, is owed the second-match bot.
            var env = new GateEnv(readyBots: 1, fastForfeit: true);
            await env.RunAsync(async uri =>
            {
                using (var first = await env.QueueAsync(uri, "sub-a"))
                {
                    await ExpectAsync<MatchStartMsg>(first);
                    await ExpectAsync<MatchEndMsg>(first, TimeSpan.FromSeconds(20));
                }

                // The bot is released when its match's pump finishes; poll the queue until then.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (env.Gate!.LiveSessions > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);

                using var second = await env.QueueAsync(uri, "sub-a");
                await ExpectAsync<MatchStartMsg>(second);
                Assert.That(env.Pool.Acquired, Is.EqualTo(new[] { BotOpponent.Greedy, BotOpponent.SecondMatch }));
            });
        }

        // ── The real H1 model, end to end ───────────────────────────────────────

        [Test]
        [CancelAfter(90_000)]
        public async Task The_Second_Match_Is_Decided_By_The_Shipped_H1_Model()
        {
            var store = new InMemoryCompletedMatchStore();
            var accounts = new InMemoryAccountStore(new SystemTimeSource());
            Account acct = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("sub-a"), "a@example.com");
            await store.RecordAsync(acct.PlayerId, "earlier", DateTimeOffset.UtcNow.AddMinutes(-5));

            string replays = Path.Combine(Path.GetTempPath(), "kingdom-firstmatch-h1-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                BotRosterPath = SessionHarness.RepoFile("tooling", "data", "bot-roster.json"),
                AccountStore = accounts,
                TokenVerifier = new SubjectVerifier(),
                CompletedMatches = store,
                SecondMatchBotModelPath = SessionHarness.RepoFile("tooling", "data", "bot-model-h1"),
                Options = new ServerOptions
                {
                    BotFillDelayMs = 60_000,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 150 },
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                    TurnMs = long.MaxValue / 4,
                },
            });

            await built.App.StartAsync();
            try
            {
                await Task.Delay(1500);   // the roster provisioner runs with the host
                var baseUri = new Uri(built.App.Urls.First());
                using var ws = await QueueAsync(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"), "sub-a");
                await ExpectAsync<MatchStartMsg>(ws);

                using var http = new HttpClient { BaseAddress = baseUri };
                long decisions = 0, fallbacks = 0;
                string? checkpoint = null;
                for (int attempt = 0; attempt < 50 && decisions == 0; attempt++)
                {
                    await Task.Delay(100);
                    using JsonDocument health = JsonDocument.Parse(await http.GetStringAsync("/healthz"));
                    JsonElement bot = health.RootElement.GetProperty("secondMatchBot");
                    checkpoint = bot.GetProperty("checkpoint").GetString();
                    decisions = bot.GetProperty("decisions").GetInt64();
                    fallbacks = bot.GetProperty("fallbacks").GetInt64();
                }
                Assert.That(checkpoint, Does.StartWith("978b89297989"));
                Assert.That(decisions, Is.GreaterThan(0), "the bot's mulligan must be H1's decision");
                Assert.That(fallbacks, Is.Zero);
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }

        // ── Harness ─────────────────────────────────────────────────────────────

        private static void MarkReady(BotPool pool, string username)
            => pool.MarkReady(new ResolvedBotAccount(
                username,
                AccountId.FromAuthenticatedAccount("player-" + username),
                username,
                new Decks.SeatDeck(CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck())));

        private sealed class GateEnv
        {
            private readonly bool _fastForfeit;
            private readonly ICompletedMatchStore _store;
            private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore(new SystemTimeSource());

            public GateEnv(int readyBots, ICompletedMatchStore? store = null, bool fastForfeit = false)
            {
                var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
                Inner = new BotPool(db);
                string[] names = { "ember_knight", "tide_warden" };
                for (int i = 0; i < readyBots; i++) MarkReady(Inner, names[i]);
                Pool = new RecordingPool(Inner);
                _store = store ?? new InMemoryCompletedMatchStore();
                _fastForfeit = fastForfeit;
            }

            public BotPool Inner { get; }
            public RecordingPool Pool { get; }
            public Transport.MatchGate? Gate { get; private set; }

            public async Task SeedCompletedAsync(string subject, int matches)
            {
                Account acct = await _accounts.ResolveOrCreateAsync(
                    "supabase", CredentialHash.Of(subject), subject + "@example.com");
                for (int i = 0; i < matches; i++)
                    await _store.RecordAsync(acct.PlayerId, $"seed-{subject}-{i}", DateTimeOffset.UtcNow.AddMinutes(-10 + i));
            }

            public Task<ClientWebSocket> QueueAsync(Uri uri, string subject) => FirstMatchPolicyTests.QueueAsync(uri, subject);

            public async Task RunAsync(Func<Uri, Task> body)
            {
                string replays = Path.Combine(Path.GetTempPath(), "kingdom-firstmatch-" + Guid.NewGuid().ToString("N"));
                var options = new ServerOptions
                {
                    // Long enough that ordinary bot fill never fires during a test: every bot seated
                    // here was seated by the first-match policy.
                    BotFillDelayMs = 60_000,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 100 },
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                    TurnMs = long.MaxValue / 4,
                };
                if (_fastForfeit)
                    options = options with { MulliganMs = 100, TurnMs = 100, IdleForfeitAutoTurns = 1 };
                var built = ServerApp.BuildWithGate(new ServerConfig
                {
                    Urls = "http://127.0.0.1:0",
                    CardsDir = SessionHarness.CardDataDir(),
                    ReplaysDir = replays,
                    AccountStore = _accounts,
                    TokenVerifier = new SubjectVerifier(),
                    CompletedMatches = _store,
                    Options = options,
                }, Pool);
                Gate = built.Gate;
                await built.App.StartAsync();
                try
                {
                    var baseUri = new Uri(built.App.Urls.First());
                    await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"));
                }
                finally
                {
                    await built.App.StopAsync();
                    try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
                }
            }
        }

        /// <summary>Signs in a seat as the subject its auth token names.</summary>
        private static async Task<ClientWebSocket> QueueAsync(Uri uri, string subject)
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(uri, CancellationToken.None);
            var frame = ProtocolCodec.Encode(
                new HelloMsg("device-" + subject, ProtocolContract.Version, "test", "first-match-tests",
                             DisplayName: "", AuthToken: subject),
                ProtocolCodec.PreMatch, 0);
            await ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
            return ws;
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws, TimeSpan? timeout = null) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var res = ProtocolCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (res.Ok && res.Message is T t) return t;
            }
        }

        /// <summary>Records which policy each successful acquire was for.</summary>
        private sealed class RecordingPool : IBotPool
        {
            private readonly BotPool _inner;
            private readonly ConcurrentQueue<BotOpponent> _acquired = new ConcurrentQueue<BotOpponent>();
            public RecordingPool(BotPool inner) => _inner = inner;
            public IReadOnlyList<BotOpponent> Acquired => _acquired.ToArray();

            public BotHandle? TryAcquire(BotOpponent opponent = BotOpponent.Default)
            {
                BotHandle? handle = _inner.TryAcquire(opponent);
                if (handle is not null) _acquired.Enqueue(opponent);
                return handle;
            }

            public bool IsBotAccount(AccountId account) => _inner.IsBotAccount(account);
        }

        private sealed class SubjectVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity(rawToken, rawToken + "@example.com", EmailVerified: true)));
        }

        private sealed class ThrowingStore : ICompletedMatchStore
        {
            private readonly bool _failReads;
            public ThrowingStore(bool failReads) => _failReads = failReads;

            public Task RecordAsync(string playerId, string matchId, DateTimeOffset endedAtUtc, CancellationToken ct = default)
                => throw new InvalidOperationException("store down");

            public Task<IReadOnlyList<string>> FirstMatchIdsAsync(string playerId, int limit, CancellationToken ct = default)
                => _failReads
                    ? throw new InvalidOperationException("store down")
                    : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private sealed class NamedPolicy : ISeatPolicy
        {
            public NamedPolicy(string identity) => Identity = identity;
            public string Identity { get; }
            public ValueTask<PlayerIntent?> ChooseAsync(GameState state, PlayerId player, CancellationToken cancellationToken = default)
                => new ValueTask<PlayerIntent?>((PlayerIntent?)null);
        }
    }
}
