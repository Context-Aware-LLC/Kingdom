using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// Replays a <see cref="ReplayFixture"/> against a FRESH core via the same <c>Submit → Execute</c>
    /// path the slice (and later the server) uses (dev-docs/quality/testing-and-ci.md:24). Produces the actual final-state
    /// hash + full event stream and, on mismatch, a first-divergence <see cref="ReplayDivergenceReport"/>.
    /// Never rewrites a fixture on mismatch — rebaselining is loud, deliberate, and one-fixture-at-a-time
    /// (§5).
    /// </summary>
    public static class ReplayHarness
    {
        public sealed class ReplayResult
        {
            public ReplayFixture Fixture = null!;
            public string ActualHash = "";
            public List<GameEvent> ActualEvents = new();
            public bool Passed;
            public ReplayDivergenceReport? Report;
        }

        /// <summary>
        /// Run the fixture and capture actual outputs (no assertions — caller decides).
        /// <paramref name="onState"/> (A5, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md §6): invoked with the live
        /// authoritative state after <c>BeginMatch</c> and after every accepted command — this is
        /// how the wire bridge test obtains its per-command reachable-state corpus. The reference
        /// is to the MUTABLE host state: consume it synchronously inside the callback, never store it.
        /// </summary>
        public static ReplayResult Run(ReplayFixture fx, Action<GameState>? onState = null)
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, fx.CardSetSnapshot);
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            var processor = new CommandProcessor(db);
            // Every hash-load-bearing setup input comes from the fixture, never from a C# default
            // (C-G1-7): schema v1 files supply the v1 implicit values at load, so no hash moves.
            var setup = new MatchSetup(
                fx.P0Class, fx.P0Deck, fx.P1Class, fx.P1Deck,
                heroMaxHealth: fx.HeroMaxHealth, sparkTokenId: fx.SparkTokenId);
            var host = new LocalMatchHost(processor, db, setup);

            host.BeginMatch(fx.MatchId, fx.P0, fx.P1, fx.Seed);   // core-driven opening (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §2.1)
            onState?.Invoke(host.State);

            var stream = new List<GameEvent>();
            foreach (var intent in fx.Intents)
            {
                var result = host.Submit(intent);
                if (!result.Accepted)
                    throw new InvalidOperationException(
                        $"Fixture '{fx.Name}' submitted an illegal intent ({intent.GetType().Name}) → {result.Reason}. " +
                        "A golden fixture must contain only legal intents.");
                stream.AddRange(result.Events);   // Sequence is global, so a flat append preserves order
                onState?.Invoke(host.State);
            }

            var actualHash = GameStateHasher.Hash(host.State);

            var res = new ReplayResult
            {
                Fixture = fx,
                ActualHash = actualHash,
                ActualEvents = stream,
                Report = null,
            };

            var report = new ReplayDivergenceReport(fx.Name);

            // §6.1 event stream first.
            if (fx.ExpectedEvents != null)
                report.RecordEventDivergence(fx.ExpectedEvents, stream);

            // §6.2 state hash (both checks always run, §6.3).
            if (fx.ExpectedStateHash != null && !string.Equals(fx.ExpectedStateHash, actualHash, StringComparison.Ordinal))
                report.RecordStateHashDivergence(fx.ExpectedStateHash, actualHash, Array.Empty<string>());

            res.Passed = fx.ExpectedStateHash != null && fx.ExpectedEvents != null && !report.HasDivergence;
            res.Report = report.HasDivergence ? report : null;
            return res;
        }

        /// <summary>
        /// Intentional rebaselining (dev-docs/quality/testing-and-ci.md:31). NEVER called by CI — only by an explicit local
        /// command (env <c>EMBERFALL_REBASELINE=&lt;name&gt;|all</c>). Writes the computed expected block back
        /// into the one named fixture and drops a sibling <c>&lt;name&gt;.rebaseline.md</c> stub the committer
        /// must fill in (the §5.2 guard, enforced by the CI rebaseline path-pair check).
        /// </summary>
        public static void Rebaseline(ReplayFixture fx)
        {
            var res = Run(fx);

            var node = JsonNode.Parse(File.ReadAllText(fx.SourcePath))!.AsObject();
            var expected = new JsonObject
            {
                ["stateHashAlgo"] = GameStateHasher.Algorithm,
                ["stateHash"] = res.ActualHash,
            };
            var arr = new JsonArray();
            foreach (var e in res.ActualEvents) arr.Add(EventToNode(e));
            expected["eventStream"] = arr;
            node["expected"] = expected;

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(fx.SourcePath, node.ToJsonString(opts) + "\n");

            // Drop the rebaseline justification stub if it does not exist yet.
            var stub = Path.ChangeExtension(fx.SourcePath, null); // strips ".json"
            stub = Path.ChangeExtension(stub, null);              // strips ".replay"
            stub += ".rebaseline.md";
            if (!File.Exists(stub))
            {
                File.WriteAllText(stub,
                    $"# Rebaseline: {fx.Name}\n\n" +
                    "_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._\n\n" +
                    "- **Which rule changed:** _fill in_\n" +
                    "- **Why the new golden is correct:** _fill in_\n" +
                    "- **Link to the design/eng change that caused it:** _fill in_\n" +
                    "- **cardSetSnapshot changed?** _no / which_\n" +
                    "- **engineContractVersion changed?** _no / which_\n");
            }
        }

        private static JsonObject EventToNode(GameEvent e) => new JsonObject
        {
            ["seq"] = e.Sequence,
            ["kind"] = e.Kind.ToString(),
            ["src"] = e.Source.HasValue ? e.Source.Value.ToString() : null,
            ["tgt"] = e.Target.HasValue ? e.Target.Value.ToString() : null,
            ["amt"] = e.Amount.HasValue ? JsonValue.Create(e.Amount.Value) : null,
            ["flags"] = e.Flags.ToString(),
        };
    }
}
