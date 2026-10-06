using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Emberfall.Policy
{
    /// <summary>
    /// Several warm scorer processes for ONE checkpoint behind the single-scorer seam, so games
    /// running in parallel do not queue on one stdio stream. A request goes to whichever process
    /// is idle. That is sound only because a scorer is stateless between requests: the answer
    /// depends on the request and the weights, never on what the process scored before, so which
    /// process answers cannot change a game.
    ///
    /// <para>Every process is started with one inference thread, at every pool size including
    /// one. A scorer left to itself sizes a thread pool to the whole machine, and the thread count
    /// is part of what makes CPU inference reproducible; pinning it means a run's output does not
    /// depend on how many workers produced it.</para>
    /// </summary>
    public sealed class CheckpointScorerPool :
        ICheckpointScorer, ICheckpointStateEmbeddingScorer, IAsyncDisposable
    {
        /// <summary>Processes warmed at once. Each one loads the inference runtime from disk, and
        /// starting them all together can push a start past the readiness deadline.</summary>
        private const int StartConcurrency = 4;

        private readonly CheckpointScorerProcess[] _scorers;
        private readonly Channel<CheckpointScorerProcess> _idle;

        private CheckpointScorerPool(CheckpointScorerProcess[] scorers)
        {
            _scorers = scorers;
            _idle = Channel.CreateUnbounded<CheckpointScorerProcess>();
            foreach (CheckpointScorerProcess scorer in scorers) _idle.Writer.TryWrite(scorer);
        }

        public string CheckpointSha256 => _scorers[0].CheckpointSha256;

        /// <summary>How many requests can be in flight at once.</summary>
        public int Size => _scorers.Length;

        public static CheckpointScorerPool Start(
            string rlExport,
            string checkpoint,
            int size,
            string device = "auto",
            string? pythonExecutable = null,
            string? repositoryRoot = null,
            TimeSpan? requestTimeout = null)
        {
            if (size < 1)
                throw new ArgumentOutOfRangeException(nameof(size), "A scorer pool needs at least one process.");

            var started = new CheckpointScorerProcess?[size];
            try
            {
                Parallel.For(
                    0, size, new ParallelOptions { MaxDegreeOfParallelism = StartConcurrency },
                    i => started[i] = CheckpointScorerProcess.Start(
                        rlExport, checkpoint, device, pythonExecutable, repositoryRoot,
                        requestTimeout, intraOpThreads: 1));
            }
            catch
            {
                foreach (CheckpointScorerProcess? scorer in started)
                    scorer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
            return new CheckpointScorerPool(started.Select(scorer => scorer!).ToArray());
        }

        async Task<PolicyScore> ICheckpointScorer.ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
        {
            CheckpointScorerProcess scorer =
                await _idle.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ((ICheckpointScorer)scorer)
                    .ScoreAsync(observation, playerMemory, legalActions, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // A faulted process goes back too: it refuses every later request, which is the
                // same fail-closed behaviour a single faulted scorer has.
                _idle.Writer.TryWrite(scorer);
            }
        }

        async Task<PolicyScore> ICheckpointStateEmbeddingScorer.ScoreWithStateEmbeddingAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
        {
            CheckpointScorerProcess scorer =
                await _idle.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ((ICheckpointStateEmbeddingScorer)scorer)
                    .ScoreWithStateEmbeddingAsync(observation, playerMemory, legalActions, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _idle.Writer.TryWrite(scorer);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (CheckpointScorerProcess scorer in _scorers)
                await scorer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
