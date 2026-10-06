using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Policy
{
    /// <summary>
    /// One warm Python scorer process speaking newline-delimited JSON over stdio. Requests are
    /// serialized because stdin/stdout form one ordered stream; a timeout faults and terminates the
    /// process so a late response can never be mistaken for a later decision.
    /// </summary>
    public sealed class CheckpointScorerProcess :
        ICheckpointScorer, ICheckpointStateEmbeddingScorer, IAsyncDisposable
    {
        private const string Protocol = "emberfall-policy-scorer-v1";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly Process _process;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _requestTimeout;
        private readonly StringBuilder _stderr = new StringBuilder();
        private long _nextRequestId;
        private bool _faulted;

        private CheckpointScorerProcess(Process process, string checkpointSha256, TimeSpan requestTimeout)
        {
            _process = process;
            CheckpointSha256 = checkpointSha256;
            _requestTimeout = requestTimeout;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderr) _stderr.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
        }

        public string CheckpointSha256 { get; }

        public static CheckpointScorerProcess Start(
            string rlExport,
            string checkpoint,
            string device = "auto",
            string? pythonExecutable = null,
            string? repositoryRoot = null,
            TimeSpan? requestTimeout = null,
            int? intraOpThreads = null)
        {
            if (intraOpThreads is <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(intraOpThreads), "A scorer needs at least one inference thread.");
            if (string.IsNullOrWhiteSpace(rlExport))
                throw new ArgumentException("RL export path must not be empty.", nameof(rlExport));
            if (string.IsNullOrWhiteSpace(checkpoint))
                throw new ArgumentException("Checkpoint path must not be empty.", nameof(checkpoint));
            string exportPath = Path.GetFullPath(rlExport);
            string checkpointPath = Path.GetFullPath(checkpoint);
            if (!Directory.Exists(exportPath))
                throw new DirectoryNotFoundException($"RL export directory not found: {exportPath}");
            if (!File.Exists(checkpointPath))
                throw new FileNotFoundException("Checkpoint file not found.", checkpointPath);

            string root = repositoryRoot is null
                ? FindRepositoryRoot()
                : Path.GetFullPath(repositoryRoot);
            string script = Path.Combine(root, "tooling", "scripts", "training_scorer.py");
            if (!File.Exists(script))
                throw new FileNotFoundException("Training scorer entry point not found.", script);

            var start = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(pythonExecutable) ? "python" : pythonExecutable,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--rl-export");
            start.ArgumentList.Add(exportPath);
            start.ArgumentList.Add("--checkpoint");
            start.ArgumentList.Add(checkpointPath);
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(device);
            if (intraOpThreads is int threads)
            {
                // Every scorer otherwise sizes its own thread pool to the whole machine, and a
                // pool of them spends its time contending for the same cores.
                start.ArgumentList.Add("--threads");
                start.ArgumentList.Add(
                    threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start the Python checkpoint scorer.");
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(checkpointPath))).ToLowerInvariant();
            var scorer = new CheckpointScorerProcess(
                process, sha, requestTimeout ?? TimeSpan.FromSeconds(30));
            try
            {
                scorer.WaitUntilReady();
                return scorer;
            }
            catch
            {
                scorer.DisposeAsync().GetAwaiter().GetResult();
                throw;
            }
        }

        async Task<PolicyScore> ICheckpointScorer.ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
            => await ScoreAsync(
                observation, playerMemory, legalActions,
                includeStateEmbedding: false, cancellationToken).ConfigureAwait(false);

        async Task<PolicyScore> ICheckpointStateEmbeddingScorer.ScoreWithStateEmbeddingAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
            => await ScoreAsync(
                observation, playerMemory, legalActions,
                includeStateEmbedding: true, cancellationToken).ConfigureAwait(false);

        private async Task<PolicyScore> ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            bool includeStateEmbedding,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_faulted)
                    throw new InvalidOperationException("Checkpoint scorer process is faulted and cannot accept more requests.");
                if (_process.HasExited)
                    throw ScorerFailure($"Checkpoint scorer exited with code {_process.ExitCode}.");

                long requestId = Interlocked.Increment(ref _nextRequestId);
                string request = JsonSerializer.Serialize(new
                {
                    requestId,
                    observation,
                    playerMemory,
                    legalActions,
                    includeStateEmbedding,
                }, JsonOptions);
                try
                {
                    await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    FaultProcess();
                    throw ScorerFailure(
                        $"Checkpoint scorer closed stdin before request {requestId}: {ex.Message}");
                }

                string? line;
                try
                {
                    // StreamReader's token-aware read can remain parked in the underlying Windows
                    // pipe after cancellation. WaitAsync owns the deadline outside that pipe read,
                    // so an unresponsive scorer cannot strand an arena indefinitely.
                    line = await _process.StandardOutput.ReadLineAsync()
                        .WaitAsync(_requestTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    FaultProcess();
                    throw new TimeoutException(
                        $"Checkpoint scorer did not answer request {requestId} within "
                        + $"{_requestTimeout.TotalSeconds:0.###} seconds.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    FaultProcess();
                    throw;
                }
                catch (IOException ex)
                {
                    FaultProcess();
                    throw ScorerFailure(
                        $"Checkpoint scorer stdout failed during request {requestId}: {ex.Message}");
                }
                if (line is null)
                    throw ScorerFailure("Checkpoint scorer closed stdout before returning a response.");

                ScorerResponse response;
                try
                {
                    response = JsonSerializer.Deserialize<ScorerResponse>(line, JsonOptions)
                        ?? throw new JsonException("response decoded as null");
                }
                catch (JsonException ex)
                {
                    FaultProcess();
                    throw new InvalidDataException($"Checkpoint scorer returned invalid JSON: {ex.Message}", ex);
                }
                if (response.RequestId != requestId)
                {
                    FaultProcess();
                    throw new InvalidDataException(
                        $"Checkpoint scorer response id {response.RequestId} did not match request {requestId}.");
                }
                if (!string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidOperationException($"Checkpoint scorer rejected request {requestId}: {response.Error}");
                if (response.Logits is null)
                    throw new InvalidDataException("Checkpoint scorer response omitted logits.");
                if (includeStateEmbedding
                    && (response.StateEmbedding is null || response.StateEmbedding.Count == 0
                        || response.StateFusionInput is null || response.StateFusionInput.Count == 0))
                    throw new InvalidDataException("Checkpoint scorer response omitted the requested state representation.");
                return new PolicyScore(
                    response.Logits, response.Value,
                    response.StateEmbedding, response.StateFusionInput);
            }
            catch
            {
                if (_process.HasExited) _faulted = true;
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            FaultProcess();
            _process.Dispose();
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }

        private InvalidOperationException ScorerFailure(string message)
        {
            _faulted = true;
            if (_process.HasExited) _process.WaitForExit();
            string diagnostics;
            lock (_stderr) diagnostics = _stderr.ToString().Trim();
            return new InvalidOperationException(
                diagnostics.Length == 0 ? message : $"{message} scorer stderr: {diagnostics}");
        }

        private void FaultProcess()
        {
            _faulted = true;
            try
            {
                // The scorer is the direct Python child and does not launch worker processes. A
                // non-tree kill avoids Windows process-tree enumeration blocking the timeout path
                // whose sole job is to make an unresponsive scorer fail closed.
                if (!_process.HasExited) _process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) { }
        }

        private void WaitUntilReady()
        {
            string? line;
            try
            {
                // Keep the deadline outside StreamReader for the same reason as request reads:
                // cancellation-aware pipe reads can remain parked indefinitely on Windows.
                line = _process.StandardOutput.ReadLineAsync()
                    .WaitAsync(_requestTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                FaultProcess();
                throw new TimeoutException(
                    $"Checkpoint scorer did not become ready within {_requestTimeout.TotalSeconds:0.###} seconds.");
            }
            catch (IOException ex)
            {
                FaultProcess();
                throw ScorerFailure($"Checkpoint scorer failed before readiness: {ex.Message}");
            }
            if (line is null)
                throw ScorerFailure("Checkpoint scorer closed stdout before its readiness frame.");

            ScorerReady ready;
            try
            {
                ready = JsonSerializer.Deserialize<ScorerReady>(line, JsonOptions)
                    ?? throw new JsonException("readiness frame decoded as null");
            }
            catch (JsonException ex)
            {
                FaultProcess();
                throw new InvalidDataException(
                    $"Checkpoint scorer returned an invalid readiness frame: {ex.Message}", ex);
            }
            if (!ready.Ready || !string.Equals(ready.Protocol, Protocol, StringComparison.Ordinal))
            {
                FaultProcess();
                throw new InvalidDataException(
                    $"Checkpoint scorer readiness protocol was '{ready.Protocol}', expected '{Protocol}'.");
            }
        }

        private static string FindRepositoryRoot()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                DirectoryInfo? cursor = new DirectoryInfo(start);
                while (cursor is not null)
                {
                    string candidate = Path.Combine(cursor.FullName, "tooling", "scripts", "training_scorer.py");
                    if (File.Exists(candidate)) return cursor.FullName;
                    cursor = cursor.Parent;
                }
            }
            throw new DirectoryNotFoundException(
                "Could not locate the repository root containing tooling/scripts/training_scorer.py.");
        }

        private sealed record ScorerResponse(
            long RequestId,
            IReadOnlyList<double>? Logits,
            double Value,
            int SelectedActionIndex,
            IReadOnlyList<double>? StateEmbedding,
            IReadOnlyList<double>? StateFusionInput,
            string? Error);

        private sealed record ScorerReady(bool Ready, string? Protocol);
    }
}
