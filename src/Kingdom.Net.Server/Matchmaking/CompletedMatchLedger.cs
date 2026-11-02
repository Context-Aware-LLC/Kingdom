using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Matchmaking
{
    /// <summary>How many matches an account has finished, as far as the first-match policy needs.</summary>
    public interface ICompletedMatchCounter
    {
        /// <summary>The account's completed matches, capped at <paramref name="cap"/>.</summary>
        Task<int> CountAsync(AccountId account, int cap, CancellationToken ct = default);
    }

    /// <summary>
    /// Records every finished match per human seat into <see cref="ICompletedMatchStore"/>, and
    /// answers <see cref="FirstMatchPolicy"/>'s question of how many an account has finished.
    /// Fed from the <see cref="IRankedMatchSink"/> seam like <see cref="MatchRewardWorker"/>, and
    /// off the match thread for the same reason.
    ///
    /// <para><b>A match counts the moment it is recorded, not when the row lands.</b> The write is
    /// queued, so a player who taps "play again" straight off the result screen could otherwise
    /// queue before their first match is in the store and be dealt Greedy twice. The match id is
    /// held in <see cref="_pending"/> until its write succeeds, and <see cref="CountAsync"/> reads
    /// that BEFORE the store, so an id moving from one to the other is never missed. A write that
    /// fails stays pending for the life of the process, so this server still counts it.</para>
    ///
    /// <para><b>What counts:</b> a match that reached a result (a concede or a timeout too) for a
    /// seat that is not a bot. AI soak never counts. A match that failed to start, was voided, or
    /// was cut off by a restart records nothing, so the player is owed the same bot again.</para>
    /// </summary>
    public sealed class CompletedMatchLedger : BackgroundService, IRankedMatchSink, ICompletedMatchCounter
    {
        private readonly ICompletedMatchStore _store;
        private readonly Func<AccountId, bool>? _isBotAccount;

        private readonly object _lock = new object();
        private readonly Dictionary<AccountId, HashSet<string>> _pending = new Dictionary<AccountId, HashSet<string>>();

        private readonly Channel<(AccountId Account, string MatchId, DateTimeOffset EndedAt)> _queue =
            Channel.CreateUnbounded<(AccountId, string, DateTimeOffset)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly TaskCompletionSource _drained =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _started;

        public CompletedMatchLedger(ICompletedMatchStore store, Func<AccountId, bool>? isBotAccount = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _isBotAccount = isBotAccount;
        }

        public void Record(RankedMatchResult result)
        {
            if (result.Outcome == MatchOutcome.InProgress) return;
            if (result.Source == TelemetrySource.AiSoak) return;
            Note(result.P0, result);
            if (result.P1 != result.P0) Note(result.P1, result);
        }

        private void Note(AccountId seat, RankedMatchResult result)
        {
            if (!seat.IsAttributed) return;
            if (_isBotAccount?.Invoke(seat) == true) return;
            lock (_lock)
            {
                if (!_pending.TryGetValue(seat, out var ids)) _pending[seat] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(result.Match.Value);
            }
            if (!_queue.Writer.TryWrite((seat, result.Match.Value, result.EndedAtUtc)))
                Console.Error.WriteLine(
                    "[first-match] queue was closed before match {0} could be recorded; " +
                    "it still counts on this server.", result.Match.Value);
        }

        public async Task<int> CountAsync(AccountId account, int cap, CancellationToken ct = default)
        {
            if (cap <= 0) return 0;
            // Pending FIRST, then the store: an id that finishes writing between the two reads has
            // already landed in the store when the second read runs.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            lock (_lock)
            {
                if (_pending.TryGetValue(account, out var pending)) ids.UnionWith(pending);
            }
            if (ids.Count >= cap) return cap;
            ids.UnionWith(await _store.FirstMatchIdsAsync(account.Value, cap, ct).ConfigureAwait(false));
            return Math.Min(ids.Count, cap);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _started = true;
            return Task.Run(DrainAsync, CancellationToken.None);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var row in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await WriteAsync(row.Account, row.MatchId, row.EndedAt).ConfigureAwait(false);
            }
            finally
            {
                _drained.TrySetResult();
            }
        }

        /// <summary>One row. Public so a test can write without starting the hosted service.</summary>
        public async Task WriteAsync(AccountId account, string matchId, DateTimeOffset endedAt)
        {
            try
            {
                await _store.RecordAsync(account.Value, matchId, endedAt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[first-match] match {0} was not recorded for player_id={1}: {2}",
                    matchId, account.Value, ex.Message);
                return;   // stays pending: this server still counts it
            }
            lock (_lock)
            {
                if (_pending.TryGetValue(account, out var ids) && ids.Remove(matchId) && ids.Count == 0)
                    _pending.Remove(account);
            }
        }

        /// <summary>Closes the queue and waits for it to drain, as <see cref="MatchRewardWorker"/> does.</summary>
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
                    "[first-match] shutdown did not wait for completed matches to be recorded; " +
                    "a newcomer may be owed the same bot once more.");
            }
        }
    }
}
