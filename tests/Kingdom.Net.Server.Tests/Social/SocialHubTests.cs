using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Social
{
    /// <summary>
    /// Presence and the challenge handshake (#409), with no sockets. What matters is who is told
    /// what — above all, who is NOT told.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SocialHubTests
    {
        private const string A = "player-a", B = "player-b", C = "player-c";
        private static readonly FriendLimits Limits = new FriendLimits();

        private FakeTime _time = null!;
        private InMemoryFriendStore _store = null!;
        private ChallengeBook _book = null!;
        private HashSet<string> _inMatch = null!;
        private SocialHub _hub = null!;
        private readonly Dictionary<string, FakeConnection> _conns = new Dictionary<string, FakeConnection>();
        private readonly Dictionary<string, string> _codes = new Dictionary<string, string>();

        [SetUp]
        public async Task SetUp()
        {
            _time = new FakeTime();
            _store = new InMemoryFriendStore(_time);
            _book = new ChallengeBook(_time);
            _inMatch = new HashSet<string>();
            _hub = new SocialHub(_store, _book, _inMatch.Contains, new FriendRequestLimiter(_time, max: 3),
                new FriendRequestLimiter(_time, max: 3));
            _conns.Clear();
            _codes.Clear();
            foreach (string player in new[] { A, B, C })
                _codes[player] = (await _store.EnsureIdentityAsync(player)).Code;

            // A and B are friends. C is a stranger to both.
            await _store.RequestAsync(A, B, Limits);
            await _store.AcceptAsync(B, A, Limits);
        }

        private async Task<FakeConnection> ConnectAsync(string player)
        {
            var conn = new FakeConnection();
            _conns[player] = conn;
            await _hub.ConnectAsync(player, conn);
            return conn;
        }

        /// <summary>Everything sent to this player since the last call, decoded.</summary>
        private List<SocialMsg> Drain(string player)
        {
            FakeConnection conn = _conns[player];
            var messages = new List<SocialMsg>();
            foreach (byte[] frame in conn.Frames)
            {
                Assert.That(SocialCodec.TryDecode(frame, out SocialMsg? msg), Is.True, "the hub sent an undecodable frame");
                messages.Add(msg!);
            }
            conn.Frames.Clear();
            return messages;
        }

        private T Single<T>(string player) where T : SocialMsg => Drain(player).OfType<T>().Single();

        // ── presence ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Connecting_Tells_Me_Who_Is_Here_And_Tells_My_Friends_I_Am()
        {
            await ConnectAsync(A);
            PresenceMsg alone = Single<PresenceMsg>(A);
            Assert.That(alone.Full, Is.True);
            Assert.That(alone.Friends, Is.Empty, "a friend not listed is offline");

            await ConnectAsync(B);

            FriendPresence seen = Single<PresenceMsg>(B).Friends.Single();
            Assert.That(seen.Code, Is.EqualTo(_codes[A]));
            Assert.That(seen.State, Is.EqualTo(PresenceState.Online));

            PresenceMsg told = Single<PresenceMsg>(A);
            Assert.That(told.Full, Is.False);
            Assert.That(told.Friends.Single().Code, Is.EqualTo(_codes[B]));
        }

        [Test]
        public async Task A_Stranger_Hears_Nothing_About_Anyone()
        {
            await ConnectAsync(C);
            Drain(C);

            await ConnectAsync(A);
            await ConnectAsync(B);
            _inMatch.Add(A);
            await _hub.MatchRosterChangedAsync(A, B);
            await _hub.DisconnectAsync(A, _conns[A]);

            Assert.That(Drain(C), Is.Empty);
        }

        [Test]
        public async Task A_Blocked_Friend_Stops_Hearing_With_No_Unsubscribe_Step()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            Drain(A);

            await _store.BlockAsync(B, A, Limits);
            await _hub.DisconnectAsync(B, _conns[B]);

            Assert.That(Drain(A), Is.Empty, "B going offline is no longer A's to know");
        }

        [Test]
        public async Task Disconnecting_And_Matches_Are_Announced_To_Friends()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            Drain(A);

            _inMatch.Add(B);
            await _hub.MatchRosterChangedAsync(B, C);
            Assert.That(Single<PresenceMsg>(A).Friends.Single().State, Is.EqualTo(PresenceState.InMatch));

            await _hub.DisconnectAsync(B, _conns[B]);
            Assert.That(Single<PresenceMsg>(A).Friends.Single().State, Is.EqualTo(PresenceState.Offline));
        }

        [Test]
        public async Task A_Match_Seat_That_Never_Opened_The_Social_Surface_Is_Not_Minted_A_Code()
        {
            await _hub.MatchRosterChangedAsync("tokenless-seat", "bot-seat");

            Assert.That((await _store.ReadAsync(A)).Self.Code, Is.EqualTo(_codes[A]));
            Assert.That(await _store.RequestAsync(A, "tokenless-seat", Limits), Is.EqualTo(FriendRequestOutcome.Refused),
                "the store refuses a target with no identity — so this proves none was minted");
        }

        [Test]
        public async Task A_Second_Login_Replaces_The_First_And_The_Old_Socket_Closing_Changes_Nothing()
        {
            FakeConnection first = await ConnectAsync(A);
            await ConnectAsync(B);
            FakeConnection second = await ConnectAsync(A);
            Drain(B);

            Assert.That(first.CloseCode, Is.EqualTo(1008));
            await _hub.DisconnectAsync(A, first);

            Assert.That(Drain(B), Is.Empty, "A is still online on the newer connection");
            Assert.That(_hub.OnlineCount, Is.EqualTo(2));
            Assert.That(second.CloseCode, Is.Null);
        }

        [Test]
        public async Task A_Graph_Change_Nudges_Only_The_Connected_And_Refreshes_Their_List()
        {
            await ConnectAsync(A);
            Drain(A);

            await _hub.GraphChangedAsync(new[] { A, B });

            List<SocialMsg> got = Drain(A);
            Assert.That(got.OfType<FriendsChangedMsg>().Count(), Is.EqualTo(1));
            Assert.That(got.OfType<PresenceMsg>().Single().Full, Is.True);
        }

        // ── challenges ──────────────────────────────────────────────────────────────────

        private async Task<string> OfferAsync()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            Drain(A);
            Drain(B);
            await _hub.HandleAsync(A, new ChallengeSendMsg(FriendCode.Display(_codes[B])));
            return Single<ChallengeSentMsg>(A).ChallengeId;
        }

        [Test]
        public async Task A_Challenge_Reaches_The_Friend_Named_By_Code()
        {
            string id = await OfferAsync();

            ChallengeIncomingMsg incoming = Single<ChallengeIncomingMsg>(B);
            Assert.That(incoming.ChallengeId, Is.EqualTo(id));
            Assert.That(incoming.Code, Is.EqualTo(_codes[A]), "B is told who by code, never by player id");
        }

        [Test]
        public async Task Accepting_Hands_Each_Player_Their_Own_Ticket()
        {
            string id = await OfferAsync();
            Drain(B);

            await _hub.HandleAsync(B, new ChallengeAcceptMsg(id));

            string forA = Single<ChallengeReadyMsg>(A).Ticket, forB = Single<ChallengeReadyMsg>(B).Ticket;
            Assert.That(forA, Is.Not.EqualTo(forB));
            Assert.That(_book.Redeem(forA, B), Is.Null);
            Assert.That(_book.Redeem(forA, A)!.OpponentPlayerId, Is.EqualTo(B));
            Assert.That(_book.Redeem(forB, B)!.OpponentPlayerId, Is.EqualTo(A));
        }

        [Test]
        public async Task Every_Reason_A_Challenge_Cannot_Be_Made_Answers_The_Same()
        {
            await ConnectAsync(A);
            await ConnectAsync(C);
            Drain(A);
            Drain(C);

            // B is a friend but offline; C is online but not a friend; the third is nobody's;
            // the last is not a code at all.
            foreach (string code in new[] { _codes[B], _codes[C], FriendCode.FromValue(5) })
            {
                await _hub.HandleAsync(A, new ChallengeSendMsg(code));
                Assert.That(Drain(A).Single(), Is.EqualTo(new ChallengeRefusedMsg(code)));
            }
            await _hub.HandleAsync(A, new ChallengeSendMsg("<b>not a code</b>"));
            Assert.That(Drain(A).Single(), Is.EqualTo(new ChallengeRefusedMsg("")),
                "a refusal never reflects the peer's raw string back");

            Assert.That(Drain(C), Is.Empty, "a stranger is never prompted");
        }

        [Test]
        public async Task A_Player_In_A_Match_Can_Neither_Challenge_Nor_Be_Challenged()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            Drain(A);
            Drain(B);

            _inMatch.Add(B);
            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));
            await _hub.HandleAsync(B, new ChallengeSendMsg(_codes[A]));

            Assert.That(Drain(A).Single(), Is.InstanceOf<ChallengeRefusedMsg>());
            Assert.That(Drain(B).Single(), Is.InstanceOf<ChallengeRefusedMsg>());
        }

        [Test]
        public async Task Challenges_Are_Rate_Limited_Per_Account()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            for (int i = 0; i < 3; i++)
            {
                await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));
                string id = Drain(A).OfType<ChallengeSentMsg>().Single().ChallengeId;
                await _hub.HandleAsync(A, new ChallengeCloseMsg(id));
            }
            Drain(A);
            Drain(B);

            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));

            Assert.That(Drain(A).Single(), Is.InstanceOf<ChallengeRefusedMsg>());
            Assert.That(Drain(B), Is.Empty, "the fourth offer in the window must not prompt B");
        }

        [Test]
        public async Task Declining_Withdrawing_Expiring_And_Leaving_Each_Tell_Both_Players_Why()
        {
            string id = await OfferAsync();
            Drain(B);
            await _hub.HandleAsync(B, new ChallengeCloseMsg(id));
            Assert.That(Single<ChallengeClosedMsg>(A).Reason, Is.EqualTo(ChallengeCloseReason.Declined));
            Assert.That(Single<ChallengeClosedMsg>(B).Reason, Is.EqualTo(ChallengeCloseReason.Declined));

            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));
            id = Single<ChallengeSentMsg>(A).ChallengeId;
            Drain(B);
            await _hub.HandleAsync(A, new ChallengeCloseMsg(id));
            Assert.That(Single<ChallengeClosedMsg>(B).Reason, Is.EqualTo(ChallengeCloseReason.Cancelled));
            Drain(A);

            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));
            Drain(A);
            Drain(B);
            _time.Advance(_book.OfferTtlMs);
            _hub.SweepChallenges();
            Assert.That(Single<ChallengeClosedMsg>(A).Reason, Is.EqualTo(ChallengeCloseReason.Expired));
            Assert.That(Single<ChallengeClosedMsg>(B).Reason, Is.EqualTo(ChallengeCloseReason.Expired));

            _time.Advance(60_000);   // a fresh rate-limit window
            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));
            Drain(A);
            await _hub.DisconnectAsync(B, _conns[B]);
            Assert.That(Drain(A).OfType<ChallengeClosedMsg>().Single().Reason, Is.EqualTo(ChallengeCloseReason.Left));
        }

        [Test]
        public async Task Acting_On_An_Id_The_Server_Does_Not_Hold_Answers_Gone_To_The_Caller_Alone()
        {
            string id = await OfferAsync();
            await ConnectAsync(C);
            Drain(B);
            Drain(C);

            await _hub.HandleAsync(C, new ChallengeAcceptMsg(id));
            await _hub.HandleAsync(C, new ChallengeCloseMsg(id));
            await _hub.HandleAsync(A, new ChallengeAcceptMsg(id));

            Assert.That(Drain(C).Cast<ChallengeClosedMsg>().Select(m => m.Reason),
                Is.All.EqualTo(ChallengeCloseReason.Gone), "somebody else's challenge id is useless to C");
            Assert.That(Single<ChallengeClosedMsg>(A).Reason, Is.EqualTo(ChallengeCloseReason.Gone),
                "the sender cannot accept their own offer");
            Assert.That(Drain(B), Is.Empty);
            Assert.That(_book.HasLive(A), Is.True, "and the real offer still stands");
        }

        [Test]
        public async Task A_Connection_That_Is_Not_Reading_Is_Closed()
        {
            await ConnectAsync(A);
            FakeConnection b = await ConnectAsync(B);
            b.RefuseEnqueue = true;

            await _hub.HandleAsync(A, new ChallengeSendMsg(_codes[B]));

            Assert.That(b.CloseCode, Is.EqualTo(1008));
        }

        // ── chat (VULN-004) ─────────────────────────────────────────────────────────────

        private async Task BothOnlineAsync()
        {
            await ConnectAsync(A);
            await ConnectAsync(B);
            Drain(A);
            Drain(B);
        }

        [Test]
        public async Task A_Message_Reaches_The_Friend_And_Comes_Back_To_The_Sender_As_Cleaned()
        {
            await BothOnlineAsync();

            await _hub.HandleAsync(A, new ChatSendMsg(FriendCode.Display(_codes[B]), "  gg\n‮wp  "));

            ChatMsg received = Single<ChatMsg>(B);
            Assert.That(received.Code, Is.EqualTo(_codes[A]), "the recipient is told who sent it, by code");
            Assert.That(received.Text, Is.EqualTo("gg wp"), "the server cleans whatever the client sent");
            Assert.That(received.Mine, Is.False);

            ChatMsg echo = Single<ChatMsg>(A);
            Assert.That(echo.Code, Is.EqualTo(_codes[B]));
            Assert.That(echo.Text, Is.EqualTo("gg wp"), "the sender sees exactly what the friend sees");
            Assert.That(echo.Mine, Is.True);
        }

        [Test]
        public async Task A_Stranger_Cannot_Message_And_Is_Told_The_Same_As_For_An_Offline_Friend()
        {
            await ConnectAsync(C);
            await BothOnlineAsync();
            Drain(C);

            await _hub.HandleAsync(C, new ChatSendMsg(_codes[A], "hi"));
            Assert.That(Drain(A), Is.Empty, "a stranger's text never reaches the screen");
            ChatRefusedMsg toStranger = Single<ChatRefusedMsg>(C);

            await _hub.DisconnectAsync(B, _conns[B]);
            Drain(A);
            await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], "hi"));
            ChatRefusedMsg toOffline = Single<ChatRefusedMsg>(A);

            Assert.That(toStranger.GetType(), Is.EqualTo(toOffline.GetType()));
            Assert.That(toStranger.Code, Is.EqualTo(_codes[A]));
            Assert.That(toOffline.Code, Is.EqualTo(_codes[B]));
        }

        [Test]
        public async Task A_Message_To_An_Offline_Friend_Is_Not_Kept_For_Later()
        {
            await ConnectAsync(A);
            Drain(A);

            await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], "are you there"));
            Single<ChatRefusedMsg>(A);

            await ConnectAsync(B);
            Assert.That(Drain(B).OfType<ChatMsg>(), Is.Empty, "chat is ephemeral: nothing is stored");
        }

        [Test]
        public async Task A_Block_Mutes_Both_Ways()
        {
            await BothOnlineAsync();
            await _store.BlockAsync(B, A, Limits);

            await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], "hey"));
            await _hub.HandleAsync(B, new ChatSendMsg(_codes[A], "hey"));

            Assert.That(Drain(A).OfType<ChatMsg>(), Is.Empty);
            Assert.That(Drain(B).OfType<ChatMsg>(), Is.Empty);
        }

        [Test]
        public async Task Unsendable_Text_And_A_Malformed_Code_Are_Refused_Without_Echoing_The_Raw_Code()
        {
            await BothOnlineAsync();

            await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], "​ \n"));
            await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], new string('a', ChatText.MaxLength + 1)));
            await _hub.HandleAsync(A, new ChatSendMsg("<b>not-a-code</b>", "hi"));

            List<SocialMsg> toA = Drain(A);
            Assert.That(toA.OfType<ChatRefusedMsg>().Select(r => r.Code),
                Is.EqualTo(new[] { _codes[B], _codes[B], "" }));
            Assert.That(Drain(B), Is.Empty);
        }

        [Test]
        public async Task Chat_Is_Rate_Limited_Per_Account()
        {
            await BothOnlineAsync();

            for (int i = 0; i < 4; i++) await _hub.HandleAsync(A, new ChatSendMsg(_codes[B], "spam " + i));

            Assert.That(Drain(B).OfType<ChatMsg>().Count(), Is.EqualTo(3), "the fixture's limiter allows three");
            Assert.That(Drain(A).OfType<ChatRefusedMsg>().Count(), Is.EqualTo(1));
        }
    }
}
