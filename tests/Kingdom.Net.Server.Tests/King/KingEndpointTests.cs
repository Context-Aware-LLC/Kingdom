using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class KingEndpointTests
    {
        private static async Task<(JsonElement Body, bool NoStore)> GetKingAsync(long? reignOneMs)
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReignOneMs = reignOneMs,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient();
                using HttpResponseMessage response = await http.GetAsync(new Uri(new Uri(app.Urls.First()), "/king"));
                string json = await response.Content.ReadAsStringAsync();
                return (JsonDocument.Parse(json).RootElement.Clone(), response.Headers.CacheControl?.NoStore == true);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task King_route_says_none_before_reign_one()
        {
            long future = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeMilliseconds();
            foreach (long? reignOne in new long?[] { null, future })
            {
                var (body, noStore) = await GetKingAsync(reignOne);
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("none"));
                Assert.That(noStore, Is.True);
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task King_route_answers_the_current_reign()
        {
            // Three Tuesdays before the most recent one, so the current reign is 4 whatever day this runs.
            DateTime today = DateTime.UtcNow.Date;
            int sinceTuesday = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
            long reignOne = new DateTimeOffset(today.AddDays(-sinceTuesday - 21), TimeSpan.Zero).ToUnixTimeMilliseconds();

            var (body, noStore) = await GetKingAsync(reignOne);

            Reign expected = KingSchedule.For(4);
            Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(body.GetProperty("reign").GetInt32(), Is.EqualTo(4));
                Assert.That(body.GetProperty("epithet").GetInt32(), Is.EqualTo(expected.Epithet));
                Assert.That(body.GetProperty("fate").GetInt32(), Is.EqualTo(expected.Fate));
                Assert.That(body.GetProperty("commission").GetString(), Is.EqualTo(expected.Commission));
                Assert.That(body.GetProperty("endsAtMs").GetInt64(), Is.EqualTo(KingCalendar.StartMs(5, reignOne)));
                Assert.That(noStore, Is.True);
            });
        }
    }
}
