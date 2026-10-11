using NUnit.Framework;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// C-G1-8 step 1 — the connect handshake that carries guest identity (telemetry-spec §0:
    /// "identity = guest device token", accounts posture decision 6a). Before this, the socket
    /// connect WAS the hello and the server minted `guest-&lt;counter&gt;` from a per-process
    /// counter — so identity reset on every restart and no emitted row could be attributed.
    ///
    /// The hello is also the first client→server carrier for the client's contract version, which
    /// the server previously never learned (`WireContractVersion` travels only server→client on
    /// <see cref="MatchStartMsg"/>). Carried now, enforced separately.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class HelloIdentityTests
    {
        private static readonly MatchId M = new MatchId("m-hello-test");

        [Test]
        public void Hello_RoundTrips_All_Fields()
        {
            var sent = new HelloMsg(
                DeviceToken: "8f14e45fce8f4b6ea1c2d3e4f5a6b7c8",
                ClientContractVersion: ProtocolContract.Version,
                Platform: "windows",
                BuildId: "m2-alpha-7",
                DisplayName: "Sam Ember",
                AuthToken: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.stub");

            var res = ProtocolCodec.Decode(ProtocolCodec.Encode(sent, M, 0));

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            Assert.That(res.Message, Is.InstanceOf<HelloMsg>());
            Assert.That((HelloMsg)res.Message!, Is.EqualTo(sent));
        }

        [Test]
        public void Hello_AuthToken_RoundTrips_When_Present()
        {
            // EF-074b — a signed-in player sends a Supabase JWT; the codec carries it unchanged.
            var sent = new HelloMsg(
                DeviceToken: "device-abc",
                AuthToken: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.sig");

            var res = ProtocolCodec.Decode(ProtocolCodec.Encode(sent, M, 1));

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            var hello = (HelloMsg)res.Message!;
            Assert.That(hello.AuthToken, Is.EqualTo(sent.AuthToken));
        }

        [Test]
        public void Hello_Without_AuthToken_Decodes_As_Guest()
        {
            // EF-074b binding constraint 1: absent authToken → empty string → guest path.
            // A hello from a client that predates EF-074b must decode cleanly.
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{\"deviceToken\":\"abc\"}}");

            var res = ProtocolCodec.Decode(bytes);

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            Assert.That(((HelloMsg)res.Message!).AuthToken, Is.Empty,
                "absent authToken must decode as empty string, not a failure");
        }

        [Test]
        public void Hello_Without_A_DeviceToken_Is_Refused()
        {
            // An identity-less hello must not decode into a valid message — that is the hole the
            // whole condition exists to close, and it has to fail at the codec, not downstream.
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{" +
                "\"clientContractVersion\":\"" + ProtocolContract.Version + "\",\"platform\":\"windows\",\"buildId\":\"b\"}}");

            var res = ProtocolCodec.Decode(bytes);

            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.BadField));
            Assert.That(res.Detail, Does.Contain("deviceToken"));
        }

        [Test]
        public void Hello_With_An_Empty_DeviceToken_Is_Refused()
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{" +
                "\"deviceToken\":\"\",\"clientContractVersion\":\"" + ProtocolContract.Version + "\"," +
                "\"platform\":\"windows\",\"buildId\":\"b\"}}");

            var res = ProtocolCodec.Decode(bytes);

            Assert.That(res.Ok, Is.False, "an empty token is an absent token wearing a costume");
            Assert.That(res.Error, Is.EqualTo(ProtocolError.BadField));
        }

        [Test]
        public void Hello_Tolerates_Absent_Optional_Provenance()
        {
            // platform/buildId are funnel provenance (telemetry-spec §4), not identity. A client
            // that omits them still has an attributable match.
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{\"deviceToken\":\"abc\"}}");

            var res = ProtocolCodec.Decode(bytes);

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            var hello = (HelloMsg)res.Message!;
            Assert.That(hello.DeviceToken, Is.EqualTo("abc"));
            Assert.That(hello.Platform, Is.Empty);
            Assert.That(hello.BuildId, Is.Empty);
            Assert.That(hello.DisplayName, Is.Empty,
                "an absent name is the first-run state, not a decode failure");
        }

        [Test]
        public void Hello_Decodes_A_Hostile_DisplayName_Without_Judging_It()
        {
            // EF-073/VULN-002. The codec's gate is the tag registry; whether a value is ACCEPTABLE
            // is the server's policy gate (design §5, two gates). This asserts the split on purpose:
            // if a future change makes the codec refuse a bad name, the CLIENT — which decodes with
            // this same method — starts enforcing the server's policy, and MatchGate's check stops
            // being the thing that is tested. The name below is refused by PlayerName.Check; it must
            // still DECODE, and MatchGate is what must then drop it.
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{\"deviceToken\":\"abc\"," +
                "\"displayName\":\"<size=400%>PWNED\"}}");

            var res = ProtocolCodec.Decode(bytes);

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            Assert.That(((HelloMsg)res.Message!).DisplayName, Is.EqualTo("<size=400%>PWNED"));
        }
    }
}
