using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary><c>GET /king/favor</c> and <c>GET /king/favor/me</c> through the real composition root.</summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class FavorBoardEndpointTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

        /// <summary>Reign one placed so that <paramref name="reign"/> is the current one, a minute in.</summary>
        private static long ReignOneFor(int reign)
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (reign - 1) * KingCalendar.WeekMs - 60_000;

        private static FavorScore Score(int reign, string player, int score, DateTimeOffset at, string? name = null)
            => new FavorScore(reign, player, name ?? ("name-" + player), score, at);

        private sealed class Server : IAsyncDisposable
        {
            public InMemoryFavorStore Favor { get; } = new InMemoryFavorStore();
            public InMemoryAccountStore Accounts { get; } = new InMemoryAccountStore(new FakeTime());
            private Microsoft.AspNetCore.Builder.WebApplication? _app;
            private readonly HttpClient _http = new HttpClient();

            public async Task StartAsync(long? reignOneMs)
            {
                _app = ServerApp.Build(new ServerConfig
                {
                    Urls = "http://127.0.0.1:0",
                    CardsDir = SessionHarness.CardDataDir(),
                    ReignOneMs = reignOneMs,
                    Favor = Favor,
                    AccountStore = Accounts,
                    TokenVerifier = new GoodOrBadVerifier(),
                });
                await _app.StartAsync();
            }

            /// <summary>The player id the route will resolve the "good" bearer to.</summary>
            public async Task<string> GoodPlayerIdAsync()
                => (await Accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("subject-good"), "good@example.com")).PlayerId;

            public async Task<(HttpStatusCode Status, JsonElement Body, bool NoStore)> GetAsync(string path, string? bearer = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_app!.Urls.First()), path));
                if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using HttpResponseMessage response = await _http.SendAsync(request);
                string json = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone(),
                    response.Headers.CacheControl?.NoStore == true);
            }

            public async ValueTask DisposeAsync()
            {
                _http.Dispose();
                if (_app != null)
                {
                    await _app.StopAsync();
                    await _app.DisposeAsync();
                }
            }
        }

        /// <summary>"good" is one signed-in player; any other token is rejected.</summary>
        private sealed class GoodOrBadVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(rawToken == "good"
                    ? TokenVerificationResult.Ok(new VerifiedIdentity("subject-good", "good@example.com", EmailVerified: true))
                    : TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
        }

        private static (int Place, string? Name, int Score)[] Rows(JsonElement body)
            => body.GetProperty("rows").EnumerateArray()
                .Select(r => (r.GetProperty("place").GetInt32(), r.GetProperty("name").GetString(), r.GetProperty("score").GetInt32()))
                .ToArray();

        [Test]
        [CancelAfter(30_000)]
        public async Task Both_routes_say_none_before_reign_one()
        {
            long future = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeMilliseconds();
            foreach (long? reignOne in new long?[] { null, future })
            {
                await using var server = new Server();
                await server.StartAsync(reignOne);

                var board = await server.GetAsync("/king/favor");
                var me = await server.GetAsync("/king/favor/me", "good");

                Assert.Multiple(() =>
                {
                    Assert.That(board.Body.GetProperty("status").GetString(), Is.EqualTo("none"));
                    Assert.That(board.NoStore, Is.True);
                    Assert.That(me.Body.GetProperty("status").GetString(), Is.EqualTo("none"));
                    Assert.That(me.NoStore, Is.True);
                });
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Rows_sort_highest_first_for_a_higher_is_better_commission()
        {
            Assume.That(KingSchedule.For(1).Commission, Is.EqualTo(CommissionIds.Overkill));
            await using var server = new Server();
            await server.Favor.RecordAsync(Score(1, "low", 3, T0), false);
            await server.Favor.RecordAsync(Score(1, "high", 9, T0), false);
            await server.Favor.RecordAsync(Score(1, "mid", 5, T0), false);
            await server.StartAsync(ReignOneFor(1));

            var (status, body, noStore) = await server.GetAsync("/king/favor");

            Assert.Multiple(() =>
            {
                Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(noStore, Is.True);
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(body.GetProperty("reign").GetInt32(), Is.EqualTo(1));
                Assert.That(body.GetProperty("commission").GetString(), Is.EqualTo(CommissionIds.Overkill));
                Assert.That(body.GetProperty("lowerIsBetter").GetBoolean(), Is.False);
                Assert.That(Rows(body), Is.EqualTo(new (int, string?, int)[]
                    { (1, "name-high", 9), (2, "name-mid", 5), (3, "name-low", 3) }));
            });
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Rows_sort_lowest_first_for_a_lower_is_better_commission()
        {
            Assume.That(KingSchedule.For(2).Commission, Is.EqualTo(CommissionIds.Technically));
            await using var server = new Server();
            await server.Favor.RecordAsync(Score(2, "high", 9, T0), true);
            await server.Favor.RecordAsync(Score(2, "low", 1, T0), true);
            await server.Favor.RecordAsync(Score(2, "mid", 5, T0), true);
            await server.Favor.RecordAsync(Score(1, "other-reign", 0, T0), true);
            await server.StartAsync(ReignOneFor(2));

            var (_, body, _) = await server.GetAsync("/king/favor");

            Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("reign").GetInt32(), Is.EqualTo(2));
                Assert.That(body.GetProperty("lowerIsBetter").GetBoolean(), Is.True);
                Assert.That(Rows(body), Is.EqualTo(new (int, string?, int)[]
                    { (1, "name-low", 1), (2, "name-mid", 5), (3, "name-high", 9) }));
            });
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task A_tie_goes_to_whoever_reached_it_first()
        {
            await using var server = new Server();
            await server.Favor.RecordAsync(Score(1, "late", 6, T0.AddHours(3)), false);
            await server.Favor.RecordAsync(Score(1, "early", 6, T0), false);
            await server.StartAsync(ReignOneFor(1));

            var (_, body, _) = await server.GetAsync("/king/favor");

            Assert.That(Rows(body).Select(r => r.Name), Is.EqualTo(new[] { "name-early", "name-late" }));
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task The_board_shows_one_hundred_rows()
        {
            await using var server = new Server();
            for (int i = 1; i <= 105; i++) await server.Favor.RecordAsync(Score(1, "p" + i, i, T0), false);
            await server.StartAsync(ReignOneFor(1));

            var (_, body, _) = await server.GetAsync("/king/favor");

            (int Place, string? Name, int Score)[] rows = Rows(body);
            Assert.That(rows, Has.Length.EqualTo(100));
            Assert.That(rows.First().Score, Is.EqualTo(105));
            Assert.That(rows.Last().Place, Is.EqualTo(100));
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Me_answers_a_player_off_the_list()
        {
            await using var server = new Server();
            string me = await server.GoodPlayerIdAsync();
            for (int i = 0; i < 100; i++) await server.Favor.RecordAsync(Score(1, "ahead" + i, 50 + i, T0), false);
            await server.Favor.RecordAsync(Score(1, me, 7, T0), false);
            await server.StartAsync(ReignOneFor(1));

            var (status, body, noStore) = await server.GetAsync("/king/favor/me", "good");

            Assert.Multiple(() =>
            {
                Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(noStore, Is.True);
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(body.GetProperty("place").GetInt32(), Is.EqualTo(101));
                Assert.That(body.GetProperty("score").GetInt32(), Is.EqualTo(7));
            });
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Me_without_a_score_this_reign_has_no_place()
        {
            await using var server = new Server();
            string me = await server.GoodPlayerIdAsync();
            await server.Favor.RecordAsync(Score(1, me, 7, T0), false);
            await server.StartAsync(ReignOneFor(2));

            var (_, body, _) = await server.GetAsync("/king/favor/me", "good");

            Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(body.GetProperty("place").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(body.GetProperty("score").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(body.GetProperty("seals").EnumerateArray().Select(s => s.GetString()),
                    Is.EqualTo(new[] { KingSchedule.For(1).Commission }), "a past reign's seal is kept");
            });
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Me_lists_one_seal_per_commission_across_reigns()
        {
            // The first reign after 1 with reign 1's commission. Searched, not computed from a pool
            // size: an epoch that grows the rotation (#443) moves it, and the Assume that used to stand
            // here turned the test inconclusive, not red, when it did.
            int again = Enumerable.Range(2, 100).First(r => KingSchedule.For(r).Commission == KingSchedule.For(1).Commission);
            Assert.That(again, Is.GreaterThan(3), "precondition: reigns 2 and 3 hold other commissions");
            await using var server = new Server();
            string me = await server.GoodPlayerIdAsync();
            await server.Favor.RecordAsync(Score(1, me, 4, T0), false);
            await server.Favor.RecordAsync(Score(2, me, 4, T0), true);
            await server.Favor.RecordAsync(Score(again, me, 4, T0), false);
            await server.Favor.RecordAsync(Score(3, "someone-else", 4, T0), false);
            await server.StartAsync(ReignOneFor(again + 1));

            var (_, body, _) = await server.GetAsync("/king/favor/me", "good");

            string[] seals = body.GetProperty("seals").EnumerateArray().Select(s => s.GetString()!).ToArray();
            Assert.That(seals, Is.EqualTo(new[] { KingSchedule.For(1).Commission, KingSchedule.For(2).Commission }),
                "oldest first, each commission once, at the reign it was first earned");
        }

        [TestCase("bad")]
        [TestCase(null)]
        [CancelAfter(30_000)]
        public async Task Me_without_a_good_bearer_is_401(string? bearer)
        {
            await using var server = new Server();
            await server.StartAsync(ReignOneFor(1));

            var (status, body, _) = await server.GetAsync("/king/favor/me", bearer);

            Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("unauthenticated"));
        }

        // Carried from Task 5: an unnamed player banks under MatchGate's per-match seat handle.
        [Test]
        [CancelAfter(30_000)]
        public async Task The_board_never_shows_a_seat_handle()
        {
            await using var server = new Server();
            await server.Favor.RecordAsync(Score(1, "a", 9, T0, "seat-17"), false);
            await server.Favor.RecordAsync(Score(1, "b", 8, T0, ""), false);
            await server.Favor.RecordAsync(Score(1, "c", 7, T0, "seat-of-power"), false);
            await server.StartAsync(ReignOneFor(1));

            var (_, body, _) = await server.GetAsync("/king/favor");

            Assert.That(Rows(body).Select(r => r.Name), Is.EqualTo(new[] { "", "", "seat-of-power" }),
                "a handle or a blank goes out empty, for the client's placeholder; a chosen name is untouched");
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task The_Roll_never_shows_a_seat_handle()
        {
            await using var server = new Server();
            server.Favor.SeedReign(2, "a", "seat-3", false);
            server.Favor.SeedReign(1, "b", "Alice", false);
            await server.StartAsync(ReignOneFor(3));

            var (_, body, _) = await server.GetAsync("/king/roll");

            Assert.That(body.GetProperty("reigns").EnumerateArray().Select(r => r.GetProperty("favorite").GetString()),
                Is.EqualTo(new string?[] { null, "", "Alice" }),
                "the reigning King has no Favorite yet; an unnamed Favorite is empty, not absent");
        }
    }
}
