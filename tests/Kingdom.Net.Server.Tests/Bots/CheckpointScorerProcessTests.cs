using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Policy;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// Drives the real stdio process against a stand-in <c>training_scorer.py</c>, so it needs a
    /// <c>python</c> on PATH and skips without one.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CheckpointScorerProcessTests
    {
        private const string FakeScorer = @"
import json, sys, time
print(json.dumps({'ready': True, 'protocol': 'emberfall-policy-scorer-v1'}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    if request['observation'] == 'stall':
        time.sleep(30)
    print(json.dumps({'requestId': request['requestId'],
                      'logits': [0.0] * len(request['legalActions']), 'value': 0.5}), flush=True)
";

        private const string ScriptVariable = CheckpointScorerProcess.ScriptVariable;

        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            if (!PythonOnPath()) Assert.Ignore("python is not on PATH");
            _root = Path.Combine(Path.GetTempPath(), "scorer-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "tooling", "scripts"));
            Directory.CreateDirectory(Path.Combine(_root, "export"));
            File.WriteAllText(Path.Combine(_root, "tooling", "scripts", "training_scorer.py"), FakeScorer);
            File.WriteAllText(Path.Combine(_root, "model.pt"), "weights");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root.Length > 0 && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        [Test]
        public async Task A_timed_out_request_fails_alone_and_the_next_one_gets_a_fresh_process()
        {
            await using CheckpointScorerProcess scorer = CheckpointScorerProcess.Start(
                Path.Combine(_root, "export"), Path.Combine(_root, "model.pt"),
                repositoryRoot: _root, requestTimeout: TimeSpan.FromSeconds(2));
            ICheckpointScorer seam = scorer;

            Assert.ThrowsAsync<TimeoutException>(() => Score(seam, "stall"));
            PolicyScore after = await Score(seam, "normal");

            Assert.Multiple(() =>
            {
                Assert.That(after.Logits, Has.Count.EqualTo(2));
                Assert.That(scorer.Restarts, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task ScorerScriptFromEnvironmentNeedsNoRepositoryRoot()
        {
            // No tooling/ anywhere above this script: only the variable can find it.
            string elsewhere = Path.Combine(
                Path.GetTempPath(), "scorer-env-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(elsewhere);
            string script = Path.Combine(elsewhere, "training_scorer.py");
            File.WriteAllText(script, FakeScorer);
            string? previous = Environment.GetEnvironmentVariable(ScriptVariable);
            try
            {
                Environment.SetEnvironmentVariable(ScriptVariable, script);
                await using CheckpointScorerProcess scorer = CheckpointScorerProcess.Start(
                    Path.Combine(_root, "export"), Path.Combine(_root, "model.pt"));
                PolicyScore score = await Score(scorer, "normal");
                Assert.That(score.Logits, Has.Count.EqualTo(2));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ScriptVariable, previous);
                // The scorer's working directory is the script's folder and a just-killed
                // process can still hold it; the OS temp sweep owns whatever is left.
                try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { }
            }
        }

        [Test]
        public void ScorerScriptFromEnvironmentMissingFileNamesTheVariable()
        {
            string missing = Path.Combine(_root, "nowhere", "training_scorer.py");
            string? previous = Environment.GetEnvironmentVariable(ScriptVariable);
            try
            {
                Environment.SetEnvironmentVariable(ScriptVariable, missing);
                var ex = Assert.Throws<FileNotFoundException>(() =>
                    CheckpointScorerProcess.Start(
                        Path.Combine(_root, "export"), Path.Combine(_root, "model.pt"),
                        repositoryRoot: _root));
                Assert.Multiple(() =>
                {
                    Assert.That(ex!.Message, Does.Contain(ScriptVariable));
                    Assert.That(ex.Message, Does.Contain(missing));
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable(ScriptVariable, previous);
            }
        }

        private static Task<PolicyScore> Score(ICheckpointScorer scorer, string observation)
        {
            JsonElement action = JsonDocument.Parse("{}").RootElement;
            return scorer.ScoreAsync(
                JsonDocument.Parse(JsonSerializer.Serialize(observation)).RootElement,
                new { }, new List<JsonElement> { action, action }, CancellationToken.None);
        }

        private static bool PythonOnPath()
        {
            try
            {
                using Process? probe = Process.Start(new ProcessStartInfo("python", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                return probe is not null && probe.WaitForExit(10_000) && probe.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
