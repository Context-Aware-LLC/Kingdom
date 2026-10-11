using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Tools.NetSoak;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// The contract every parallel capture rests on: jobs may finish in any order, and what is
    /// written is still the schedule's order. A capture that depended on which worker finished
    /// first could not be diffed against a re-run, and nothing downstream would notice.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class OrderedParallelTests
    {
        [Test]
        public async Task Results_are_committed_in_index_order_when_jobs_finish_out_of_order()
        {
            const int count = 40;
            var committed = new List<int>();
            var finished = new List<int>();

            int total = await OrderedParallel.RunAsync(
                count,
                workers: 8,
                async (index, token) =>
                {
                    // Earlier indices take longer, so completion order is roughly reversed.
                    await Task.Delay((count - index) % 7, token);
                    lock (finished) finished.Add(index);
                    return index * 10;
                },
                (index, result) =>
                {
                    Assert.That(result, Is.EqualTo(index * 10));
                    committed.Add(index);
                    return true;
                });

            Assert.Multiple(() =>
            {
                Assert.That(total, Is.EqualTo(count));
                Assert.That(committed, Is.EqualTo(Enumerable.Range(0, count)));
                Assert.That(finished, Is.Not.EqualTo(Enumerable.Range(0, count)),
                    "the jobs finished in schedule order, so this run proved nothing about reordering");
            });
        }

        [Test]
        public async Task No_more_than_the_requested_workers_are_ever_in_flight()
        {
            int inFlight = 0, peak = 0;

            await OrderedParallel.RunAsync(
                60,
                workers: 4,
                async (index, token) =>
                {
                    int now = Interlocked.Increment(ref inFlight);
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)))
                        Interlocked.CompareExchange(ref peak, now, seen);
                    await Task.Delay(2, token);
                    Interlocked.Decrement(ref inFlight);
                    return index;
                },
                (_, _) => true);

            Assert.That(peak, Is.GreaterThan(1).And.LessThanOrEqualTo(4));
        }

        [Test]
        public async Task One_worker_runs_each_job_to_its_commit_before_the_next_starts()
        {
            var log = new List<string>();

            await OrderedParallel.RunAsync(
                3,
                workers: 1,
                async (index, token) =>
                {
                    log.Add($"run {index}");
                    await Task.Yield();
                    return index;
                },
                (index, _) =>
                {
                    log.Add($"commit {index}");
                    return true;
                });

            Assert.That(log, Is.EqualTo(new[]
            {
                "run 0", "commit 0", "run 1", "commit 1", "run 2", "commit 2",
            }));
        }

        [TestCase(1)]
        [TestCase(6)]
        public async Task A_commit_that_returns_false_is_the_last_one(int workers)
        {
            var committed = new List<int>();

            int total = await OrderedParallel.RunAsync(
                50,
                workers,
                async (index, token) =>
                {
                    await Task.Delay(index % 3, token);
                    return index;
                },
                (index, _) =>
                {
                    committed.Add(index);
                    return index != 9;
                });

            Assert.Multiple(() =>
            {
                Assert.That(total, Is.EqualTo(10));
                Assert.That(committed, Is.EqualTo(Enumerable.Range(0, 10)));
            });
        }

        [TestCase(1)]
        [TestCase(6)]
        public void A_failed_job_fails_the_run_and_nothing_after_it_is_committed(int workers)
        {
            var committed = new List<int>();

            var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
                OrderedParallel.RunAsync(
                    50,
                    workers,
                    async (index, token) =>
                    {
                        await Task.Delay(index % 3, token);
                        if (index == 12) throw new InvalidOperationException("job 12 failed");
                        return index;
                    },
                    (index, _) =>
                    {
                        lock (committed) committed.Add(index);
                        return true;
                    }));

            Assert.Multiple(() =>
            {
                Assert.That(error!.Message, Is.EqualTo("job 12 failed"));
                Assert.That(committed, Is.EqualTo(Enumerable.Range(0, committed.Count)),
                    "what was committed before the failure must still be a clean prefix");
                Assert.That(committed, Has.None.GreaterThanOrEqualTo(12));
            });
        }

        [Test]
        public void Zero_workers_is_refused()
            => Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                OrderedParallel.RunAsync(1, 0, (index, _) => Task.FromResult(index), (_, _) => true));
    }
}
