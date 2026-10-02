using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Social;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Social
{
    /// <summary>
    /// The <c>/friends</c> routes (#409), below HTTP. What matters here is what a caller is TOLD:
    /// the store's own contract suite covers what happens.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FriendsEndpointTests
    {
        /// <summary>The token IS the subject, so each test names its players by token.</summary>
        internal sealed class SubjectVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(rawToken.StartsWith("bad")
                    ? TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid)
                    // "noemail-…" is the account Sam's ruling exists for: signed in, no email.
                    : TokenVerificationResult.Ok(new VerifiedIdentity(
                        rawToken, rawToken.StartsWith("noemail") ? "" : rawToken + "@example.com", true)));
        }

        private FakeTime _time = null!;
        private InMemoryFriendStore _store = null!;

        private FriendsEndpoint Endpoint(FriendLimits? limits = null, IAccountStore? accounts = null, bool noAccounts = false)
            => new FriendsEndpoint(
                new SubjectVerifier(),
                noAccounts ? null : accounts ?? _accounts,
                _store,
                limits ?? new FriendLimits(),
                new FriendRequestLimiter(_time, max: 10, windowMs: 60_000));

        private InMemoryAccountStore _accounts = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _store = new InMemoryFriendStore(_time);
            _accounts = new InMemoryAccountStore(_time);
        }

        private static async Task<string> CodeOf(FriendsEndpoint endpoint, string token)
            => (await endpoint.ReadAsync(token)).List!.Self.Code;

        // ── auth ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task No_Token_Or_A_Refused_One_Is_Unauthenticated_With_No_List()
        {
            var endpoint = Endpoint();

            foreach (string? token in new[] { null, "", "bad-token" })
            {
                FriendsResult r = await endpoint.ReadAsync(token);
                Assert.That(r.Status, Is.EqualTo(FriendsStatus.Unauthenticated));
                Assert.That(r.List, Is.Null);
            }
        }

        [Test]
        public async Task No_Account_Store_Is_Unavailable_Not_An_Empty_List()
        {
            FriendsResult r = await Endpoint(noAccounts: true).ReadAsync("ann");

            Assert.That(r.Status, Is.EqualTo(FriendsStatus.Unavailable));
            Assert.That(r.HttpStatus, Is.EqualTo(503));
            Assert.That(r.List, Is.Null, "an empty list here would read as 'this player has no friends'");
        }

        // ── the flow ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Two_Accounts_One_With_No_Email_Become_Friends_By_Code()
        {
            var endpoint = Endpoint();
            string annCode = await CodeOf(endpoint, "ann");
            string benCode = await CodeOf(endpoint, "noemail-ben");

            FriendsResult sent = await endpoint.RequestAsync("ann", FriendCode.Display(benCode).ToLowerInvariant());
            Assert.That(sent.Status, Is.EqualTo(FriendsStatus.Sent));
            Assert.That(sent.List!.Outgoing.Single().Code, Is.EqualTo(benCode));

            FriendsResult accepted = await endpoint.AcceptAsync("noemail-ben", annCode);
            Assert.That(accepted.Status, Is.EqualTo(FriendsStatus.Accepted));
            Assert.That(accepted.List!.Friends.Single().Code, Is.EqualTo(annCode));
            Assert.That((await endpoint.ReadAsync("ann")).List!.Friends.Single().Code, Is.EqualTo(benCode));
        }

        [Test]
        public async Task Accepting_A_Code_With_No_Request_Behind_It_Is_NotFound()
        {
            var endpoint = Endpoint();
            string benCode = await CodeOf(endpoint, "ben");

            Assert.That((await endpoint.AcceptAsync("ann", benCode)).Status, Is.EqualTo(FriendsStatus.NotFound));
            Assert.That((await endpoint.AcceptAsync("ann", FriendCode.FromValue(5))).Status,
                Is.EqualTo(FriendsStatus.NotFound));
        }

        [Test]
        public async Task Decline_Remove_And_Unblock_Answer_The_Same_Whether_Or_Not_Anything_Stood()
        {
            var endpoint = Endpoint();
            string benCode = await CodeOf(endpoint, "ben");
            string nobody = FriendCode.FromValue(5);

            foreach (string code in new[] { benCode, nobody })
            {
                Assert.That((await endpoint.DeclineAsync("ann", code)).Status, Is.EqualTo(FriendsStatus.Declined));
                Assert.That((await endpoint.RemoveAsync("ann", code)).Status, Is.EqualTo(FriendsStatus.Removed));
                Assert.That((await endpoint.UnblockAsync("ann", code)).Status, Is.EqualTo(FriendsStatus.Unblocked));
            }
        }

        // ── what a caller can learn about a code ────────────────────────────────────────

        [Test]
        public async Task A_Request_Answers_Sent_Whether_Or_Not_It_Was()
        {
            var endpoint = Endpoint();
            string annCode = await CodeOf(endpoint, "ann");
            string blockerCode = await CodeOf(endpoint, "blocker");
            string friendCode = await CodeOf(endpoint, "friend");
            await endpoint.BlockAsync("blocker", annCode);
            await endpoint.RequestAsync("ann", friendCode);
            await endpoint.AcceptAsync("friend", annCode);

            string[] neverSent =
            {
                FriendCode.FromValue(5),   // nobody holds it
                annCode,                   // her own
                blockerCode,               // its holder blocked her
                friendCode,                // already friends
            };

            foreach (string code in neverSent)
            {
                FriendsResult r = await endpoint.RequestAsync("ann", code);
                Assert.That(r.Status, Is.EqualTo(FriendsStatus.Sent), code);
                Assert.That(r.List!.Outgoing, Is.Empty, "nothing may have been written for " + code);
            }

            Assert.That((await endpoint.ReadAsync("blocker")).List!.Incoming, Is.Empty,
                "a blocked player's request must not arrive");
        }

        [Test]
        public async Task The_Callers_Own_Cap_Is_Answered_Before_The_Code_Is_Looked_Up()
        {
            var endpoint = Endpoint(new FriendLimits(MaxPendingOutgoing: 1));
            await endpoint.RequestAsync("ann", await CodeOf(endpoint, "ben"));

            // If the cap were checked after the lookup, only the held code could answer it —
            // and "at capacity" would be a way to ask whether a code is real.
            Assert.That((await endpoint.RequestAsync("ann", await CodeOf(endpoint, "cy"))).Status,
                Is.EqualTo(FriendsStatus.AtCapacity));
            Assert.That((await endpoint.RequestAsync("ann", FriendCode.FromValue(5))).Status,
                Is.EqualTo(FriendsStatus.AtCapacity));
        }

        [Test]
        public async Task A_Malformed_Code_Is_InvalidCode_On_Every_Verb()
        {
            var endpoint = Endpoint();
            const string typo = "22222-2222Z";

            Assert.That((await endpoint.RequestAsync("ann", typo)).Status, Is.EqualTo(FriendsStatus.InvalidCode));
            Assert.That((await endpoint.AcceptAsync("ann", typo)).Status, Is.EqualTo(FriendsStatus.InvalidCode));
            Assert.That((await endpoint.BlockAsync("ann", null)).Status, Is.EqualTo(FriendsStatus.InvalidCode));
            Assert.That((await endpoint.RequestAsync("ann", typo)).HttpStatus, Is.EqualTo(400));
        }

        [Test]
        public async Task Requests_And_Blocks_Share_One_Rate_Limit_Per_Account()
        {
            var endpoint = Endpoint();
            string unknown = FriendCode.FromValue(5);

            for (int i = 0; i < 5; i++)
            {
                Assert.That((await endpoint.RequestAsync("ann", unknown)).Status, Is.EqualTo(FriendsStatus.Sent));
                Assert.That((await endpoint.BlockAsync("ann", unknown)).Status, Is.EqualTo(FriendsStatus.Blocked));
            }

            Assert.That((await endpoint.RequestAsync("ann", unknown)).Status, Is.EqualTo(FriendsStatus.RateLimited));
            Assert.That((await endpoint.BlockAsync("ann", unknown)).HttpStatus, Is.EqualTo(429));
            Assert.That((await endpoint.RequestAsync("ben", unknown)).Status, Is.EqualTo(FriendsStatus.Sent),
                "the limit is per account");

            _time.Advance(60_000);
            Assert.That((await endpoint.RequestAsync("ann", unknown)).Status, Is.EqualTo(FriendsStatus.Sent));
        }

        // ── names ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Name_Is_Stored_Normalized_And_Refused_By_The_Nameplate_Rules()
        {
            var endpoint = Endpoint();

            FriendsResult named = await endpoint.SetNameAsync("ann", "  Ann   Bell ");
            Assert.That(named.Status, Is.EqualTo(FriendsStatus.Named));
            Assert.That(named.List!.Self.DisplayName, Is.EqualTo("Ann Bell"));

            foreach (string? refused in new[] { null, "ab", "SYSTEM", "Ann‮Bell" })
            {
                FriendsResult r = await endpoint.SetNameAsync("ann", refused);
                Assert.That(r.Status, Is.EqualTo(FriendsStatus.InvalidName), refused ?? "null");
            }

            Assert.That((await endpoint.ReadAsync("ann")).List!.Self.DisplayName, Is.EqualTo("Ann Bell"),
                "a refused name must not overwrite the stored one");
        }
    }
}
