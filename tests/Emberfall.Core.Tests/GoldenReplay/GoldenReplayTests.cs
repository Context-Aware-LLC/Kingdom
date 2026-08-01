using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// L3 — the R1 gate (dev-docs/quality/testing-and-ci.md:24). Every fixture replays against a fresh core and must
    /// reproduce the stored final-state hash AND the full ordered event stream exactly. Either mismatch
    /// fails the build. Emits <c>artifacts/golden-replay-results.json</c> (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/ci-gates.md §4).
    ///
    /// Rebaseline mode: set env <c>EMBERFALL_REBASELINE=&lt;fixtureName&gt;</c> (or <c>all</c>) to regenerate
    /// the expected block for the named fixture(s) — loud, deliberate, never automatic in CI (§5).
    /// </summary>
    [TestFixture]
    [Category("GoldenReplay")]
    public sealed class GoldenReplayTests
    {
        private static readonly List<object> _results = new();

        public static IEnumerable<string> FixtureFiles()
        {
            var dir = RepoPaths.FixturesDir;
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*.replay.json"))
                yield return f;
        }

        [TestCaseSource(nameof(FixtureFiles))]
        public void Fixture_Replays_To_Stored_Hash_And_EventStream(string fixturePath)
        {
            var fx = ReplayFixture.Load(fixturePath);

            string? rebaseline = Environment.GetEnvironmentVariable("EMBERFALL_REBASELINE");
            if (!string.IsNullOrEmpty(rebaseline) &&
                (string.Equals(rebaseline, "all", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rebaseline, fx.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ReplayHarness.Rebaseline(fx);
                Assert.Ignore($"Rebaselined fixture '{fx.Name}' (wrote expected block + rebaseline stub). Review & commit.");
                return;
            }

            Assert.That(fx.ExpectedStateHash, Is.Not.Null,
                $"Fixture '{fx.Name}' has no expected.stateHash. Bake it first: EMBERFALL_REBASELINE={fx.Name}");
            Assert.That(fx.ExpectedEvents, Is.Not.Null,
                $"Fixture '{fx.Name}' has no expected.eventStream. Bake it first: EMBERFALL_REBASELINE={fx.Name}");

            var res = ReplayHarness.Run(fx);

            lock (_results)
            {
                _results.Add(new
                {
                    fixture = fx.Name,
                    pass = res.Passed,
                    expectedHash = fx.ExpectedStateHash,
                    actualHash = res.ActualHash,
                    events = res.ActualEvents.Count,
                    report = res.Report?.ToString(),
                });
            }

            if (!res.Passed)
                Assert.Fail(res.Report?.ToString() ?? $"Golden replay diverged for '{fx.Name}'.");
        }

        [OneTimeTearDown]
        public void WriteArtifact()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n  \"gate\": \"golden-replay\",\n  \"fixtures\": [\n");
                for (int i = 0; i < _results.Count; i++)
                {
                    sb.Append("    ").Append(System.Text.Json.JsonSerializer.Serialize(_results[i]));
                    sb.Append(i == _results.Count - 1 ? "\n" : ",\n");
                }
                sb.Append("  ]\n}\n");
                File.WriteAllText(Path.Combine(RepoPaths.ArtifactsDir, "golden-replay-results.json"), sb.ToString());
            }
            catch (DirectoryNotFoundException) { /* artifacts dir unavailable in some sandboxes; non-fatal */ }
        }
    }
}
