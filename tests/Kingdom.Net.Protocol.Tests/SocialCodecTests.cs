using System.Text;
using NUnit.Framework;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// The <c>/social</c> codec (#409). Both ends run this one implementation, so what these pin is
    /// that every type survives a round trip and that hostile input is a false return, never a throw.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class SocialCodecTests
    {
        private static T RoundTrip<T>(T msg) where T : SocialMsg
        {
            Assert.That(SocialCodec.TryDecode(SocialCodec.Encode(msg), out SocialMsg? decoded), Is.True,
                typeof(T).Name + " must decode");
            Assert.That(decoded, Is.InstanceOf<T>());
            return (T)decoded!;
        }

        [Test]
        public void Every_Scalar_Message_Round_Trips()
        {
            SocialMsg[] all =
            {
                new SocialHelloMsg("a.jwt.token", "wire.v1"),
                new SocialPingMsg(),
                new SocialPongMsg(),
                new FriendsChangedMsg(),
                new ChallengeSendMsg("2222222222"),
                new ChallengeAcceptMsg("abc123"),
                new ChallengeCloseMsg("abc123"),
                new ChallengeIncomingMsg("abc123", "2222222222", 60_000),
                new ChallengeSentMsg("abc123", "2222222222", 60_000),
                new ChallengeRefusedMsg(""),
                new ChallengeReadyMsg("abc123", "ticket-0", 30_000),
                new ChallengeClosedMsg("abc123", ChallengeCloseReason.Declined),
                new ChallengeClosedMsg("abc123", ChallengeCloseReason.Cancelled),
                new ChallengeClosedMsg("abc123", ChallengeCloseReason.Expired),
                new ChallengeClosedMsg("abc123", ChallengeCloseReason.Left),
                new ChallengeClosedMsg("abc123", ChallengeCloseReason.Gone),
                new ChatSendMsg("2222222222", "gg \"wp\" \\ é 🎉"),
                new ChatMsg("2222222222", "rematch?", Mine: false),
                new ChatMsg("2222222222", "sure", Mine: true),
                new ChatRefusedMsg("2222222222"),
            };

            foreach (SocialMsg msg in all)
                Assert.That(RoundTrip(msg), Is.EqualTo(msg), msg.GetType().Name);
        }

        [Test]
        public void Presence_Round_Trips_Its_List_And_Every_State()
        {
            var sent = new PresenceMsg(true, new[]
            {
                new FriendPresence("2222222222", PresenceState.Online),
                new FriendPresence("3333333333", PresenceState.InMatch),
                new FriendPresence("4444444444", PresenceState.Offline),
            });

            PresenceMsg got = RoundTrip(sent);

            Assert.That(got.Full, Is.True);
            Assert.That(got.Friends.Count, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(got.Friends[i].Code, Is.EqualTo(sent.Friends[i].Code));
                Assert.That(got.Friends[i].State, Is.EqualTo(sent.Friends[i].State));
            }

            Assert.That(RoundTrip(new PresenceMsg(false, new FriendPresence[0])).Friends, Is.Empty);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"t\":7}")]
        [TestCase("{\"t\":\"no_such_type\"}")]
        [TestCase("{\"t\":\"hello\",\"authToken\":\"x\"}")]                     // missing version
        [TestCase("{\"t\":\"challenge_send\",\"code\":5}")]                      // wrong type
        [TestCase("{\"t\":\"challenge_closed\",\"id\":\"a\",\"reason\":\"bored\"}")]
        [TestCase("{\"t\":\"presence\",\"full\":true,\"friends\":[{\"code\":\"a\",\"state\":\"away\"}]}")]
        [TestCase("{\"t\":\"presence\",\"full\":true,\"friends\":{}}")]
        public void Malformed_Frames_Are_Refused_Without_Throwing(string json)
        {
            Assert.That(SocialCodec.TryDecode(Encoding.UTF8.GetBytes(json), out SocialMsg? msg), Is.False);
            Assert.That(msg, Is.Null);
        }

        [Test]
        public void A_Frame_Over_The_Size_Cap_Is_Refused_Before_It_Is_Parsed()
        {
            byte[] huge = SocialCodec.Encode(new ChallengeSendMsg(new string('2', SocialCodec.MaxMessageBytes)));

            Assert.That(SocialCodec.TryDecode(huge, out _), Is.False);
        }
    }
}
