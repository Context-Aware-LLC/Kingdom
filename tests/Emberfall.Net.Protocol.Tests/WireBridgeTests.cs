using System;
using System.IO;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.GoldenReplay;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// The A2 deferred bridge obligation, disfervord (dev-docs/architecture/determinism-and-projection.md:13;
    /// dev-docs/contracts/wire-protocol.md:41): for every reachable state s,
    /// <c>CanonicalHash(Decode(Encode(s))) == CanonicalHash(s)</c>. Corpus = every post-command
    /// state of every golden-replay fixture — authoritative AND per-viewer projected (projected
    /// states are what the wire actually carries; authoritative states additionally exercise live
    /// RNG cursors and un-redacted zones). States are consumed synchronously inside the capture
    /// callback (the harness hands out the mutable host state).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class WireBridgeTests
    {
        private static string[] FixtureFiles()
            => Directory.GetFiles(RepoPaths.FixturesDir, "*.replay.json");

        [TestCaseSource(nameof(FixtureFiles))]
        public void Every_PostCommand_State_Survives_The_Wire_Hash_Preserving(string fixturePath)
        {
            var fx = ReplayFixture.Load(fixturePath);
            int statesChecked = 0;

            ReplayHarness.Run(fx, state =>
            {
                AssertRoundTripPreservesHash(state, $"{fx.Name} authoritative @v{state.StateVersion}");
                foreach (var viewer in new[] { fx.P0, fx.P1 })
                {
                    var projected = GameStateProjector.Project(state, viewer);
                    AssertRoundTripPreservesHash(projected, $"{fx.Name} projected({viewer}) @v{state.StateVersion}");
                }
                statesChecked++;
            });

            Assert.That(statesChecked, Is.GreaterThan(0), "capture hook never fired — corpus is empty");
        }

        private static void AssertRoundTripPreservesHash(GameState s, string label)
        {
            string expectedHash = GameStateHasher.Hash(s);

            var bytes = ProtocolCodec.Encode(SnapshotMsg.Of(s), s.Id, seq: 1);
            var decoded = ProtocolCodec.Decode(bytes);

            Assert.That(decoded.Ok, Is.True, $"[{label}] decode failed: {decoded.Error} {decoded.Detail}");
            var snap = (SnapshotMsg)decoded.Message!;
            Assert.That(GameStateHasher.Hash(snap.State), Is.EqualTo(expectedHash),
                $"[{label}] the JSON walk dropped or misread a field — canonical hash changed across " +
                "the wire round-trip (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md §6: fix SnapshotJsonCodec, never the hasher).");
            Assert.That(snap.VerifyIntegrity(), Is.True,
                $"[{label}] mirror-integrity check failed on a clean round-trip.");
        }
    }
}
