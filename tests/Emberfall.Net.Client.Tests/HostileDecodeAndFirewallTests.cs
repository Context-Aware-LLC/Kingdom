using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Tests.Support;
using Emberfall.Net.Protocol;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests
{
    /// <summary>§11 test 7 (hostile server / decode surface) + the a5 §8 unknown-key round-trip
    /// + §11.8 firewall enrollment.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class HostileDecodeAndFirewallTests : ClientTestBase
    {
        [Test]
        public void Unknown_Envelope_Keys_Round_Trip_Under_A5_S8_Forward_Compat()
        {
            byte[] frame = ProtocolCodec.Encode(new HeartbeatMsg(), new MatchId("m"), 7);
            string json = Encoding.UTF8.GetString(frame);
            // A newer server adds a field this client has never heard of. It must be ignored, not fatal.
            string withUnknown = json.Replace("\"type\":", "\"futureField\":123,\"type\":");

            DecodeResult res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(withUnknown));
            Assert.That(res.Ok, Is.True, "unknown envelope keys are ignored (a5 §8), not a decode failure");
            Assert.That(res.Message, Is.TypeOf<HeartbeatMsg>());
        }

        [Test]
        public async Task Unknown_Key_Frame_Fed_To_The_Client_Does_Not_Trip_A_Resync()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0);
            using var c1 = new NetworkedMatchSession(peer.Transport1);
            await StartPairAsync(peer, c0, c1);
            int baseline = c0.InboundProcessed;

            byte[] frame = ProtocolCodec.Encode(new HeartbeatMsg(), new MatchId("m-loop"), 99);
            string withUnknown = Encoding.UTF8.GetString(frame).Replace("\"type\":", "\"futureField\":123,\"type\":");
            peer.Transport0.EnqueueInbound(Encoding.UTF8.GetBytes(withUnknown));
            await c0.WaitInboundAsync(baseline + 1);

            Assert.That(c0.ResyncCount, Is.EqualTo(0), "a forward-compatible frame is not corruption");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
        }

        [Test]
        public async Task Truncated_And_Garbage_Frames_Resync_Never_Throw()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { ResyncBudget = 100 });
            using var c1 = new NetworkedMatchSession(peer.Transport1);
            await StartPairAsync(peer, c0, c1);
            int baseline = c0.InboundProcessed;

            byte[][] hostile =
            {
                Array.Empty<byte>(),
                Encoding.UTF8.GetBytes("{"),
                Encoding.UTF8.GetBytes("{\"v\":2,\"matchId\":\"m\",\"seq\":1,\"type\":\"snapshot\",\"body\":{}}"), // wrong version
                Encoding.UTF8.GetBytes("{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"snapshot\",\"body\":{\"stateVersion\":1.5}}"), // float
                new byte[] { 0xff, 0xfe, 0x00 }, // not UTF-8
            };
            for (int i = 0; i < hostile.Length; i++)
            {
                peer.Transport0.EnqueueInbound(hostile[i]);
                await c0.WaitInboundAsync(baseline + i + 1);
            }

            // Every hostile frame became a resync request, none an exception, and the session lives
            // (budget is generous here — the point is "no throw", not the death path, which is 4c).
            Assert.That(c0.ResyncCount, Is.EqualTo(hostile.Length));
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
        }

        [Test]
        public void Client_Assembly_References_Neither_UnityEngine_Nor_The_Server()
        {
            Assembly client = typeof(NetworkedMatchSession).Assembly;
            string[] refs = client.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

            Assert.That(refs, Has.None.StartsWith("UnityEngine"),
                "§2: the client assembly is UnityEngine-free (the netstandard2.1 firewall).");
            Assert.That(refs, Has.None.EqualTo("Emberfall.Net.Server"),
                "OQ-B: the client NEVER references the server — only the tests do (one-way seam).");
            Assert.That(refs, Has.None.EqualTo("Emberfall.Core"),
                "§2: the client never touches rules/host/projector (ADR-0006); Contracts+Serialization+Protocol only.");
        }
    }
}
