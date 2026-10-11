using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kingdom.Policy
{
    /// <summary>One decision inside a batch frame. <see cref="PlayerMemory"/> stays an
    /// <c>object</c> so it is written by its runtime type, as a single request writes it.</summary>
    internal sealed record BatchDecision(
        JsonElement Observation, object PlayerMemory, IReadOnlyList<JsonElement> LegalActions);

    /// <summary>One decision's answer: a score, or the reason this decision alone has none.</summary>
    internal sealed record BatchScore(PolicyScore? Score, Exception? Error);

    /// <summary>A scorer that answers several decisions with one forward pass, in order.</summary>
    internal interface ICheckpointBatchScorer
    {
        Task<IReadOnlyList<BatchScore>> ScoreBatchAsync(
            IReadOnlyList<BatchDecision> decisions, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Gathers <see cref="ICheckpointScorer.ScoreAsync"/> calls that are waiting at the same time
    /// into one batch request. A caller sees the single-scorer seam and nothing else: a searching
    /// seat that awaits a round's rollouts together gets them scored a batch at a time without
    /// knowing it.
    ///
    /// <para>Each backend runs one loop: take the oldest waiting call, gather what else is
    /// waiting up to <c>maxBatch</c>, give latecomers until <c>maxWait</c> after the first one was
    /// taken, then send. With no wait a batch is exactly what queued while the backends were
    /// busy, which under load is most of the gain and costs a lone request nothing.</para>
    ///
    /// <para>NOT BIT-REPRODUCIBLE, by construction. A padded batch moves a row's logits by about
    /// 1e-4 against the same row alone, and which calls share a batch is decided by timing. Runs
    /// that SAMPLE from these logits therefore differ run to run, on any device. Callers that
    /// need a reproducible game keep the unbatched scorer.</para>
    ///
    /// <para>A failed batch fails exactly the calls that were in it. A decision the scorer
    /// rejected on its own fails that one call. A cancelled call leaves the queue (or, once sent,
    /// stops waiting) without touching the others: the batch is never cancelled on one caller's
    /// behalf, because cancelling a request kills the scorer process.</para>
    /// </summary>
    internal sealed class BatchingCheckpointScorer : ICheckpointScorer, IAsyncDisposable
    {
        private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task[] _loops;
        private readonly int _maxBatch;
        private readonly TimeSpan _maxWait;
        private long _batches;
        private long _decisions;
        private int _disposed;

        public BatchingCheckpointScorer(
            IReadOnlyList<ICheckpointBatchScorer> backends, int maxBatch, TimeSpan maxWait)
        {
            if (backends is null || backends.Count == 0)
                throw new ArgumentException("A batching scorer needs at least one backend.", nameof(backends));
            if (maxBatch < 1)
                throw new ArgumentOutOfRangeException(nameof(maxBatch), "A batch holds at least one decision.");
            if (maxWait < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxWait), "The batch wait cannot be negative.");
            _maxBatch = maxBatch;
            _maxWait = maxWait;
            _loops = new Task[backends.Count];
            for (int i = 0; i < _loops.Length; i++)
            {
                ICheckpointBatchScorer backend = backends[i];
                _loops[i] = Task.Run(() => RunAsync(backend));
            }
        }

        public int MaxBatch => _maxBatch;

        public TimeSpan MaxWait => _maxWait;

        /// <summary>Batch requests sent so far.</summary>
        public long Batches => Interlocked.Read(ref _batches);

        /// <summary>Decisions those batches carried; over <see cref="Batches"/> it is the mean
        /// batch, which is what says whether the settings are gathering anything.</summary>
        public long Decisions => Interlocked.Read(ref _decisions);

        public Task<PolicyScore> ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<PolicyScore>(cancellationToken);
            var pending = new Pending(
                new BatchDecision(observation, playerMemory, legalActions), cancellationToken);
            if (!_queue.Writer.TryWrite(pending))
                return Task.FromException<PolicyScore>(
                    new ObjectDisposedException(nameof(BatchingCheckpointScorer)));
            return cancellationToken.CanBeCanceled
                ? pending.Completion.Task.WaitAsync(cancellationToken)
                : pending.Completion.Task;
        }

        private async Task RunAsync(ICheckpointBatchScorer backend)
        {
            var batch = new List<Pending>(_maxBatch);
            ChannelReader<Pending> reader = _queue.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
                {
                    batch.Clear();
                    long started = Stopwatch.GetTimestamp();
                    while (batch.Count < _maxBatch)
                    {
                        if (reader.TryRead(out Pending? next))
                        {
                            // A caller that gave up while queued is not worth a row.
                            if (!next.Cancellation.IsCancellationRequested) batch.Add(next);
                            continue;
                        }
                        // Nothing was taken: another loop got there first, or all were cancelled.
                        if (batch.Count == 0) break;
                        if (Stopwatch.GetElapsedTime(started) >= _maxWait) break;
                        // POLLED, NOT TIMED. A timer on Windows fires on a ~15 ms tick, so a
                        // sub-millisecond wait asked of one would hold every lone request for a
                        // whole tick. The wait is short by design, so yielding through it is
                        // cheaper than that.
                        if (_stop.IsCancellationRequested) break;
                        Thread.Yield();
                    }
                    if (batch.Count == 0) continue;
                    await ScoreAsync(backend, batch).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                // Taken but never sent: disposal interrupted the gather.
                foreach (Pending pending in batch)
                    pending.Completion.TrySetException(
                        new ObjectDisposedException(nameof(BatchingCheckpointScorer)));
            }
        }

        private async Task ScoreAsync(ICheckpointBatchScorer backend, List<Pending> batch)
        {
            var decisions = new BatchDecision[batch.Count];
            for (int i = 0; i < decisions.Length; i++) decisions[i] = batch[i].Decision;
            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _decisions, decisions.Length);

            IReadOnlyList<BatchScore> scores;
            try
            {
                scores = await backend.ScoreBatchAsync(decisions, CancellationToken.None)
                    .ConfigureAwait(false);
                if (scores.Count != decisions.Length)
                    throw new InvalidOperationException(
                        $"Batch scorer answered {decisions.Length} decisions with {scores.Count} scores.");
            }
            catch (Exception ex)
            {
                // The failure belongs to this batch and to nobody queued behind it. Each caller
                // gets the same exception object: it describes one event.
                foreach (Pending pending in batch) pending.Completion.TrySetException(ex);
                batch.Clear();
                return;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                BatchScore score = scores[i];
                if (score.Score is not null) batch[i].Completion.TrySetResult(score.Score);
                else
                    batch[i].Completion.TrySetException(
                        score.Error ?? new InvalidOperationException("Batch scorer returned neither a score nor an error."));
            }
            batch.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.Writer.TryComplete();
            _stop.Cancel();
            try
            {
                await Task.WhenAll(_loops).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A loop that died has already failed the calls it held; the rest fail below.
            }
            while (_queue.Reader.TryRead(out Pending? pending))
                pending.Completion.TrySetException(
                    new ObjectDisposedException(nameof(BatchingCheckpointScorer)));
            _stop.Dispose();
        }

        private sealed class Pending
        {
            public Pending(BatchDecision decision, CancellationToken cancellation)
            {
                Decision = decision;
                Cancellation = cancellation;
            }

            public BatchDecision Decision { get; }

            public CancellationToken Cancellation { get; }

            // Continuations run off the loop: a caller's continuation is a whole game step, and
            // run inline it would hold the backend idle while the next batch waits.
            public TaskCompletionSource<PolicyScore> Completion { get; } =
                new TaskCompletionSource<PolicyScore>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
