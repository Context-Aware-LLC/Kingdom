using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// C-G1-8 step 1 — the connect handshake that carries guest identity (telemetry-spec §0:
    /// "identity = guest device token", accounts posture decision 6a). Before this, the socket
    /// connect WAS the hello and the server minted `guest-&lt;counter&gt;` from a per-process
    /// counter — so identity reset on every restart and no emitted row could be attributed.
    ///
    /// The hello is also the first client→server carrier for the client's contract version, which
    /// the server previously never learned (`EngineContractVersion` travels only server→client on
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
                ClientContractVersion: "interfaces.v3",
                Platform: "windows",
                BuildId: "m2-alpha-7");

            var res = ProtocolCodec.Decode(ProtocolCodec.Encode(sent, M, 0));

            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            Assert.That(res.Message, Is.InstanceOf<HelloMsg>());
            Assert.That((HelloMsg)res.Message!, Is.EqualTo(sent));
        }

        [Test]
        public void Hello_Without_A_DeviceToken_Is_Refused()
        {
            // An identity-less hello must not decode into a valid message — that is the hole the
            // whole condition exists to close, and it has to fail at the codec, not downstream.
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"matchId\":\"m\",\"seq\":0,\"type\":\"hello\",\"body\":{" +
                "\"clientContractVersion\":\"interfaces.v3\",\"platform\":\"windows\",\"buildId\":\"b\"}}");

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
                "\"deviceToken\":\"\",\"clientContractVersion\":\"interfaces.v3\"," +
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
        }
    }
}
