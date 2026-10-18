using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Social
{
    /// <summary>
    /// Find Friends From Play Games (#423). The failures worth a test: an id the client asserts
    /// instead of one Google answered, a blocked player surfacing, and a friend shown twice.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PlayGamesFriendsTests
    {
        /// <summary>Google, as far as these tests need it: an auth code "code-for:ann" opens
        /// Ann's Play Games session, with whatever friends the test gave her.</summary>
        private sealed class FakeGoogle : IPlayGamesApi
        {
            public readonly Dictionary<string, List<string>> Friends = new Dictionary<string, List<string>>();
            public PlayGamesCallStatus FriendsStatus = PlayGamesCallStatus.Ok;

            public Task<PlayGamesSession> OpenAsync(string authCode, bool withFriends, CancellationToken ct = default)
            {
                if (!authCode.StartsWith("code-for:"))
                    return Task.FromResult(new PlayGamesSession(PlayGamesCallStatus.CodeRefused, null, null));
                string who = authCode.Substring("code-for:".Length);
                string id = "g-" + who;
                if (!withFriends) return Task.FromResult(new PlayGamesSession(PlayGamesCallStatus.Ok, id, null));
                if (FriendsStatus != PlayGamesCallStatus.Ok)
                    return Task.FromResult(new PlayGamesSession(FriendsStatus, id, null));
                IReadOnlyList<string> friends = Friends.TryGetValue(who, out var list) ? list : new List<string>();
                return Task.FromResult(new PlayGamesSession(PlayGamesCallStatus.Ok, id, friends));
            }
        }

        private FakeTime _time = null!;
        private InMemoryFriendStore _store = null!;
        private InMemoryAccountStore _accounts = null!;
        private InMemoryPlayGamesLinkStore _links = null!;
        private FakeGoogle _google = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _store = new InMemoryFriendStore(_time);
            _accounts = new InMemoryAccountStore(_time);
            _links = new InMemoryPlayGamesLinkStore();
            _google = new FakeGoogle();
        }

        private FriendsEndpoint Endpoint(bool withGoogle = true)
            => new FriendsEndpoint(
                new FriendsEndpointTests.SubjectVerifier(),
                _accounts,
                _store,
                new FriendLimits(),
                new FriendRequestLimiter(_time, max: 10, windowMs: 60_000),
                hub: null,
                withGoogle ? _google : null,
                _links,
                new FriendRequestLimiter(_time, max: 100, windowMs: 60_000));

        private async Task LinkAsync(FriendsEndpoint endpoint, string who)
        {
            PlayGamesFriendsResult r = await endpoint.LinkPlayGamesAsync(who + "|" + Cap(who), "code-for:" + who);
            Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.Linked));
        }

        private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s.Substring(1);

        [Test]
        public async Task A_Linked_Play_Games_Friend_Is_Suggested_By_Code_And_Name()
        {
            var endpoint = Endpoint();
            await LinkAsync(endpoint, "bob");
            _google.Friends["ann"] = new List<string> { "g-bob", "g-nobody" };

            PlayGamesFriendsResult r = await endpoint.FindPlayGamesFriendsAsync("ann|Ann", "code-for:ann");

            string bobCode = (await endpoint.ReadAsync("bob|Bob")).List!.Self.Code;
            Assert.Multiple(() =>
            {
                Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.Known));
                Assert.That(r.Suggestions, Has.Count.EqualTo(1), "a Play Games friend who never linked is not shown");
                Assert.That(r.Suggestions![0].Code, Is.EqualTo(bobCode));
                Assert.That(r.Suggestions[0].DisplayName, Is.EqualTo("Bob"));
                Assert.That(r.Suggestions[0].PlayGamesId, Is.EqualTo("g-bob"));
                Assert.That(r.Suggestions[0].Relation, Is.EqualTo("none"));
            });
        }

        [Test]
        public async Task Finding_Links_The_Caller_Too()
        {
            // Ann looking for friends is also Ann becoming findable.
            var endpoint = Endpoint();
            await endpoint.FindPlayGamesFriendsAsync("ann|Ann", "code-for:ann");
            _google.Friends["bob"] = new List<string> { "g-ann" };

            PlayGamesFriendsResult r = await endpoint.FindPlayGamesFriendsAsync("bob|Bob", "code-for:bob");

            Assert.That(r.Suggestions!.Select(s => s.DisplayName), Is.EqualTo(new[] { "Ann" }));
        }

        [Test]
        public async Task Friends_And_Blocks_Either_Way_Are_Left_Out_And_Requests_Are_Marked()
        {
            var endpoint = Endpoint();
            foreach (string who in new[] { "bob", "cat", "dan", "eve", "fay" }) await LinkAsync(endpoint, who);
            async Task<string> Code(string who) => (await endpoint.ReadAsync(who + "|" + Cap(who))).List!.Self.Code;

            // Bob is already a friend; Ann blocked Cat; Dan blocked Ann; Ann asked Eve; Fay asked Ann.
            await endpoint.RequestAsync("ann|Ann", await Code("bob"));
            await endpoint.AcceptAsync("bob|Bob", await Code("ann"));
            await endpoint.BlockAsync("ann|Ann", await Code("cat"));
            await endpoint.BlockAsync("dan|Dan", await Code("ann"));
            await endpoint.RequestAsync("ann|Ann", await Code("eve"));
            await endpoint.RequestAsync("fay|Fay", await Code("ann"));
            _google.Friends["ann"] = new List<string> { "g-bob", "g-cat", "g-dan", "g-eve", "g-fay", "g-ann" };

            PlayGamesFriendsResult r = await endpoint.FindPlayGamesFriendsAsync("ann|Ann", "code-for:ann");

            Assert.That(r.Suggestions!.Select(s => (s.DisplayName, s.Relation)),
                Is.EqualTo(new[] { ("Eve", "outgoing"), ("Fay", "incoming") }));
        }

        [Test]
        public async Task A_Refused_Or_Missing_Code_Is_Invalid()
        {
            var endpoint = Endpoint();

            foreach (string? code in new[] { null, "", "forged" })
            {
                PlayGamesFriendsResult r = await endpoint.FindPlayGamesFriendsAsync("ann|Ann", code);
                Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.InvalidCode));
                Assert.That(r.Suggestions, Is.Null);
            }
        }

        [Test]
        public async Task Friends_Not_Shared_Is_Its_Own_Answer()
        {
            _google.FriendsStatus = PlayGamesCallStatus.FriendsHidden;

            PlayGamesFriendsResult r = await Endpoint().FindPlayGamesFriendsAsync("ann|Ann", "code-for:ann");

            Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.FriendsHidden));
            Assert.That(r.HttpStatus, Is.EqualTo(403));
        }

        [Test]
        public async Task Without_The_Game_Server_Credential_The_Routes_Are_Unavailable()
        {
            PlayGamesFriendsResult r = await Endpoint(withGoogle: false).LinkPlayGamesAsync("ann|Ann", "code-for:ann");

            Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.Unavailable));
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task No_Token_Is_Unauthenticated()
        {
            PlayGamesFriendsResult r = await Endpoint().FindPlayGamesFriendsAsync(null, "code-for:ann");

            Assert.That(r.Status, Is.EqualTo(PlayGamesFriendsStatus.Unauthenticated));
        }

        [Test]
        public async Task A_Second_Account_Linking_The_Same_Google_Account_Takes_It_Over()
        {
            var store = new InMemoryPlayGamesLinkStore();
            await store.LinkAsync("player-1", "g-1");
            await store.LinkAsync("player-2", "g-1");

            IReadOnlyDictionary<string, string> found = await store.FindAsync(new[] { "g-1" });

            Assert.That(found["g-1"], Is.EqualTo("player-2"));
        }
    }
}
