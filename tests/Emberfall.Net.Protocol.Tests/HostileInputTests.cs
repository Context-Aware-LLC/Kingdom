using System;
using System.Text;
using NUnit.Framework;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// Decode never throws on wire input (dev-docs/contracts/wire-protocol.md:5) — the server feeds
    /// attacker-controlled bytes straight in (state-sync §6.2 T-class). Every row must yield a
    /// typed <see cref="DecodeResult"/> failure, no exception.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class HostileInputTests
    {
        private static readonly (string name, string json, ProtocolError expected)[] Rows =
        {
            ("empty", "", ProtocolError.Malformed),
            ("not json", "hello", ProtocolError.Malformed),
            ("truncated", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\"", ProtocolError.Malformed),
            ("array root", "[1,2,3]", ProtocolError.Malformed),
            ("float", "{\"v\":1,\"matchId\":\"m\",\"seq\":1.5,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.Malformed),
            ("exponent", "{\"v\":1,\"matchId\":\"m\",\"seq\":1e3,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.Malformed),
            ("float in body", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\":1.0}}", ProtocolError.Malformed),
            ("leading zero", "{\"v\":01,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.Malformed),
            ("duplicate key", "{\"v\":1,\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.Malformed),
            ("trailing data", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"heartbeat\",\"body\":{}}x", ProtocolError.Malformed),
            ("missing v", "{\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.Malformed),
            ("missing body", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\"}", ProtocolError.Malformed),
            ("wrong version", "{\"v\":2,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\",\"body\":{\"version\":1}}", ProtocolError.VersionMismatch),
            ("unknown type", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"warp\",\"body\":{}}", ProtocolError.UnknownType),
            ("bad enum name", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"reject\",\"body\":{\"reason\":\"NotARealReason\",\"seq\":1}}", ProtocolError.BadField),
            ("numeric enum smuggle", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"reject\",\"body\":{\"reason\":\"2\",\"seq\":1}}", ProtocolError.BadField),
            ("u64 overflow", "{\"v\":1,\"matchId\":\"m\",\"seq\":99999999999999999999,\"type\":\"heartbeat\",\"body\":{}}", ProtocolError.Malformed),
            ("negative seq", "{\"v\":1,\"matchId\":\"m\",\"seq\":-1,\"type\":\"heartbeat\",\"body\":{}}", ProtocolError.Malformed),
            ("bad instance id", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"intent\",\"body\":{\"lastVersion\":0,\"intent\":{\"$type\":\"attack\",\"player\":\"p0\",\"attacker\":\"x5\",\"defender\":\"i1\"}}}", ProtocolError.BadField),
            ("unearth choice missing sequence", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"intent\",\"body\":{\"lastVersion\":0,\"intent\":{\"$type\":\"unearth_choice\",\"player\":\"p0\",\"chosenCardId\":\"card_x\"}}}", ProtocolError.BadField),
            ("unearth choice negative sequence", "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"intent\",\"body\":{\"lastVersion\":0,\"intent\":{\"$type\":\"unearth_choice\",\"player\":\"p0\",\"chosenCardId\":\"card_x\",\"choiceSequence\":-1}}}", ProtocolError.BadField),
            ("bad escape", "{\"v\":1,\"matchId\":\"\\q\",\"seq\":1,\"type\":\"heartbeat\",\"body\":{}}", ProtocolError.Malformed),
        };

        private static string[] RowNames() => Array.ConvertAll(Rows, r => r.name);

        [TestCaseSource(nameof(RowNames))]
        public void Hostile_Payload_Fails_Typed_Never_Throws(string name)
        {
            var row = Array.Find(Rows, r => r.name == name);
            DecodeResult res = default;
            Assert.DoesNotThrow(() => res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(row.json)),
                $"[{name}] Decode threw on hostile input.");
            Assert.That(res.Ok, Is.False, $"[{name}] hostile payload decoded successfully.");
            Assert.That(res.Error, Is.EqualTo(row.expected), $"[{name}] detail: {res.Detail}");
        }

        [Test]
        public void Depth_Bomb_Is_Rejected()
        {
            var sb = new StringBuilder("{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"heartbeat\",\"body\":");
            for (int i = 0; i < 100; i++) sb.Append("{\"a\":");
            sb.Append("1");
            for (int i = 0; i < 100; i++) sb.Append('}');
            sb.Append('}');
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(sb.ToString()));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.Malformed));
        }

        [Test]
        public void Oversized_Payload_Is_Rejected_Before_Parsing()
        {
            var big = new byte[ProtocolCodec.MaxMessageBytes + 1];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)' ';
            var res = ProtocolCodec.Decode(big);
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.TooLarge));
        }

        [Test]
        public void Invalid_Utf8_Is_Rejected()
        {
            var bytes = new byte[] { 0x7B, 0xFF, 0xFE, 0x7D };   // '{', invalid bytes, '}'
            var res = ProtocolCodec.Decode(bytes);
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.Malformed));
        }

        [Test]
        public void Unknown_Body_Keys_Are_Tolerated()
        {
            // Forward compatibility within a protocol version (design §3): unknown keys skip.
            const string json = "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"ack\"," +
                "\"body\":{\"version\":5,\"futureField\":\"ignored\"}}";
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.True, $"{res.Error} {res.Detail}");
            Assert.That(((AckMsg)res.Message!).Version, Is.EqualTo(5UL));
        }
    }
}
