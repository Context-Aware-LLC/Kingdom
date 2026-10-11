using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Accounts;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Social
{
    /// <summary>
    /// The friends-store contract (#409), run against BOTH implementations. The store reports what
    /// actually happened — <c>Refused</c> is distinct from <c>Sent</c> here — and collapsing those
    /// for the wire is <c>FriendsEndpoint</c>'s job, tested separately.
    /// </summary>
    [Category("Contract")]
    public abstract class FriendStoreContractTests
    {
        protected const string A = "player-a", B = "player-b", C = "player-c";
        protected static readonly FriendLimits Limits = new FriendLimits();

        protected IFriendStore Store { get; private set; } = null!;

        /// <param name="mint">Null for the real random source.</param>
        protected abstract Task<IFriendStore> CreateAsync(Func<string>? mint);

        [SetUp]
        public async Task CreateStore() => Store = await CreateAsync(null);

        private async Task KnownAsync(params string[] players)
        {
            foreach (string player in players) await Store.EnsureIdentityAsync(player);
        }

        private async Task FriendsAsync(string a, string b)
        {
            Assert.That(await Store.RequestAsync(a, b, Limits), Is.EqualTo(FriendRequestOutcome.Sent));
            Assert.That(await Store.AcceptAsync(b, a, Limits), Is.EqualTo(FriendAcceptOutcome.Accepted));
        }

        private static string[] Ids(IReadOnlyList<FriendIdentity> entries)
            => entries.Select(e => e.PlayerId).ToArray();

        // ── codes ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Code_Is_Minted_Once_And_Finds_Its_Holder()
        {
            FriendIdentity first = await Store.EnsureIdentityAsync(A);
            FriendIdentity again = await Store.EnsureIdentityAsync(A);

            Assert.That(FriendCode.TryNormalize(first.Code, out string canonical), Is.True);
            Assert.That(canonical, Is.EqualTo(first.Code), "the stored form is the canonical one");
            Assert.That(again.Code, Is.EqualTo(first.Code),
                "a code a player has already handed out must never change underneath them");
            Assert.That(await Store.FindPlayerIdByCodeAsync(first.Code), Is.EqualTo(A));
        }

        [Test]
        public async Task A_Code_Nobody_Holds_Finds_Nobody()
            => Assert.That(await Store.FindPlayerIdByCodeAsync(FriendCode.FromValue(7)), Is.Null);

        [Test]
        public async Task A_Minted_Code_Already_Held_Is_Minted_Again_Not_Shared()
        {
            var codes = new Queue<string>(new[]
            {
                FriendCode.FromValue(1),   // A takes it
                FriendCode.FromValue(1),   // B draws the same one
                FriendCode.FromValue(2),
            });
            Store = await CreateAsync(() => codes.Dequeue());

            FriendIdentity a = await Store.EnsureIdentityAsync(A);
            FriendIdentity b = await Store.EnsureIdentityAsync(B);

            Assert.That(b.Code, Is.Not.EqualTo(a.Code));
            Assert.That(await Store.FindPlayerIdByCodeAsync(a.Code), Is.EqualTo(A),
                "the collision must not hand A's code to B");
        }

        [Test]
        public async Task A_Display_Name_Is_What_A_Friend_Sees()
        {
            await KnownAsync(A, B);
            await Store.SetDisplayNameAsync(A, "Ashe");
            await FriendsAsync(A, B);

            FriendList b = await Store.ReadAsync(B);
            Assert.That(b.Friends.Single().DisplayName, Is.EqualTo("Ashe"));
            Assert.That(b.Self.DisplayName, Is.Null, "B never chose one");
        }

        // ── requests ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Request_Is_Outgoing_For_The_Sender_And_Incoming_For_The_Target()
        {
            await KnownAsync(A, B);

            Assert.That(await Store.RequestAsync(A, B, Limits), Is.EqualTo(FriendRequestOutcome.Sent));

            FriendList a = await Store.ReadAsync(A), b = await Store.ReadAsync(B);
            Assert.That(Ids(a.Outgoing), Is.EqualTo(new[] { B }));
            Assert.That(Ids(b.Incoming), Is.EqualTo(new[] { A }));
            Assert.That(a.Friends, Is.Empty, "a request is not a friendship until it is answered");
            Assert.That(b.Friends, Is.Empty);
        }

        [Test]
        public async Task Asking_Twice_Leaves_One_Request()
        {
            await KnownAsync(A, B);
            await Store.RequestAsync(A, B, Limits);

            Assert.That(await Store.RequestAsync(A, B, Limits), Is.EqualTo(FriendRequestOutcome.AlreadyPending));
            Assert.That((await Store.ReadAsync(B)).Incoming.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Asking_Someone_Who_Already_Asked_You_Makes_You_Friends()
        {
            await KnownAsync(A, B);
            await Store.RequestAsync(A, B, Limits);

            Assert.That(await Store.RequestAsync(B, A, Limits), Is.EqualTo(FriendRequestOutcome.Accepted));

            Assert.That(Ids((await Store.ReadAsync(A)).Friends), Is.EqualTo(new[] { B }));
            Assert.That(Ids((await Store.ReadAsync(B)).Friends), Is.EqualTo(new[] { A }));
            Assert.That((await Store.ReadAsync(B)).Incoming, Is.Empty);
        }

        [Test]
        public async Task A_Request_To_Yourself_Or_To_Someone_With_No_Code_Is_Refused()
        {
            await KnownAsync(A);

            Assert.That(await Store.RequestAsync(A, A, Limits), Is.EqualTo(FriendRequestOutcome.Refused));
            Assert.That(await Store.RequestAsync(A, "never-seen", Limits), Is.EqualTo(FriendRequestOutcome.Refused));
            Assert.That((await Store.ReadAsync(A)).Outgoing, Is.Empty);
        }

        // ── accept / decline / remove ───────────────────────────────────────────────────

        [Test]
        public async Task Accept_Makes_Both_Sides_Friends_And_Is_Idempotent()
        {
            await KnownAsync(A, B);
            await FriendsAsync(A, B);

            Assert.That(await Store.AcceptAsync(B, A, Limits), Is.EqualTo(FriendAcceptOutcome.Accepted),
                "a retried accept must not read as 'no such request' — the Postgres store re-runs " +
                "a whole unit after a dead pooled socket");
            Assert.That(Ids((await Store.ReadAsync(A)).Friends), Is.EqualTo(new[] { B }));
            Assert.That(Ids((await Store.ReadAsync(B)).Friends), Is.EqualTo(new[] { A }));
            Assert.That((await Store.ReadAsync(A)).Outgoing, Is.Empty);
        }

        [Test]
        public async Task Only_The_Target_Can_Accept()
        {
            await KnownAsync(A, B, C);
            await Store.RequestAsync(A, B, Limits);

            Assert.That(await Store.AcceptAsync(A, B, Limits), Is.EqualTo(FriendAcceptOutcome.NotFound),
                "the sender accepting their own request would make a friendship nobody agreed to");
            Assert.That(await Store.AcceptAsync(C, A, Limits), Is.EqualTo(FriendAcceptOutcome.NotFound));
            Assert.That((await Store.ReadAsync(A)).Friends, Is.Empty);
        }

        [Test]
        public async Task Decline_Drops_A_Request_From_Either_Side_And_Never_A_Friendship()
        {
            await KnownAsync(A, B, C);
            await Store.RequestAsync(A, B, Limits);
            await Store.RequestAsync(A, C, Limits);

            Assert.That(await Store.DeclineAsync(B, A), Is.True, "the target declines");
            Assert.That(await Store.DeclineAsync(A, C), Is.True, "the sender withdraws");
            Assert.That(await Store.DeclineAsync(A, C), Is.False);
            Assert.That((await Store.ReadAsync(A)).Outgoing, Is.Empty);

            await FriendsAsync(A, B);
            Assert.That(await Store.DeclineAsync(A, B), Is.False);
            Assert.That((await Store.ReadAsync(A)).Friends.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Remove_Ends_A_Friendship_For_Both_And_Never_A_Request()
        {
            await KnownAsync(A, B, C);
            await FriendsAsync(A, B);
            await Store.RequestAsync(A, C, Limits);

            Assert.That(await Store.RemoveAsync(A, C), Is.False);
            Assert.That(await Store.RemoveAsync(B, A), Is.True);
            Assert.That((await Store.ReadAsync(A)).Friends, Is.Empty);
            Assert.That((await Store.ReadAsync(B)).Friends, Is.Empty);
            Assert.That((await Store.ReadAsync(A)).Outgoing.Count, Is.EqualTo(1));
        }

        // ── blocks ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Block_Ends_The_Friendship_And_Refuses_Requests_Both_Ways()
        {
            await KnownAsync(A, B);
            await FriendsAsync(A, B);

            Assert.That(await Store.BlockAsync(A, B, Limits), Is.EqualTo(FriendBlockOutcome.Blocked));

            Assert.That((await Store.ReadAsync(B)).Friends, Is.Empty);
            Assert.That(Ids((await Store.ReadAsync(A)).Blocked), Is.EqualTo(new[] { B }));
            Assert.That((await Store.ReadAsync(B)).Blocked, Is.Empty, "B is not told, and blocked nobody");
            Assert.That(await Store.RequestAsync(B, A, Limits), Is.EqualTo(FriendRequestOutcome.Refused));
            Assert.That(await Store.RequestAsync(A, B, Limits), Is.EqualTo(FriendRequestOutcome.Refused));
        }

        [Test]
        public async Task Lifting_My_Block_Does_Not_Lift_Theirs()
        {
            await KnownAsync(A, B);
            await Store.BlockAsync(A, B, Limits);
            await Store.BlockAsync(B, A, Limits);

            Assert.That(await Store.UnblockAsync(A, B), Is.True);

            Assert.That(await Store.RequestAsync(A, B, Limits), Is.EqualTo(FriendRequestOutcome.Refused),
                "B's block is B's own row — this is why a block is not a friendship status");
            Assert.That(await Store.UnblockAsync(B, A), Is.True);
            Assert.That(await Store.RequestAsync(A, B, Limits), Is.EqualTo(FriendRequestOutcome.Sent));
        }

        // ── caps ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_Senders_Own_Caps_Answer_AtCapacity()
        {
            await KnownAsync(A, B, C);
            var oneOutgoing = Limits with { MaxPendingOutgoing = 1 };
            var oneFriend = Limits with { MaxFriends = 1 };

            await Store.RequestAsync(A, B, oneOutgoing);
            Assert.That(await Store.RequestAsync(A, C, oneOutgoing), Is.EqualTo(FriendRequestOutcome.AtCapacity));

            await Store.AcceptAsync(B, A, oneFriend);
            Assert.That(await Store.RequestAsync(A, C, oneFriend), Is.EqualTo(FriendRequestOutcome.AtCapacity));
            Assert.That((await Store.ReadAsync(C)).Incoming, Is.Empty);
        }

        [Test]
        public async Task The_Targets_Caps_Are_Never_Told_To_The_Sender()
        {
            await KnownAsync(A, B, C);
            var oneIncoming = Limits with { MaxPendingIncoming = 1 };

            await Store.RequestAsync(A, C, oneIncoming);

            Assert.That(await Store.RequestAsync(B, C, oneIncoming), Is.EqualTo(FriendRequestOutcome.Refused),
                "a full inbox answering AtCapacity would tell B that C exists and is popular");
            Assert.That((await Store.ReadAsync(C)).Incoming.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Accepting_Past_Either_Sides_Friend_Cap_Is_AtCapacity()
        {
            await KnownAsync(A, B, C);
            var oneFriend = Limits with { MaxFriends = 1 };
            await Store.RequestAsync(A, C, Limits);
            await FriendsAsync(A, B);

            Assert.That(await Store.AcceptAsync(C, A, oneFriend), Is.EqualTo(FriendAcceptOutcome.AtCapacity));
            Assert.That((await Store.ReadAsync(C)).Friends, Is.Empty);
        }

        [Test]
        public async Task The_Block_List_Is_Capped_And_Reblocking_Is_Not_A_New_Block()
        {
            await KnownAsync(A, B, C);
            var oneBlock = Limits with { MaxBlocked = 1 };

            Assert.That(await Store.BlockAsync(A, B, oneBlock), Is.EqualTo(FriendBlockOutcome.Blocked));
            Assert.That(await Store.BlockAsync(A, B, oneBlock), Is.EqualTo(FriendBlockOutcome.Blocked));
            Assert.That(await Store.BlockAsync(A, C, oneBlock), Is.EqualTo(FriendBlockOutcome.AtCapacity));
        }
    }

    /// <summary>The in-memory implementation against the shared contract. Always runs.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryFriendStoreContractTests : FriendStoreContractTests
    {
        protected override Task<IFriendStore> CreateAsync(Func<string>? mint)
            => Task.FromResult<IFriendStore>(mint is null
                ? new InMemoryFriendStore(new FakeTime())
                : new InMemoryFriendStore(new FakeTime(), mint));
    }

    /// <summary>The identical suite against Postgres. Gated on <c>EMBERFALL_LEDGER_DB</c> (see
    /// <see cref="PostgresProbe"/>); truncates before each test for a clean store.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresFriendStoreContractTests : FriendStoreContractTests
    {
        private string _conn = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            await new PostgresFriendStore(_conn, new FakeTime()).EnsureSchemaAsync();
        }

        protected override async Task<IFriendStore> CreateAsync(Func<string>? mint)
        {
            if (mint is null) await PostgresProbe.TruncateAsync(_conn);
            return mint is null
                ? new PostgresFriendStore(_conn, new FakeTime())
                : new PostgresFriendStore(_conn, new FakeTime(), mint);
        }

        /// <summary>The one thing only this store can get wrong: a cap is a count followed by an
        /// insert, and without the per-player advisory lock every one of these wins.</summary>
        [Test]
        public async Task Concurrent_Requests_Cannot_Race_Past_The_Outgoing_Cap()
        {
            string[] targets = Enumerable.Range(0, 12).Select(i => "target-" + i).ToArray();
            await Store.EnsureIdentityAsync(A);
            foreach (string target in targets) await Store.EnsureIdentityAsync(target);
            var oneOutgoing = Limits with { MaxPendingOutgoing = 1 };

            FriendRequestOutcome[] outcomes = await Task.WhenAll(
                targets.Select(target => Task.Run(() => Store.RequestAsync(A, target, oneOutgoing))));

            Assert.That(outcomes.Count(o => o == FriendRequestOutcome.Sent), Is.EqualTo(1));
            Assert.That(outcomes.Count(o => o == FriendRequestOutcome.AtCapacity), Is.EqualTo(11));
            Assert.That((await Store.ReadAsync(A)).Outgoing.Count, Is.EqualTo(1));
        }
    }
}
