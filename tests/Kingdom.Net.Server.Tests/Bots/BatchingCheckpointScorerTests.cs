using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Policy;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// The batcher sits behind the single-scorer seam, so a caller cannot see it. These pin what
    /// that requires: every call gets the answer to ITS decision, a failure reaches the calls in
    /// the failed batch and no others, and a call with nobody to share a batch with is still sent.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class BatchingCheckpointScorerTests
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

        [Test]
        public async Task Calls_waiting_while_the_backend_is_busy_go_out_as_one_batch_up_to_the_maximum()
        {
            var backend = new FakeBackend { HoldFirstBatch = true };
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 8, maxWait: TimeSpan.Zero);

            Task<PolicyScore> first = Score(scorer, 0);
            await backend.FirstBatchStarted.Task.WaitAsync(Deadline);
            Task<PolicyScore>[] rest = Enumerable.Range(1, 20).Select(n => Score(scorer, n)).ToArray();
            backend.ReleaseFirstBatch();
            await Task.WhenAll(rest.Prepend(first)).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 1, 8, 8, 4 }));
                Assert.That(scorer.Batches, Is.EqualTo(4));
                Assert.That(scorer.Decisions, Is.EqualTo(21));
                Assert.That(backend.PeakConcurrentBatches, Is.EqualTo(1),
                    "one backend is one ordered stream: a second batch was sent before the first answered");
            });
        }

        [Test]
        public async Task Every_call_gets_the_score_of_its_own_decision()
        {
            // Several backends answering out of order, batches of every size, callers on many
            // threads: the only thing tying an answer to a caller is its place in its batch.
            var backends = Enumerable.Range(0, 3)
                .Select(i => new FakeBackend { Jitter = new Random(100 + i) }).ToArray();
            await using var scorer = new BatchingCheckpointScorer(
                backends, maxBatch: 5, maxWait: TimeSpan.FromMilliseconds(1));

            const int calls = 600;
            var answers = new PolicyScore[calls];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, calls),
                new ParallelOptions { MaxDegreeOfParallelism = 24 },
                async (n, _) => answers[n] = await Score(scorer, n).WaitAsync(Deadline));

            int[] sizes = backends.SelectMany(b => b.BatchSizes).ToArray();
            Assert.Multiple(() =>
            {
                for (int n = 0; n < calls; n++)
                {
                    Assert.That(answers[n].Logits, Is.EqualTo(new double[] { n, -n }), $"call {n}");
                    Assert.That(answers[n].Value, Is.EqualTo(n / 1000.0), $"call {n}");
                }
                Assert.That(sizes.Sum(), Is.EqualTo(calls), "a decision was scored twice or never");
                Assert.That(sizes.Max(), Is.LessThanOrEqualTo(5));
                Assert.That(sizes.Max(), Is.GreaterThan(1),
                    "nothing was ever batched, so pairing inside a batch was never tested");
                Assert.That(backends.Count(b => b.BatchSizes.Count > 0), Is.GreaterThan(1),
                    "one backend took every batch, so several were never tested");
            });
        }

        [Test]
        public async Task A_failed_batch_fails_exactly_the_calls_that_were_in_it()
        {
            var backend = new FakeBackend { HoldFirstBatch = true, FailBatchContaining = 3 };
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 4, maxWait: TimeSpan.Zero);

            Task<PolicyScore> first = Score(scorer, 0);
            await backend.FirstBatchStarted.Task.WaitAsync(Deadline);
            // Queued in order behind the held batch: [1,2,3,4] fails, [5,6,7,8] does not.
            Task<PolicyScore>[] queued = Enumerable.Range(1, 8).Select(n => Score(scorer, n)).ToArray();
            backend.ReleaseFirstBatch();
            await Task.WhenAll(queued.Prepend(first).Select(Settled)).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 1, 4, 4 }));
                Assert.That(first.IsCompletedSuccessfully, Is.True);
                for (int n = 1; n <= 4; n++)
                {
                    Assert.That(queued[n - 1].IsFaulted, Is.True, $"call {n} shared the failed batch");
                    Assert.That(queued[n - 1].Exception!.InnerException,
                        Is.TypeOf<InvalidOperationException>().With.Message.Contains("scorer died"));
                }
                for (int n = 5; n <= 8; n++)
                {
                    Assert.That(queued[n - 1].IsCompletedSuccessfully, Is.True, $"call {n} was in the next batch");
                    Assert.That(queued[n - 1].Result.Logits[0], Is.EqualTo((double)n));
                }
            });
            // The backend that failed a batch is still the scorer: the next call is answered.
            Assert.That((await Score(scorer, 9).WaitAsync(Deadline)).Logits[0], Is.EqualTo(9.0));
        }

        [Test]
        public async Task A_decision_the_scorer_rejects_fails_alone_inside_its_batch()
        {
            var backend = new FakeBackend { HoldFirstBatch = true, RejectDecision = 2 };
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 8, maxWait: TimeSpan.Zero);

            Task<PolicyScore> first = Score(scorer, 0);
            await backend.FirstBatchStarted.Task.WaitAsync(Deadline);
            Task<PolicyScore>[] queued = Enumerable.Range(1, 3).Select(n => Score(scorer, n)).ToArray();
            backend.ReleaseFirstBatch();
            await Task.WhenAll(queued.Select(Settled)).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 1, 3 }));
                Assert.That(queued[0].IsCompletedSuccessfully, Is.True);
                Assert.That(queued[1].IsFaulted, Is.True);
                Assert.That(queued[1].Exception!.InnerException!.Message, Does.Contain("rejected decision 2"));
                Assert.That(queued[2].IsCompletedSuccessfully, Is.True);
                Assert.That(queued[2].Result.Logits[0], Is.EqualTo(3.0));
            });
        }

        [Test]
        public async Task A_lone_call_is_sent_after_the_wait_and_not_before()
        {
            var backend = new FakeBackend();
            var wait = TimeSpan.FromMilliseconds(150);
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 8, maxWait: wait);

            long started = Stopwatch.GetTimestamp();
            PolicyScore score = await Score(scorer, 7).WaitAsync(Deadline);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            Assert.Multiple(() =>
            {
                Assert.That(score.Logits[0], Is.EqualTo(7.0));
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 1 }));
                Assert.That(elapsed, Is.GreaterThanOrEqualTo(wait - TimeSpan.FromMilliseconds(5)),
                    "the batch was sent before the wait ran out, with room left in it");
            });
        }

        [Test]
        public async Task The_wait_gathers_a_latecomer_and_a_full_batch_does_not_wait()
        {
            var backend = new FakeBackend();
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 2, maxWait: TimeSpan.FromSeconds(10));

            // The backend is idle throughout, so only the wait can put these two in one batch,
            // and only the full batch can send it ten seconds early.
            long started = Stopwatch.GetTimestamp();
            Task<PolicyScore> early = Score(scorer, 1);
            await Task.Delay(50);
            Task<PolicyScore> late = Score(scorer, 2);
            await Task.WhenAll(early, late).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 2 }));
                Assert.That(Stopwatch.GetElapsedTime(started), Is.LessThan(TimeSpan.FromSeconds(5)));
            });
        }

        [Test]
        public async Task A_call_cancelled_while_queued_is_dropped_and_the_others_are_scored()
        {
            var backend = new FakeBackend { HoldFirstBatch = true };
            await using var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 8, maxWait: TimeSpan.Zero);
            using var cancel = new CancellationTokenSource();

            Task<PolicyScore> first = Score(scorer, 0);
            await backend.FirstBatchStarted.Task.WaitAsync(Deadline);
            Task<PolicyScore> kept = Score(scorer, 1);
            Task<PolicyScore> dropped = scorer.ScoreAsync(Observation(2), new { }, Array.Empty<JsonElement>(), cancel.Token);
            Task<PolicyScore> alsoKept = Score(scorer, 3);
            cancel.Cancel();
            backend.ReleaseFirstBatch();
            await Task.WhenAll(new[] { first, kept, dropped, alsoKept }.Select(Settled)).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(dropped.IsCanceled, Is.True);
                Assert.That(kept.Result.Logits[0], Is.EqualTo(1.0));
                Assert.That(alsoKept.Result.Logits[0], Is.EqualTo(3.0));
                Assert.That(backend.BatchSizes, Is.EqualTo(new[] { 1, 2 }),
                    "the cancelled call was still sent to the scorer");
            });
        }

        [Test]
        public async Task Disposal_fails_the_calls_still_queued_and_refuses_new_ones()
        {
            var backend = new FakeBackend { HoldFirstBatch = true };
            var scorer = new BatchingCheckpointScorer(
                new[] { backend }, maxBatch: 8, maxWait: TimeSpan.Zero);

            Task<PolicyScore> first = Score(scorer, 0);
            await backend.FirstBatchStarted.Task.WaitAsync(Deadline);
            Task<PolicyScore> queued = Score(scorer, 1);
            Task disposal = scorer.DisposeAsync().AsTask();
            backend.ReleaseFirstBatch();
            await disposal.WaitAsync(Deadline);
            await Task.WhenAll(Settled(first), Settled(queued)).WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(first.IsCompletedSuccessfully, Is.True, "a batch already sent is answered");
                Assert.That(queued.Exception?.InnerException, Is.TypeOf<ObjectDisposedException>());
                Assert.That(
                    async () => await Score(scorer, 2), Throws.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void A_batcher_refuses_settings_that_could_never_send_a_batch()
        {
            var backend = new FakeBackend();
            Assert.Multiple(() =>
            {
                Assert.That(
                    () => new BatchingCheckpointScorer(Array.Empty<ICheckpointBatchScorer>(), 8, TimeSpan.Zero),
                    Throws.ArgumentException);
                Assert.That(
                    () => new BatchingCheckpointScorer(new[] { backend }, 0, TimeSpan.Zero),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => new BatchingCheckpointScorer(new[] { backend }, 8, TimeSpan.FromMilliseconds(-1)),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            });
        }

        private static Task<PolicyScore> Score(BatchingCheckpointScorer scorer, int n)
            => scorer.ScoreAsync(Observation(n), new { }, Array.Empty<JsonElement>(), CancellationToken.None);

        private static JsonElement Observation(int n)
            => JsonSerializer.SerializeToElement(new { n });

        /// <summary>Completes when the task does, however it ends.</summary>
        private static Task Settled(Task task)
            => task.ContinueWith(_ => { }, TaskScheduler.Default);

        /// <summary>
        /// Answers decision <c>n</c> with logits <c>[n, -n]</c> and value <c>n/1000</c>, so an
        /// answer names the decision it belongs to.
        /// </summary>
        private sealed class FakeBackend : ICheckpointBatchScorer
        {
            private readonly ConcurrentQueue<int> _batchSizes = new ConcurrentQueue<int>();
            private readonly TaskCompletionSource _release =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _batches;
            private int _inFlight;
            private int _peak;

            public bool HoldFirstBatch { get; init; }

            /// <summary>The batch holding this decision throws instead of answering.</summary>
            public int? FailBatchContaining { get; init; }

            /// <summary>This decision alone is answered with an error.</summary>
            public int? RejectDecision { get; init; }

            public Random? Jitter { get; init; }

            public TaskCompletionSource FirstBatchStarted { get; } =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<int> BatchSizes => _batchSizes.ToArray();

            public int PeakConcurrentBatches => Volatile.Read(ref _peak);

            public void ReleaseFirstBatch() => _release.TrySetResult();

            public async Task<IReadOnlyList<BatchScore>> ScoreBatchAsync(
                IReadOnlyList<BatchDecision> decisions, CancellationToken cancellationToken)
            {
                int now = Interlocked.Increment(ref _inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref _peak)))
                    Interlocked.CompareExchange(ref _peak, now, seen);
                try
                {
                    _batchSizes.Enqueue(decisions.Count);
                    if (Interlocked.Increment(ref _batches) == 1 && HoldFirstBatch)
                    {
                        FirstBatchStarted.TrySetResult();
                        await _release.Task.ConfigureAwait(false);
                    }
                    if (Jitter is not null)
                    {
                        int spin;
                        lock (Jitter) spin = Jitter.Next(0, 3);
                        if (spin > 0) await Task.Delay(spin, cancellationToken).ConfigureAwait(false);
                        else await Task.Yield();
                    }
                    int[] ids = decisions.Select(d => d.Observation.GetProperty("n").GetInt32()).ToArray();
                    if (FailBatchContaining is int bad && ids.Contains(bad))
                        throw new InvalidOperationException("scorer died");
                    return ids.Select(n => n == RejectDecision
                        ? new BatchScore(null, new InvalidOperationException($"rejected decision {n}"))
                        : new BatchScore(new PolicyScore(new double[] { n, -n }, n / 1000.0), null)).ToArray();
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
    }
}
