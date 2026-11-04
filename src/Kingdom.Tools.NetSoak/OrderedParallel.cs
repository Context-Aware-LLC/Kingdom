using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// Runs independent jobs on several workers and hands each result to one committer in index
    /// order. The split is the whole point: a job plays a game and may finish at any time, while
    /// the committer appends to a manifest, a telemetry file and the console, so the files a run
    /// leaves behind are the same bytes whatever the worker count and a crash still leaves a clean
    /// prefix. A job must therefore touch nothing shared except what is safe to share (the card
    /// database, per-match replay files); everything ordered belongs in the committer.
    ///
    /// <para>One worker runs the jobs inline, one after another, on the caller's context — the
    /// loop this replaced, not a one-thread pool.</para>
    /// </summary>
    internal static class OrderedParallel
    {
        /// <summary>How far ahead of the committer workers may run, per worker. Bounds what one
        /// slow game can make the others hold in memory.</summary>
        private const int WindowPerWorker = 4;

        /// <param name="commit">
        /// Called once per job, in index order, never concurrently. Returning false stops the
        /// run: nothing after that index is committed and jobs still running are cancelled.
        /// </param>
        /// <returns>The number of results committed.</returns>
        public static async Task<int> RunAsync<T>(
            int count,
            int workers,
            Func<int, CancellationToken, Task<T>> run,
            Func<int, T, bool> commit,
            CancellationToken cancellationToken = default)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (workers < 1)
                throw new ArgumentOutOfRangeException(nameof(workers), "At least one worker is required.");
            if (run is null) throw new ArgumentNullException(nameof(run));
            if (commit is null) throw new ArgumentNullException(nameof(commit));

            if (workers == 1 || count <= 1)
            {
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    T result = await run(i, cancellationToken);
                    if (!commit(i, result)) return i + 1;
                }
                return count;
            }

            workers = Math.Min(workers, count);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var window = new SemaphoreSlim(workers * WindowPerWorker);
            var pending = new Dictionary<int, T>();
            var gate = new object();
            int next = -1, committed = 0;
            bool stopped = false;
            var failures = new List<(int Index, Exception Error)>();

            async Task WorkAsync()
            {
                while (true)
                {
                    try
                    {
                        await window.WaitAsync(stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    int i = Interlocked.Increment(ref next);
                    if (i >= count) return;

                    T result;
                    try
                    {
                        result = await run(i, stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        lock (gate) failures.Add((i, ex));
                        stop.Cancel();
                        return;
                    }

                    lock (gate)
                    {
                        pending[i] = result;
                        while (!stopped && pending.Remove(committed, out T? ready))
                        {
                            bool carryOn;
                            try
                            {
                                carryOn = commit(committed, ready);
                            }
                            catch (Exception ex)
                            {
                                failures.Add((committed, ex));
                                stopped = true;
                                stop.Cancel();
                                return;
                            }
                            committed++;
                            window.Release();
                            if (carryOn) continue;
                            stopped = true;
                            stop.Cancel();
                        }
                    }
                }
            }

            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++) tasks[w] = Task.Run(WorkAsync, CancellationToken.None);
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (failures.Count > 0)
            {
                // The lowest index is the one a serial run would have hit first.
                Exception first = failures.OrderBy(f => f.Index).First().Error;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return committed;
        }
    }
}
