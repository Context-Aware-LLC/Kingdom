using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Auth;
using Kingdom.Net.Client.Tests.Auth;
using Kingdom.Net.Client.Collection;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Collection
{
    /// <summary>
    /// The ledger read-down, client side (spec <c>docs/dev-docs/specs/card-ownership.md</c>, slice 3).
    ///
    /// <para><b>What every test here is really guarding is one distinction:</b> a collection that came
    /// back EMPTY and a collection that did not . <see cref="CollectionSnapshot.ShouldReplaceCache"/>
    /// is the single place that call is made, so it is what these assert against.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpCollectionGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        private static HttpCollectionGateway Gateway(FakeAuthHttp http)
            => new HttpCollectionGateway(Base, http);

        private static OwnedCardCounts Owned(params (int id, int copies)[] rows)
        {
            var counts = new OwnedCardCounts();
            foreach (var row in rows) counts.Set(row.id, row.copies);
            return counts;
        }

        private static string CardsJson(OwnedCardCounts counts)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var row in counts.Entries())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(row.Key).Append("\":").Append(row.Value);
            }
            return sb.Append('}').ToString();
        }

        /// <summary>A list-rule body whose digest honestly matches its cards.</summary>
        private static string ListBody(OwnedCardCounts counts, string tail = "", string packs = "{}")
            => "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"" +
               OwnershipRecord.List(counts).Digest + "\",\"cardsUnchanged\":false,\"cards\":" +
               CardsJson(counts) + ",\"packs\":" + packs + tail + "}";

        private static string AllAtLimitBody(string tail = "", string packs = "{}")
            => "{\"ownership\":\"known\",\"rule\":\"all-at-limit\",\"digest\":\"" +
               OwnershipRecord.AllAtLimit.Digest + "\",\"cardsUnchanged\":false,\"packs\":" +
               packs + tail + "}";

        [TestCase(false)]
        [TestCase(true)]
        public async Task Cosmetic_ownership_is_numeric_and_independent_of_gameplay_ownership(bool allAtLimit)
        {
            string tail = ",\"cardBacks\":{\"1\":1,\"2\":0,\"3\":-1,\"gravekeep_allover\":1}";
            var http = new FakeAuthHttp().Reply(200,
                allAtLimit ? AllAtLimitBody(tail) : ListBody(Owned(), tail));
            var result = await Gateway(http).FetchAsync("jwt", null);
            Assert.That(result.OwnsCardBack(1), Is.True);
            Assert.That(result.OwnsCardBack(2), Is.False);
            Assert.That(result.OwnsCardBack(3), Is.False);
            Assert.That(result.CardBacks!.Count, Is.EqualTo(1));
        }

        [TestCase("")]
        [TestCase(",\"cardBacks\":null")]
        public async Task Missing_cosmetic_ownership_never_unlocks_even_for_superusers(string field)
        {
            var http = new FakeAuthHttp().Reply(200, AllAtLimitBody(field));
            var result = await Gateway(http).FetchAsync("jwt", null);
            Assert.That(result.CardBacks, Is.Null);
            Assert.That(result.OwnsCardBack(1), Is.False);
        }

        // ── The request ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_fetch_is_a_get_carrying_the_bearer()
        {
            var http = new FakeAuthHttp().Reply(200, ListBody(Owned()));

            await Gateway(http).FetchAsync("jwt-token", null);

            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/collection"));
            // Null body is how the fake records a GET — a POST with an empty body would reach the same
            // URL and be a different request.
            Assert.That(http.Requests[0].Body, Is.Null);
            Assert.That(http.Requests[0].Headers.Any(
                h => h.Key == "Authorization" && h.Value == "Bearer jwt-token"), Is.True);
        }

        [Test]
        public async Task A_signed_out_device_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Unauthenticated));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_unconfigured_endpoint_is_unavailable_rather_than_a_failed_request()
        {
            var http = new FakeAuthHttp();

            CollectionSnapshot snapshot = await new HttpCollectionGateway("", http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Unavailable));
            Assert.That(http.Requests, Is.Empty);
        }

        // ── The answers that may be cached ──────────────────────────────────────────────

        [Test]
        public async Task A_known_collection_comes_back_as_counts()
        {
            var http = new FakeAuthHttp().Reply(200, ListBody(Owned((1001, 2), (1042, 1)),
                ",\"playerId\":\"abc\",\"wallet\":{\"gold\":150,\"gems\":0,\"shards\":40}"));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.ShouldReplaceCache, Is.True);
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1001), Is.EqualTo(2));
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1042), Is.EqualTo(1));
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(9999), Is.EqualTo(0));
            Assert.That(snapshot.Wallet.Gold, Is.EqualTo(150));
            Assert.That(snapshot.Wallet.Shards, Is.EqualTo(40));
        }

        [Test]
        public async Task An_empty_known_collection_is_a_real_answer_and_is_cached()
        {
            // The load-bearing one. "known" with no cards means this player owns nothing, and it MUST
            // be written — that is what makes their decks read as drafts instead of silently legal.
            var http = new FakeAuthHttp().Reply(200, ListBody(Owned()));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.ShouldReplaceCache, Is.True);
            Assert.That(snapshot.Record!.Counts!.RecordedCards, Is.EqualTo(0));
        }

        [Test]
        public async Task A_row_that_does_not_parse_costs_only_that_row()
        {
            // Same posture as LedgerOwnedCounts server-side: a row written under a future or foreign
            // item shape must not take the rest of the collection with it.
            string digest = OwnershipRecord.List(Owned((1001, 2), (1099, 1))).Digest;
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"" + digest + "\"," +
                "\"cardsUnchanged\":false,\"packs\":{}," +
                "\"cards\":{\"1001\":2,\"not-a-card\":3,\"1042\":\"x\",\"1077\":-4,\"1099\":1}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1001), Is.EqualTo(2));
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1099), Is.EqualTo(1));
            // A negative balance is a ledger bug; zero is the honest projection of it, and a negative
            // copy count would make DeckValidator's copy-limit arithmetic nonsense.
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1077), Is.EqualTo(0));
            Assert.That(snapshot.Record!.Counts!.RecordedCards, Is.EqualTo(2));
        }

        // ── The answers that must NOT be cached ─────────────────────────────────────────

        [Test]
        public async Task Parses_AllAtLimit()
        {
            var http = new FakeAuthHttp().Reply(200,
                AllAtLimitBody(",\"wallet\":{\"gold\":0,\"gems\":0,\"shards\":0}"));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.ShouldReplaceCache, Is.True);
            Assert.That(snapshot.Record, Is.SameAs(OwnershipRecord.AllAtLimit));
        }

        [Test]
        public async Task An_AllAtLimit_body_with_the_wrong_digest_is_malformed()
        {
            string wrong = OwnershipRecord.List(Owned()).Digest;
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"all-at-limit\",\"digest\":\"" + wrong +
                "\",\"cardsUnchanged\":false,\"packs\":{}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
        }

        [Test]
        public async Task Parses_List_And_VerifiesDigest()
        {
            // A proxy that rewrote the list must not be cached under the server's digest.
            string digest = OwnershipRecord.List(Owned((1001, 2))).Digest;
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"" + digest +
                "\",\"cardsUnchanged\":false,\"packs\":{},\"cards\":{\"1001\":3}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
        }

        [Test]
        public async Task CardsUnchanged_IsSynced_WithNoRecord_AndShouldNotReplaceCache()
        {
            string digest = OwnershipRecord.List(Owned((1001, 2))).Digest;
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"" + digest +
                "\",\"cardsUnchanged\":true,\"packs\":{\"pack.pendingham.starter\":2}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", digest);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.CardsUnchanged, Is.True);
            Assert.That(snapshot.Record, Is.Null);
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
            Assert.That(snapshot.HasAuthoritativePacks, Is.True);
            Assert.That(snapshot.PackCount("pack.pendingham.starter"), Is.EqualTo(2));
        }

        [Test]
        public async Task CardsUnchanged_WithoutAWellFormedDigest_IsMalformed()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"ABC\"," +
                "\"cardsUnchanged\":true,\"packs\":{}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
        }

        [Test]
        public async Task UnknownRule_IsMalformed()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"everything\",\"digest\":\"" +
                OwnershipRecord.AllAtLimit.Digest + "\",\"cardsUnchanged\":false,\"packs\":{}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
        }

        [Test]
        public async Task The_retired_unconstrained_word_is_not_an_ownership_answer()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"ownership\":\"unconstrained\",\"packs\":{}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.ShouldReplaceCache, Is.False);
            Assert.That(snapshot.Record, Is.Null);
        }

        [Test]
        public async Task SendsDigestHeader_WhenGiven()
        {
            string digest = OwnershipRecord.AllAtLimit.Digest;
            var http = new FakeAuthHttp().Reply(200, AllAtLimitBody());

            await Gateway(http).FetchAsync("jwt", digest);

            Assert.That(http.Requests[0].Headers.Any(
                h => h.Key == "Kingdom-Ownership-Digest" && h.Value == digest), Is.True);
        }

        [Test]
        public async Task OmitsDigestHeader_WhenNull()
        {
            var http = new FakeAuthHttp().Reply(200, AllAtLimitBody());

            await Gateway(http).FetchAsync("jwt", null);

            Assert.That(http.Requests[0].Headers.Any(h => h.Key == "Kingdom-Ownership-Digest"), Is.False);
        }

        [Test]
        public async Task Only_successful_collection_answers_carry_an_authoritative_wallet()
        {
            CollectionSnapshot known = await Gateway(new FakeAuthHttp().Reply(200,
                ListBody(Owned(), ",\"wallet\":{\"gold\":60,\"gems\":2,\"shards\":4}")))
                .FetchAsync("jwt", null);
            CollectionSnapshot unconstrained = await Gateway(new FakeAuthHttp().Reply(200,
                AllAtLimitBody(",\"wallet\":{\"gold\":60,\"gems\":2,\"shards\":4}")))
                .FetchAsync("jwt", null);
            CollectionSnapshot offline = await Gateway(new FakeAuthHttp()
                .ReplyTransportFailure("no route to host"))
                .FetchAsync("jwt", null);
            CollectionSnapshot missingWallet = await Gateway(new FakeAuthHttp().Reply(200,
                ListBody(Owned())))
                .FetchAsync("jwt", null);
            CollectionSnapshot partialWallet = await Gateway(new FakeAuthHttp().Reply(200,
                ListBody(Owned(), ",\"wallet\":{\"gold\":60,\"gems\":2}")))
                .FetchAsync("jwt", null);

            Assert.Multiple(() =>
            {
                Assert.That(known.HasAuthoritativeWallet, Is.True);
                Assert.That(unconstrained.HasAuthoritativeWallet, Is.True);
                Assert.That(offline.HasAuthoritativeWallet, Is.False,
                    "A failure snapshot's default-valued wallet must not masquerade as a real zero.");
                Assert.That(missingWallet.Status, Is.EqualTo(CollectionSyncStatus.Synced),
                    "A missing wallet must not discard an otherwise valid ownership answer.");
                Assert.That(missingWallet.HasAuthoritativePacks, Is.True,
                    "Wallet completeness must not invalidate the pack shelf in the same answer.");
                Assert.That(missingWallet.HasAuthoritativeWallet, Is.False);
                Assert.That(partialWallet.HasAuthoritativeWallet, Is.False,
                    "A partial wallet must not render missing currency fields as real zeroes.");
            });
        }

        [Test]
        public async Task An_offline_device_keeps_whatever_it_had()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure("no route to host");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Offline));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
            Assert.That(snapshot.Record, Is.Null);
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task A_timed_out_first_read_retries_once_and_uses_the_server_answer()
        {
            var http = new FakeAuthHttp()
                .ReplyTransportFailure("Request timeout")
                .Reply(200, ListBody(Owned((1001, 2))));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(http.Requests.Count, Is.EqualTo(2));
            Assert.That(http.Requests.All(r => r.Verb == "GET" && r.Url == Base + "/collection"), Is.True);
            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.Record!.Counts!.CopiesOwned(1001), Is.EqualTo(2));
        }

        [Test]
        public async Task A_second_timeout_stays_offline_without_an_unbounded_retry()
        {
            var http = new FakeAuthHttp()
                .ReplyTransportFailure("Request timeout")
                .ReplyTransportFailure("Request timeout");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(http.Requests.Count, Is.EqualTo(2));
            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Offline));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
        }

        [Test]
        public async Task A_cancelled_timeout_does_not_retry()
        {
            using var cancellation = new CancellationTokenSource();
            var http = new FakeAuthHttp().ReplyTransportFailure("Request timeout");
            cancellation.Cancel();

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null, cancellation.Token);

            Assert.That(http.Requests.Count, Is.LessThanOrEqualTo(1));
            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Offline));
        }

        [Test]
        public async Task A_rejected_token_and_a_server_without_a_ledger_are_told_apart()
        {
            var rejected = await Gateway(new FakeAuthHttp().Reply(401, "{}")).FetchAsync("jwt", null);
            var noLedger = await Gateway(new FakeAuthHttp().Reply(503, "{}")).FetchAsync("jwt", null);

            Assert.That(rejected.Status, Is.EqualTo(CollectionSyncStatus.Unauthenticated));
            Assert.That(noLedger.Status, Is.EqualTo(CollectionSyncStatus.Unavailable));
            Assert.That(rejected.ShouldReplaceCache, Is.False);
            Assert.That(noLedger.ShouldReplaceCache, Is.False);
        }

        /// <summary>
        /// A 500 is a FAULT, not a configuration, and telling the player "this server has no pack
        /// economy" over one is a lie that reads as permanent. It cost Sam his onboarding gift on
        /// 2026-09-04: an overflowing ledger sum 500'd /collection and the pack vault rendered the
        /// Unavailable copy. Offline is the honest mapping — it says nothing about ownership, so it
        /// still cannot overwrite the cache, and every screen already words it as "try again".
        /// </summary>
        [Test]
        public async Task A_five_hundred_is_a_fault_not_a_server_without_an_economy()
        {
            var faulted = await Gateway(new FakeAuthHttp().Reply(500, "")).FetchAsync("jwt", null);

            Assert.That(faulted.Status, Is.EqualTo(CollectionSyncStatus.Offline));
            Assert.That(faulted.ShouldReplaceCache, Is.False);
            Assert.That(faulted.Record, Is.Null);
        }

        [Test]
        public async Task A_two_hundred_with_no_ownership_word_is_malformed_not_an_empty_collection()
        {
            // The dangerous decode. A body whose shape we did not understand has no cards in it, and
            // reading that as "owns nothing" would empty a real collection off the back of a server
            // change or a captive-portal login page returning 200.
            var http = new FakeAuthHttp().Reply(200, "{\"cards\":{\"1001\":2}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
        }

        [Test]
        public async Task An_unreadable_body_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200, "<html>captive portal</html>");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
        }

        // ── Unopened packs ──────────────────────────────────────────────────────────────

        [Test]
        public async Task Unopened_packs_come_back_with_the_collection()
        {
            var http = new FakeAuthHttp().Reply(200,
                ListBody(Owned(), "", "{\"pack.pendingham.starter\":5,\"pack.pendingham\":1}"));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.PackCount("pack.pendingham.starter"), Is.EqualTo(5));
            Assert.That(snapshot.PackCount("pack.pendingham"), Is.EqualTo(1));
            Assert.That(snapshot.PackCount("pack.rekindling"), Is.EqualTo(0),
                "a line the player holds nothing of is 0, not a missing key the caller has to handle");
        }

        [Test]
        public async Task A_superuser_still_gets_their_packs()
        {
            // The gift is developed on a superuser account, so one that could not see its own
            // starter packs would make the whole first-run flow untestable on the only device it is
            // built on.
            var http = new FakeAuthHttp().Reply(200,
                AllAtLimitBody("", "{\"pack.pendingham.starter\":5}"));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.Record, Is.SameAs(OwnershipRecord.AllAtLimit));
            Assert.That(snapshot.PackCount("pack.pendingham.starter"), Is.EqualTo(5));
        }

        [Test]
        public async Task A_known_body_with_no_packs_field_is_malformed_not_an_empty_shelf()
        {
            // The contract test the spec asks for, and the same dangerous decode as the missing
            // ownership word one level up. "No packs field" and "this player has opened everything"
            // arrive as the same absence and route the first run differently: one is a proxy or a
            // server change, the other is a finished gift. Silently reading the first as the second
            // is how five granted packs go missing with nothing reporting a fault.
            string digest = OwnershipRecord.List(Owned((1001, 2))).Digest;
            var http = new FakeAuthHttp().Reply(200,
                "{\"ownership\":\"known\",\"rule\":\"list\",\"digest\":\"" + digest +
                "\",\"cardsUnchanged\":false,\"cards\":{\"1001\":2}}");

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceCache, Is.False);
        }

        [Test]
        public async Task An_empty_packs_object_is_a_real_answer()
        {
            // The other half of the pair: present and empty means the shelf is genuinely bare, which
            // is what every launch after the gift looks like.
            var http = new FakeAuthHttp().Reply(200, ListBody(Owned((1001, 2))));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.Packs, Is.Empty);
        }

        [Test]
        public async Task A_pack_row_that_does_not_parse_costs_only_that_row()
        {
            var http = new FakeAuthHttp().Reply(200,
                ListBody(Owned(), "", "{\"pack.pendingham.starter\":3,\"pack.broken\":\"x\",\"pack.spent\":0}"));

            CollectionSnapshot snapshot = await Gateway(http).FetchAsync("jwt", null);

            Assert.That(snapshot.Status, Is.EqualTo(CollectionSyncStatus.Synced));
            Assert.That(snapshot.PackCount("pack.pendingham.starter"), Is.EqualTo(3));
            Assert.That(snapshot.Packs.Count, Is.EqualTo(1));
        }
    }
}
