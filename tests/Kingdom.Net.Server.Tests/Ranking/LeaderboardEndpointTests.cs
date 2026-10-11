using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Ranking
{
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class LeaderboardEndpointTests
    {
        [Test]
        [CancelAfter(30_000)]
        public async Task Endpoint_returns_ranked_public_projection_without_player_ids()
        {
            var ratings = new InMemoryRatingStore();
            await ratings.ApplyAsync(new RankedMatchResult(
                new MatchId("endpoint-match"),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                AccountId.FromAuthenticatedAccount("private-ash-id"),
                "Ash",
                AccountId.FromAuthenticatedAccount("private-cinder-id"),
                "Cinder",
                MatchOutcome.Player0Win,
                TelemetrySource.Ladder));

            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                RatingStore = ratings,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient();
                string json = await http.GetStringAsync(new Uri(new Uri(app.Urls.First()), "/leaderboard?limit=10"));
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement entries = document.RootElement.GetProperty("entries");

                Assert.That(document.RootElement.GetProperty("ratingSystem").GetString(), Is.EqualTo("elo"));
                Assert.That(entries.GetArrayLength(), Is.EqualTo(2));
                Assert.Multiple(() =>
                {
                    Assert.That(entries[0].GetProperty("rank").GetInt32(), Is.EqualTo(1));
                    Assert.That(entries[0].GetProperty("displayName").GetString(), Is.EqualTo("Ash"));
                    Assert.That(entries[0].GetProperty("rating").GetInt32(), Is.EqualTo(1_020));
                    Assert.That(json, Does.Not.Contain("private-ash-id"));
                    Assert.That(json, Does.Not.Contain("playerId"));
                });
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
