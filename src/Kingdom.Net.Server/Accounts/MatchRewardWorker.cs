using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// Pays the match Gold rewards for finished matches. Modelled on <see cref="RatingWorker"/> and
    /// fed from the same <see cref="IRankedMatchSink"/> seam because that result already carries the
    /// outcome, identities and source. Reward eligibility deliberately diverges from Elo for bot
    /// fill: the human receives ordinary progression while the synthetic opponent and rating do not.
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
        private readonly Func<AccountId, bool>? _isBotAccount;

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

        public MatchRewardWorker(
            GoldFaucet faucet,
            IAccountStore accounts,
            Func<AccountId, bool>? isBotAccount = null)
        {
            _faucet = faucet ?? throw new ArgumentNullException(nameof(faucet));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _isBotAccount = isBotAccount;
        }

        /// <summary>
        /// Accept one finished match for payment. Human ladder matches and the human seat of a
        /// bot-filled match qualify; AI soak, self-play and unattributed matches do not.
        /// </summary>
        public void Record(RankedMatchResult result)
        {
            if (!IsPayable(result)) return;
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
            if (!IsPayable(result)) return;
            await PaySeatAsync(result, result.P0, result.Outcome == MatchOutcome.Player0Win)
                .ConfigureAwait(false);
            await PaySeatAsync(result, result.P1, result.Outcome == MatchOutcome.Player1Win)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Reward eligibility is deliberately separate from <see cref="RankedMatchResult.IsEligible"/>:
        /// bot fill should look like an ordinary match to the player, but it must not move Elo.
        /// A bot-filled result is accepted only when the configured roster identifies exactly one
        /// bot seat, so a malformed source label cannot turn self-play into a Gold faucet.
        /// </summary>
        private bool IsPayable(RankedMatchResult result)
        {
            if (result.IsEligible) return true;

            // A friend challenge pays like a ladder match and moves no rating (Sam, 2026-10-01).
            // Two accounts one person owns can farm this; that was accepted knowingly.
            if (result.Source == TelemetrySource.Friendly)
                return result.P0.IsAttributed && result.P1.IsAttributed && result.P0 != result.P1
                    && result.Outcome != MatchOutcome.InProgress;

            if (result.Source != TelemetrySource.BotFill || _isBotAccount is null) return false;
            if (!result.P0.IsAttributed || !result.P1.IsAttributed || result.P0 == result.P1) return false;
            if (result.Outcome == MatchOutcome.InProgress) return false;
            return _isBotAccount(result.P0) != _isBotAccount(result.P1);
        }

        /// <summary>
        /// One seat's payment. A seat with no durable account behind it is paid nothing and is not
        /// an error: <see cref="AccountId"/> is derived from the device token for a guest, so the
        /// lookup misses, and a guest may not move currency anyway
        /// (<c>EconomyAccountPolicy.RequireDurable</c>). Refusing loudly here would log on every
        /// guest match.
        /// </summary>
        private async Task PaySeatAsync(RankedMatchResult result, AccountId seat, bool won)
        {
            if (_isBotAccount?.Invoke(seat) == true) return;
            // Conceding, or being conceded for after sitting out, pays nothing, or a concede on
            // turn one is Gold for a few seconds' work. The winner is still paid.
            if (!won && result.LoserForfeited) return;
            string playerId = seat.Value;
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
