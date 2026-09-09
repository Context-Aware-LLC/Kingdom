using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Ranking;
using Microsoft.Extensions.Hosting;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// Pays the match Gold rewards for finished matches. Modelled on <see cref="RatingWorker"/> and
    /// fed from the same <see cref="IRankedMatchSink"/> seam, because the question "did this match
    /// count?" has exactly one right answer and two systems must not disagree about it.
    ///
    /// <para><b>Off the match thread, deliberately.</b> <c>MatchSession</c> calls
    /// <see cref="Record"/> from inside its single-threaded dispatch, at the moment a match is
    /// tearing down. A ledger write there would put a database round-trip between the loser and
    /// their match-end screen, so this enqueues and returns; the grant lands a moment later. The
    /// player sees the new balance on their next wallet read either way — nothing in the match-end
    /// flow reads it.</para>
    ///
    /// <para><b>The ledger is the durability story, not this queue.</b> An in-process channel loses
    /// its backlog if the box dies, so a match that ended during a crash pays nothing and there is
    /// no retry. That is the same exposure <see cref="RatingWorker"/> accepts for ratings and it is
    /// accepted here for the same reason: the alternative is a durable outbox, and a handful of
    /// missed match rewards is cheaper than that. What the ledger DOES guarantee is the other
    /// direction — every transaction id below is deterministic, so a replay can never pay twice.</para>
    /// </summary>
    public sealed class MatchRewardWorker : BackgroundService, IRankedMatchSink
    {
        private readonly GoldFaucet _faucet;
        private readonly IAccountStore _accounts;

        /// <summary>Completed when the drain loop has finished — see <see cref="StopAsync"/>.</summary>
        private readonly TaskCompletionSource _drained =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Whether the drain loop ever began. Without it a <see cref="StopAsync"/> on a worker that
        /// was never started would wait on a task nothing will ever complete.
        /// </summary>
        private volatile bool _started;

        private readonly Channel<RankedMatchResult> _queue =
            Channel.CreateUnbounded<RankedMatchResult>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        public MatchRewardWorker(GoldFaucet faucet, IAccountStore accounts)
        {
            _faucet = faucet ?? throw new ArgumentNullException(nameof(faucet));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        }

        /// <summary>
        /// Accept one finished match for payment. Ineligible matches — bot-fill, AI soak, self-play,
        /// unattributed — are dropped here rather than filtered later, so nothing but a real human
        /// ladder match ever reaches the faucet.
        /// </summary>
        public void Record(RankedMatchResult result)
        {
            if (!result.IsEligible) return;
            if (!_queue.Writer.TryWrite(result))
                Console.Error.WriteLine(
                    "[rewards] result queue was closed before match {0} could be paid.",
                    result.Match.Value);
        }

        /// <remarks>
        /// <b>Run on the pool, not on the caller's stack, and every await is
        /// <c>ConfigureAwait(false)</c>.</b> <c>BackgroundService.StartAsync</c> invokes
        /// <c>ExecuteAsync</c> inline, so a caller with a <see cref="SynchronizationContext"/>
        /// installed would otherwise own the drain loop's continuations and the loop could only
        /// advance while that context was being pumped.
        /// </remarks>
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Set HERE rather than inside the loop: ExecuteAsync runs synchronously on StartAsync's
            // stack, so by the time StartAsync returns the flag is already true. Setting it inside
            // DrainAsync would leave a window in which a stop issued right after a start saw false
            // and skipped the drain it was supposed to wait for.
            _started = true;
            return Task.Run(DrainAsync, CancellationToken.None);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (RankedMatchResult result in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await PayAsync(result).ConfigureAwait(false);
            }
            finally
            {
                _drained.TrySetResult();
            }
        }

        /// <summary>
        /// Pay one finished match. The whole reward decision lives here rather than in the drain
        /// loop so that it can be exercised without starting a hosted service — the queue is how
        /// the work gets off the match thread, not part of what "paid correctly" means.
        ///
        /// <para>Ineligible results are a no-op, so this is safe to call with anything
        /// <c>MatchSession</c> produced.</para>
        ///
        /// <para><c>CancellationToken.None</c> throughout, for the reason
        /// <see cref="RatingWorker"/> uses it: the writer is completed before cancellation, so
        /// finishing a result already accepted into the process is the graceful path, not a leak.</para>
        /// </summary>
        public async Task PayAsync(RankedMatchResult result)
        {
            if (!result.IsEligible) return;
            await PaySeatAsync(result, result.P0.Value, result.Outcome == MatchOutcome.Player0Win)
                .ConfigureAwait(false);
            await PaySeatAsync(result, result.P1.Value, result.Outcome == MatchOutcome.Player1Win)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// One seat's payment. A seat with no durable account behind it is paid nothing and is not
        /// an error: <see cref="AccountId"/> is derived from the device token for a guest, so the
        /// lookup misses, and a guest may not move currency anyway
        /// (<c>EconomyAccountPolicy.RequireDurable</c>). Refusing loudly here would log on every
        /// guest match.
        /// </summary>
        private async Task PaySeatAsync(RankedMatchResult result, string playerId, bool won)
        {
            try
            {
                Account? account = await _accounts
                    .FindByPlayerIdAsync(playerId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (account is null || !EconomyAccountPolicy.IsDurable(account)) return;
                await _faucet
                    .GrantMatchAsync(account, result.Match.Value, won, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A reward that did not land must never take down the worker: the next match in the
                // queue belongs to somebody else. Logged rather than swallowed, because a faucet
                // failing every time pays nobody and is otherwise invisible.
                Console.Error.WriteLine("[rewards] match {0} was not paid for player_id={1}: {2}",
                    result.Match.Value, playerId, ex.Message);
            }
        }

        /// <summary>
        /// Closes the queue and waits for what is already in it to be paid.
        ///
        /// <para><b>The wait is on <see cref="_drained"/>, not on
        /// <c>base.StopAsync</c>.</b> Awaiting the base was not enough in practice: a stop issued
        /// immediately after a <see cref="Record"/> returned with the queue still full, and the
        /// grants then landed — or did not — after the caller had moved on. That is invisible in
        /// production (the next deploy simply pays a few matches late or not at all) and it made
        /// the tests here fail about half the time, which is how it was found. Draining is what
        /// "stopped" has to mean for a worker whose whole job is a side effect.</para>
        ///
        /// <para>Bounded by the caller's token so a wedged ledger cannot hold up host shutdown
        /// forever; a drain that loses that race is abandoned, exactly as it would be if the
        /// process were killed. Nothing is double-paid either way — every transaction id is
        /// deterministic.</para>
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _queue.Writer.TryComplete();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!_started) return;
            try
            {
                await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(
                    "[rewards] shutdown did not wait for the reward queue to drain; " +
                    "unpaid matches in it are dropped.");
            }
        }
    }
}
