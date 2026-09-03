using System.Linq;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Auth;
using Emberfall.Net.Client.Collection;
using Emberfall.Net.Client.Shop;
using Emberfall.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Collection
{
    /// <summary>
    /// <c>POST /packs/open</c>, client side (spec
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, slice 0).
    ///
    /// <para><b>Two things here are worth more than the status table.</b> The first is that the
    /// request carries a pack LINE and nothing else — no instance id, no card count, no odds — because
    /// every one of those being server-decided is what makes the open exactly-once and the odds
    /// unforgeable. The second is that a 200 which decodes to no cards is
    /// <see cref="PackOpenStatus.Malformed"/>: the pack is spent by the time the response is written,
    /// so an empty reveal is the one outcome a player can neither understand nor recover from.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpPackGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";
        private const string Starter = "pack.emberfall.starter";

        private static HttpPackGateway Gateway(FakeAuthHttp http)
            => new HttpPackGateway(Base, http);

        private static string Receipt(string status, string instance, params string[] awards)
            => "{\"status\":\"" + status + "\",\"packId\":\"" + Starter + "\"," +
               "\"packInstanceId\":\"" + instance + "\",\"awards\":[" + string.Join(",", awards) + "]}";

        private static string Award(int cardId, string rarity, string upgrade = "none")
            => "{\"cardId\":" + cardId + ",\"rarity\":\"" + rarity + "\",\"upgrade\":\"" + upgrade + "\"}";

        // ── The request ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_open_is_a_post_naming_only_the_pack_line()
        {
            var http = new FakeAuthHttp().Reply(200,
                Receipt("opened", "open:p1:" + Starter + ":0", Award(1001, "COMMON")));

            await Gateway(http).OpenAsync(Starter, "jwt-token");

            (string url, string? body, var headers, string verb) = http.Requests[0];

            Assert.That(url, Is.EqualTo("https://emberfall.fly.dev/packs/open"));
            Assert.That(verb, Is.EqualTo("POST"));
            Assert.That(headers.Any(h => h.Key == "Authorization" && h.Value == "Bearer jwt-token"),
                Is.True);

            // The whole body. Not "contains packId" — an assertion that the request carries NOTHING
            // ELSE is the point, and a `contains` would pass with an instance id smuggled beside it.
            Assert.That(body, Is.EqualTo("{\"packId\":\"" + Starter + "\"}"));
        }

        [Test]
        public async Task A_signed_out_device_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Unauthenticated));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_unconfigured_endpoint_is_unavailable_rather_than_a_failed_request()
        {
            var http = new FakeAuthHttp();

            PackOpenResult result = await new HttpPackGateway("", http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Unavailable));
            Assert.That(http.Requests, Is.Empty);
        }

        // ── The reveal ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task An_opened_pack_comes_back_as_a_receipt_in_slot_order()
        {
            var http = new FakeAuthHttp().Reply(200, Receipt(
                "opened", "open:p1:" + Starter + ":0",
                Award(1001, "COMMON"),
                Award(1042, "RARE"),
                Award(1099, "LEGENDARY", "legendarypity")));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Opened));
            Assert.That(result.HasReveal, Is.True);
            Assert.That(result.Receipt!.PackId, Is.EqualTo(Starter));
            Assert.That(result.Receipt!.PackInstanceId, Is.EqualTo("open:p1:" + Starter + ":0"));
            Assert.That(result.Receipt!.Awards.Select(a => a.CardId),
                Is.EqualTo(new[] { 1001, 1042, 1099 }),
                "slot order is the reveal order and the server chose it");
            Assert.That(result.Receipt!.Awards[2].Rarity, Is.EqualTo(Rarity.LEGENDARY));
            Assert.That(result.Receipt!.Awards[2].Upgrade,
                Is.EqualTo(PackUpgradeReason.LegendaryPity));
        }

        [Test]
        public async Task A_replay_carries_the_same_cards_and_is_told_apart()
        {
            // The retried tap. The client shows these exactly as it would a fresh open — the two
            // statuses exist so a log line can tell them apart, not so the reveal can.
            var http = new FakeAuthHttp().Reply(200, Receipt(
                "alreadyopened", "open:p1:" + Starter + ":0", Award(1001, "COMMON")));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.AlreadyOpened));
            Assert.That(result.HasReveal, Is.True);
            Assert.That(result.Receipt!.Awards.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task A_repeated_card_in_two_slots_survives_the_decode()
        {
            // Why `awards` is an array and not an object keyed by anything: two slots may legally be
            // the same card, and a map would silently collapse them into one.
            var http = new FakeAuthHttp().Reply(200, Receipt(
                "opened", "open:p1:" + Starter + ":0",
                Award(1001, "COMMON"), Award(1001, "COMMON")));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Receipt!.Awards.Count, Is.EqualTo(2));
            Assert.That(result.Receipt!.Awards.All(a => a.CardId == 1001), Is.True);
        }

        [Test]
        public async Task An_unknown_rarity_word_costs_the_chrome_and_not_the_card()
        {
            // The card is the entitlement and it is already granted server-side. Losing the whole
            // reveal over a rarity spelling would be the worse trade.
            var http = new FakeAuthHttp().Reply(200, Receipt(
                "opened", "open:p1:" + Starter + ":0", Award(1001, "MYTHIC", "wat")));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Opened));
            Assert.That(result.Receipt!.Awards[0].CardId, Is.EqualTo(1001));
            Assert.That(result.Receipt!.Awards[0].Rarity, Is.EqualTo(Rarity.COMMON));
            Assert.That(result.Receipt!.Awards[0].Upgrade, Is.EqualTo(PackUpgradeReason.None));
        }

        // ── The refusals ────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_player_with_no_packs_is_told_so_rather_than_shown_nothing()
        {
            var http = new FakeAuthHttp().Reply(409,
                "{\"status\":\"noneowned\",\"packId\":\"" + Starter + "\",\"awards\":[]}");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.NoneOwned));
            Assert.That(result.HasReveal, Is.False);
        }

        [Test]
        public async Task A_ledger_conflict_reaches_the_log_even_though_it_shares_a_status()
        {
            // One client outcome — there is no pack to reveal — but two very different server states,
            // and folding the defect into the same detail as "you have no packs" would hide it.
            var http = new FakeAuthHttp().Reply(409, "{\"status\":\"conflict\",\"awards\":[]}");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.NoneOwned));
            Assert.That(result.Detail, Does.StartWith("ledger conflict:"));
        }

        [Test]
        public async Task An_unknown_pack_line_and_a_rejected_token_are_told_apart()
        {
            var unknown = await Gateway(new FakeAuthHttp().Reply(400, "{}")).OpenAsync(Starter, "jwt");
            var rejected = await Gateway(new FakeAuthHttp().Reply(401, "{}")).OpenAsync(Starter, "jwt");
            var noLine = await Gateway(new FakeAuthHttp().Reply(503, "{}")).OpenAsync(Starter, "jwt");

            Assert.That(unknown.Status, Is.EqualTo(PackOpenStatus.UnknownPack));
            Assert.That(rejected.Status, Is.EqualTo(PackOpenStatus.Unauthenticated));
            Assert.That(noLine.Status, Is.EqualTo(PackOpenStatus.Unavailable));
        }

        [Test]
        public async Task An_empty_pack_id_never_leaves_the_device()
        {
            var http = new FakeAuthHttp();

            PackOpenResult result = await Gateway(http).OpenAsync("", "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.UnknownPack));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task A_dropped_response_is_offline_and_carries_no_receipt()
        {
            // The status a caller must NOT retry. The open is committed before the response is
            // written, so a retry derives the next instance id and spends a second pack; the recovery
            // is to re-read GET /collection. That rule lives on the gateway's own doc comment.
            var http = new FakeAuthHttp().ReplyTransportFailure("no route to host");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Offline));
            Assert.That(result.HasReveal, Is.False);
        }

        // ── The decodes that must not become an empty reveal ────────────────────────────

        [Test]
        public async Task A_two_hundred_with_no_awards_is_malformed_not_an_empty_pack()
        {
            var http = new FakeAuthHttp().Reply(200, Receipt("opened", "open:p1:x:0"));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Malformed));
            Assert.That(result.HasReveal, Is.False);
        }

        [Test]
        public async Task A_two_hundred_with_no_awards_field_at_all_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"opened\",\"packId\":\"" + Starter + "\",\"packInstanceId\":\"i\"}");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Malformed));
        }

        [Test]
        public async Task A_two_hundred_with_no_status_word_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"packId\":\"" + Starter + "\",\"packInstanceId\":\"i\",\"awards\":[" +
                Award(1001, "COMMON") + "]}");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Malformed));
        }

        [Test]
        public async Task An_unreadable_body_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200, "<html>captive portal</html>");

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Malformed));
        }

        [Test]
        public async Task An_award_with_no_card_id_fails_the_whole_receipt()
        {
            // Half a pack is not a smaller pack. The cards are already granted, so a partial reveal
            // teaches the player they got fewer than they did — with no way to find out otherwise.
            var http = new FakeAuthHttp().Reply(200, Receipt(
                "opened", "open:p1:x:0",
                Award(1001, "COMMON"), "{\"rarity\":\"RARE\",\"upgrade\":\"none\"}"));

            PackOpenResult result = await Gateway(http).OpenAsync(Starter, "jwt");

            Assert.That(result.Status, Is.EqualTo(PackOpenStatus.Malformed));
        }
    }
}
