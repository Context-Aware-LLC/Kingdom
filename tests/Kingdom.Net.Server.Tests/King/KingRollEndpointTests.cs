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
    public sealed class KingRollEndpointTests
    {
        private static long ReignOneThreeTuesdaysAgo()
        {
            DateTime today = DateTime.UtcNow.Date;
            int sinceTuesday = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
            return new DateTimeOffset(today.AddDays(-sinceTuesday - 21), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        private static async Task<(JsonElement Body, bool NoStore)> GetRollAsync(long? reignOneMs, IFavorStore? favor)
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReignOneMs = reignOneMs,
                Favor = favor,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient();
                using HttpResponseMessage response = await http.GetAsync(new Uri(new Uri(app.Urls.First()), "/king/roll"));
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
        public async Task Roll_says_none_before_reign_one()
        {
            var (body, noStore) = await GetRollAsync(null, null);
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("none"));
            Assert.That(noStore, Is.True);
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task Roll_lists_every_reign_newest_first_with_the_current_one_unfavored()
        {
            var favor = new InMemoryFavorStore();
            favor.SeedReign(3, "c", "Cy", false);
            favor.SeedReign(2, "b", "Bob", true);   // struck: reads as none
            favor.SeedReign(1, "a", "Alice", false);

            var (body, noStore) = await GetRollAsync(ReignOneThreeTuesdaysAgo(), favor);

            JsonElement[] reigns = body.GetProperty("reigns").EnumerateArray().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(noStore, Is.True);
                Assert.That(reigns.Select(r => r.GetProperty("reign").GetInt32()), Is.EqualTo(new[] { 4, 3, 2, 1 }));
                Assert.That(reigns.Select(r => r.GetProperty("favorite").GetString()),
                    Is.EqualTo(new string?[] { null, "Cy", null, "Alice" }));
                Reign three = KingSchedule.For(3);
                Assert.That(reigns[1].GetProperty("epithet").GetInt32(), Is.EqualTo(three.Epithet));
                Assert.That(reigns[1].GetProperty("fate").GetInt32(), Is.EqualTo(three.Fate));
            });
        }
    }
}
