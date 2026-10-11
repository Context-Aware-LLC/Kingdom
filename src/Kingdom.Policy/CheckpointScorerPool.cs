using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kingdom.Policy
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
    ///
    /// <para>BATCHING IS OFF UNLESS ASKED FOR. With a batch size above one the pool gathers the
    /// calls waiting at the same time into batch requests (<see cref="BatchingCheckpointScorer"/>),
    /// one gathering loop per process. That is faster and it is NOT the same numbers: a batch
    /// moves logits by about 1e-4 and its membership is decided by timing, so everything above
    /// about which process answers stops being true of a batched pool. It is a different label
    /// lineage and the caller has to say so wherever it records what a run was.</para>
    /// </summary>
    public sealed class CheckpointScorerPool :
        ICheckpointScorer, ICheckpointStateEmbeddingScorer, IAsyncDisposable
    {
        /// <summary>Processes warmed at once. Each one loads the inference runtime from disk, and
        /// starting them all together can push a start past the readiness deadline.</summary>
        private const int StartConcurrency = 4;

        private readonly CheckpointScorerProcess[] _scorers;
        private readonly Channel<CheckpointScorerProcess> _idle;
        private readonly BatchingCheckpointScorer? _batcher;

        private CheckpointScorerPool(
            CheckpointScorerProcess[] scorers, int batchSize, TimeSpan batchWait)
        {
            _scorers = scorers;
            _idle = Channel.CreateUnbounded<CheckpointScorerProcess>();
            foreach (CheckpointScorerProcess scorer in scorers) _idle.Writer.TryWrite(scorer);
            if (batchSize > 1)
                _batcher = new BatchingCheckpointScorer(scorers, batchSize, batchWait);
        }

        public string CheckpointSha256 => _scorers[0].CheckpointSha256;

        /// <summary>How many requests can be in flight at once.</summary>
        public int Size => _scorers.Length;

        /// <summary>The largest batch one request carries; 1 when the pool does not batch.</summary>
        public int BatchSize => _batcher?.MaxBatch ?? 1;

        /// <summary>Batch requests sent and the decisions they carried, both 0 when unbatched.
        /// Their ratio is the mean batch a run actually achieved.</summary>
        public (long Batches, long Decisions) BatchCounts
            => _batcher is null ? (0, 0) : (_batcher.Batches, _batcher.Decisions);

        public static CheckpointScorerPool Start(
            string rlExport,
            string checkpoint,
            int size,
            string device = "auto",
            string? pythonExecutable = null,
            string? repositoryRoot = null,
            TimeSpan? requestTimeout = null,
            int batchSize = 1,
            TimeSpan? batchWait = null)
        {
            if (size < 1)
                throw new ArgumentOutOfRangeException(nameof(size), "A scorer pool needs at least one process.");
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize), "A batch holds at least one decision.");
            if (batchWait is { } wait && wait < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(batchWait), "The batch wait cannot be negative.");

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
            return new CheckpointScorerPool(
                started.Select(scorer => scorer!).ToArray(), batchSize, batchWait ?? TimeSpan.Zero);
        }

        async Task<PolicyScore> ICheckpointScorer.ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
        {
            if (_batcher is not null)
                return await _batcher
                    .ScoreAsync(observation, playerMemory, legalActions, cancellationToken)
                    .ConfigureAwait(false);
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
            // Never batched: a batch frame carries no state representation. In a batched pool
            // this request waits its turn on a process behind that process's batches.
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
            if (_batcher is not null) await _batcher.DisposeAsync().ConfigureAwait(false);
            foreach (CheckpointScorerProcess scorer in _scorers)
                await scorer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
