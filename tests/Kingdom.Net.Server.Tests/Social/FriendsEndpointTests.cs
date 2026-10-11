using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Social
{
    /// <summary>
    /// The <c>/friends</c> routes (#409), below HTTP. What matters here is what a caller is TOLD:
    /// the store's own contract suite covers what happens.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FriendsEndpointTests
    {
        /// <summary>The token IS the subject, so each test names its players by token. A token
        /// written <c>subject|Name</c> carries that sign-up name, as a real one carries
        /// <c>user_metadata.display_name</c>.</summary>
        internal sealed class SubjectVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
            {
                if (rawToken.StartsWith("bad"))
                    return Task.FromResult(TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));

                string[] parts = rawToken.Split('|');
                string subject = parts[0];
                // "noemail-…" is the account Sam's ruling exists for: signed in, no email.
                return Task.FromResult(TokenVerificationResult.Ok(new VerifiedIdentity(
                    subject, subject.StartsWith("noemail") ? "" : subject + "@example.com", true,
                    parts.Length > 1 ? parts[1] : "")));
            }
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
        public async Task The_Sign_Up_Name_In_The_Token_Is_The_Name_A_Friend_Sees()
        {
            var endpoint = Endpoint();
            string annCode = (await endpoint.ReadAsync("ann|  Ann   Bell ")).List!.Self.Code;
            string benCode = await CodeOf(endpoint, "ben");
            await endpoint.RequestAsync("ann", benCode);

            FriendsResult accepted = await endpoint.AcceptAsync("ben", annCode);

            Assert.That(accepted.List!.Friends.Single().DisplayName, Is.EqualTo("Ann Bell"),
                "stored normalized, and still there on a call whose token carried no name");
            Assert.That(accepted.List.Self.DisplayName, Is.Null, "Ben signed up with none");

            // A rename at sign-up level follows on the next call.
            Assert.That((await endpoint.ReadAsync("ann|Annie")).List!.Self.DisplayName, Is.EqualTo("Annie"));
        }

        [Test]
        public async Task A_Token_Name_The_Nameplate_Rules_Refuse_Is_Skipped_Not_Stored()
        {
            var endpoint = Endpoint();
            await endpoint.ReadAsync("ann|Ann Bell");

            // user_metadata is the player's own to write, so a genuine token can carry any of these.
            foreach (string refused in new[] { "ab", "SYSTEM", "Ann\u202EBell", "<b>Ann</b>" })
            {
                FriendsResult r = await endpoint.ReadAsync("ann|" + refused);
                Assert.That(r.Status, Is.EqualTo(FriendsStatus.Known), "a bad name is not a failed call");
                Assert.That(r.List!.Self.DisplayName, Is.EqualTo("Ann Bell"), refused);
            }
        }
    }
}
