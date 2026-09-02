using System.Linq;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Collection;
using Emberfall.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Collection
{
    /// <summary>
    /// The roster read-down and write-up, client side (spec
    /// <c>docs/dev-docs/specs/login-handshake.md</c>).
    ///
    /// <para><b>What every read test here guards is one distinction:</b> a roster that came back
    /// EMPTY and a roster that did not come back. They arrive as the same absence of characters,
    /// they are opposite statements about the player, and only one of them may replace what is on
    /// the device. Treat a failed read as "no characters" and a returning player is walked through
    /// character creation a second time while their real roster is overwritten with nothing.
    /// <see cref="RosterSnapshot.ShouldReplaceLocal"/> is the single place that call is made.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpRosterGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        private static HttpRosterGateway Gateway(FakeAuthHttp http)
            => new HttpRosterGateway(Base, http);

        private static Character NewCharacter(string id = "c1")
            => new Character(id, "Ashe", CardClass.EMBERWARD, CardClass.TIDECALLER);

        private static DeckList NewDeck(string id = "d1", string characterId = "c1")
        {
            var deck = new DeckList { Id = id, CharacterId = characterId, Name = "Aggro" };
            deck.Entries.Add(new DeckEntry(1001, 2));
            return deck;
        }

        /// <summary>A <c>known</c> body in exactly the shape the server emits: the two rosters are
        /// JSON documents carried as STRING members, because the wire shape and the storage shape are
        /// the same <c>CollectionJson</c> text on both sides.</summary>
        private static string KnownBody(Character[] characters, DeckList[] decks)
        {
            string charactersJson = CollectionJson.WriteCharacters(characters);
            string decksJson = CollectionJson.WriteDecks(decks);
            return "{\"status\":\"known\"," +
                   "\"characters\":" + Quote(charactersJson) + "," +
                   "\"decks\":" + Quote(decksJson) + "}";
        }

        private static string Quote(string raw)
            => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"")
                         .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        // ── The request ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_fetch_is_a_get_carrying_the_bearer()
        {
            var http = new FakeAuthHttp().Reply(200, KnownBody(new Character[0], new DeckList[0]));

            await Gateway(http).FetchAsync("jwt-token");

            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/roster"));
            Assert.That(http.Requests[0].Verb, Is.EqualTo("GET"));
            Assert.That(http.Requests[0].Headers.Any(
                h => h.Key == "Authorization" && h.Value == "Bearer jwt-token"), Is.True);
        }

        [Test]
        public async Task A_signed_out_device_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Unauthenticated));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False);
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_unconfigured_endpoint_is_unavailable_rather_than_a_failed_request()
        {
            var http = new FakeAuthHttp();

            RosterSnapshot snapshot = await new HttpRosterGateway("", http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Unavailable));
            Assert.That(http.Requests, Is.Empty);
        }

        // ── The distinction the whole handshake exists for ──────────────────────────────

        [Test]
        public async Task A_known_roster_round_trips_its_characters_and_decks()
        {
            var http = new FakeAuthHttp().Reply(200,
                KnownBody(new[] { NewCharacter() }, new[] { NewDeck() }));

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Known));
            Assert.That(snapshot.ShouldReplaceLocal, Is.True);
            Assert.That(snapshot.Characters!.Count, Is.EqualTo(1));
            Assert.That(snapshot.Characters[0].Id, Is.EqualTo("c1"));
            Assert.That(snapshot.Characters[0].PrimaryClass, Is.EqualTo(CardClass.EMBERWARD));
            Assert.That(snapshot.Decks!.Count, Is.EqualTo(1));
            Assert.That(snapshot.Decks[0].CountOf(1001), Is.EqualTo(2));
        }

        [Test]
        public async Task A_genuinely_empty_roster_is_known_and_DOES_replace_the_local_one()
        {
            var http = new FakeAuthHttp().Reply(200, KnownBody(new Character[0], new DeckList[0]));

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Known));
            Assert.That(snapshot.Characters, Is.Empty);
            Assert.That(snapshot.ShouldReplaceLocal, Is.True,
                "a real empty is an ANSWER — a player who deleted their last character on another " +
                "device must not have it restored by this one");
        }

        [Test]
        public async Task A_transport_failure_is_offline_and_never_replaces_anything()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Offline));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False);
            Assert.That(snapshot.Characters, Is.Null, "null is 'unknown', never 'none'");
        }

        [Test]
        public async Task A_401_is_unauthenticated()
        {
            var http = new FakeAuthHttp().Reply(401, "{\"status\":\"unauthenticated\"}");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Unauthenticated));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False);
        }

        [Test]
        public async Task A_503_is_unavailable()
        {
            var http = new FakeAuthHttp().Reply(503, "{\"status\":\"unavailable\"}");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Unavailable));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False);
        }

        // The server omits the roster fields on anything but "known" precisely so an absent field
        // cannot be misread as an empty one. This is the same failure arriving with a 200 on it: a
        // body that CLAIMS known and carries nothing must not decode as "this player has nothing".
        [Test]
        public async Task A_known_body_with_no_roster_fields_is_malformed_not_empty()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"known\"}");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False);
        }

        [Test]
        public async Task A_200_with_no_status_word_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"characters\":\"[]\",\"decks\":\"[]\"}");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Malformed),
                "the status word is the authority — never the presence of the fields");
        }

        [Test]
        public async Task A_known_body_whose_roster_will_not_parse_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"known\",\"characters\":\"not json at all\",\"decks\":\"[]\"}");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Malformed));
            Assert.That(snapshot.ShouldReplaceLocal, Is.False,
                "half a roster written over the local one destroys the half that did not arrive");
        }

        [Test]
        public async Task An_unreadable_body_is_malformed()
        {
            var http = new FakeAuthHttp().Reply(200, "<html>gateway timeout</html>");

            RosterSnapshot snapshot = await Gateway(http).FetchAsync("jwt-token");

            Assert.That(snapshot.Status, Is.EqualTo(RosterSyncStatus.Malformed));
        }

        // ── Writes ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Creating_a_character_posts_the_pairing_by_name()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"created\"}");

            RosterWriteStatus status = await Gateway(http)
                .CreateCharacterAsync("jwt-token", NewCharacter());

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Ok));
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/characters"));
            Assert.That(http.Requests[0].Verb, Is.EqualTo("POST"));
            Assert.That(http.Requests[0].Body, Does.Contain("\"characterId\":\"c1\""));
            Assert.That(http.Requests[0].Body, Does.Contain("\"primaryClass\":\"EMBERWARD\""));
            Assert.That(http.Requests[0].Body, Does.Contain("\"secondaryClass\":\"TIDECALLER\""));
        }

        [Test]
        public async Task Saving_a_deck_puts_it_at_its_own_id()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"saved\"}");

            RosterWriteStatus status = await Gateway(http).SaveDeckAsync("jwt-token", NewDeck());

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Ok));
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/decks/d1"));
            Assert.That(http.Requests[0].Verb, Is.EqualTo("PUT"));
            // The body is CollectionJson's own text, so the wire shape and the storage shape stay
            // one thing — the server reads it with ReadDecks.
            Assert.That(CollectionJson.ReadDecks(http.Requests[0].Body!)[0].CountOf(1001),
                        Is.EqualTo(2));
        }

        [Test]
        public async Task An_illegal_deck_is_still_sent()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"saved\"}");
            var draft = new DeckList { Id = "d-draft", CharacterId = "c1", Name = "Work in progress" };
            draft.Entries.Add(new DeckEntry(1001, 1));   // nowhere near a legal 30

            RosterWriteStatus status = await Gateway(http).SaveDeckAsync("jwt-token", draft);

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Ok));
            Assert.That(http.Requests.Count, Is.EqualTo(1),
                "a client that withheld a deck it judged illegal would silently stop backing up " +
                "the player's work in progress — validation is a QUEUE-time concern");
        }

        [Test]
        public async Task A_deck_id_is_url_escaped()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"deleted\"}");

            await Gateway(http).DeleteDeckAsync("jwt-token", "deck/../characters/c1");

            Assert.That(http.Requests[0].Url,
                Is.EqualTo("https://emberfall.fly.dev/decks/deck%2F..%2Fcharacters%2Fc1"),
                "ids are client-minted strings; an unescaped one could address a different route");
        }

        [Test]
        public async Task Deleting_a_deck_is_a_delete()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"deleted\"}");

            RosterWriteStatus status = await Gateway(http).DeleteDeckAsync("jwt-token", "d1");

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Ok));
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/decks/d1"));
            Assert.That(http.Requests[0].Verb, Is.EqualTo("DELETE"),
                "a GET to the same URL would record identically and read the deck instead");
        }

        [Test]
        public async Task Deleting_a_character_is_a_delete_and_does_not_also_delete_its_decks()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"deleted\"}");

            RosterWriteStatus status = await Gateway(http).DeleteCharacterAsync("jwt-token", "c1");

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Ok));
            Assert.That(http.Requests.Count, Is.EqualTo(1),
                "the server cascades to the character's decks in one atomic call");
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/characters/c1"));
            Assert.That(http.Requests[0].Verb, Is.EqualTo("DELETE"));
        }

        [Test]
        public async Task A_404_on_a_delete_is_NotFound_rather_than_a_failure()
        {
            var http = new FakeAuthHttp().Reply(404, "{\"status\":\"notfound\"}");

            RosterWriteStatus status = await Gateway(http).DeleteDeckAsync("jwt-token", "d1");

            Assert.That(status, Is.EqualTo(RosterWriteStatus.NotFound),
                "the caller already deleted the local copy — the two records now agree");
        }

        [TestCase(400, RosterWriteStatus.Rejected)]
        [TestCase(401, RosterWriteStatus.Unauthenticated)]
        [TestCase(409, RosterWriteStatus.Conflict)]
        [TestCase(503, RosterWriteStatus.Unavailable)]
        [TestCase(500, RosterWriteStatus.Unavailable)]
        public async Task A_write_maps_its_status_code(int code, RosterWriteStatus expected)
        {
            var http = new FakeAuthHttp().Reply(code, "{}");

            RosterWriteStatus status = await Gateway(http).SaveDeckAsync("jwt-token", NewDeck());

            Assert.That(status, Is.EqualTo(expected));
        }

        [Test]
        public async Task A_write_that_did_not_reach_the_server_is_offline()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            RosterWriteStatus status = await Gateway(http).SaveDeckAsync("jwt-token", NewDeck());

            Assert.That(status, Is.EqualTo(RosterWriteStatus.Offline),
                "the local write stands; the server does not know about it yet");
        }

        [Test]
        public async Task A_signed_out_write_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            Assert.That(await Gateway(http).SaveDeckAsync("", NewDeck()),
                        Is.EqualTo(RosterWriteStatus.Unauthenticated));
            Assert.That(await Gateway(http).CreateCharacterAsync("", NewCharacter()),
                        Is.EqualTo(RosterWriteStatus.Unauthenticated));
            Assert.That(await Gateway(http).DeleteDeckAsync("", "d1"),
                        Is.EqualTo(RosterWriteStatus.Unauthenticated));
            Assert.That(await Gateway(http).DeleteCharacterAsync("", "c1"),
                        Is.EqualTo(RosterWriteStatus.Unauthenticated));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_empty_id_is_rejected_before_the_network()
        {
            var http = new FakeAuthHttp();

            Assert.That(await Gateway(http).DeleteDeckAsync("jwt-token", "  "),
                        Is.EqualTo(RosterWriteStatus.Rejected));
            Assert.That(await Gateway(http).DeleteCharacterAsync("jwt-token", ""),
                        Is.EqualTo(RosterWriteStatus.Rejected));
            Assert.That(await Gateway(http).SaveDeckAsync("jwt-token", new DeckList { Id = "" }),
                        Is.EqualTo(RosterWriteStatus.Rejected));
            Assert.That(http.Requests, Is.Empty,
                "a row keyed on \"\" is a row nothing can address again — the server refuses it too, " +
                "but there is no reason to ask");
        }
    }
}
