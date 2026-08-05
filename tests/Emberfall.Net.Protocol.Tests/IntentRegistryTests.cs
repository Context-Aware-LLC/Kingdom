using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// Registry completeness + unknown-tag behavior (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md §10.4). The codec's
    /// tag table is hand-written (zero runtime reflection → IL2CPP-stripping-proof, §5 as-built);
    /// THIS test is where the reflection lives: it proves the table covers every sealed
    /// [ReplayTag]-annotated PlayerIntent subtype, so a new intent that forgets either the attribute
    /// or the codec case goes red at test time, not runtime.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class IntentRegistryTests
    {
        private static readonly PlayerId P = new PlayerId("p0");

        /// <summary>One sample instance per intent subtype — extend when a new intent lands.</summary>
        private static readonly Dictionary<Type, PlayerIntent> Samples = new()
        {
            [typeof(PlayCardIntent)] = new PlayCardIntent(P, new InstanceId(5), 1, new List<TargetRef> { new(new InstanceId(9)) }),
            [typeof(AttackIntent)] = new AttackIntent(P, new InstanceId(5), new TargetRef(new InstanceId(1))),
            [typeof(HeroPowerIntent)] = new HeroPowerIntent(P, new List<TargetRef>()),
            [typeof(EndTurnIntent)] = new EndTurnIntent(P),
            [typeof(MulliganIntent)] = new MulliganIntent(P, new List<InstanceId> { new(3), new(4) }),
            [typeof(UnearthChoiceIntent)] = new UnearthChoiceIntent(P, "card_x"),
            [typeof(ConcedeIntent)] = new ConcedeIntent(P),
            [typeof(AutoConfirmIntent)] = new AutoConfirmIntent(P),
        };

        private static IEnumerable<Type> TaggedIntentTypes()
            => typeof(PlayerIntent).Assembly.GetTypes()
                .Where(t => t.IsSealed && t.IsSubclassOf(typeof(PlayerIntent))
                            && t.GetCustomAttribute<ReplayTagAttribute>() != null)
                .OrderBy(t => t.Name, StringComparer.Ordinal);

        [Test]
        public void Every_Tagged_Intent_Subtype_Has_A_Sample_Here()
        {
            Assert.That(Samples.Keys.OrderBy(t => t.Name, StringComparer.Ordinal), Is.EqualTo(TaggedIntentTypes()),
                "A [ReplayTag] PlayerIntent subtype has no wire-codec sample — add it to this test AND " +
                "to IntentJsonCodec (write + read + KnownTags) in the same commit (design §10.4).");
        }

        [TestCaseSource(nameof(TaggedIntentTypes))]
        public void Tagged_Intent_RoundTrips_Under_Its_ReplayTag(Type intentType)
        {
            var sample = Samples[intentType];
            string tag = intentType.GetCustomAttribute<ReplayTagAttribute>()!.Tag;

            var bytes = ProtocolCodec.Encode(new IntentMsg(1, sample), new MatchId("m"), 1);
            string json = Encoding.UTF8.GetString(bytes);
            Assert.That(json, Does.Contain($"\"$type\":\"{tag}\""),
                $"{intentType.Name} did not serialize under its [ReplayTag] '{tag}'.");

            var res = ProtocolCodec.Decode(bytes);
            Assert.That(res.Ok, Is.True, $"{res.Error} {res.Detail}");
            Assert.That(((IntentMsg)res.Message!).Intent, Is.InstanceOf(intentType));
        }

        [Test]
        public void Unknown_Tag_Fails_Typed_Not_Thrown()
        {
            const string json = "{\"v\":1,\"matchId\":\"m\",\"seq\":1,\"type\":\"intent\"," +
                "\"body\":{\"lastVersion\":0,\"intent\":{\"$type\":\"debug_spawn\",\"player\":\"p0\"}}}";
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.UnknownIntentTag),
                "off-registry tag must surface as UnknownIntentTag (server maps to RejectReason.UnknownIntent).");
            Assert.That(res.Seq, Is.EqualTo(1UL), "envelope seq must survive a body failure (reject routing).");
        }
    }
}
