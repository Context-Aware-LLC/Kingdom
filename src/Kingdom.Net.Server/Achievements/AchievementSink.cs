using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// Watches match telemetry for achievements, and banks the King's Favor alongside them. Like
    /// <c>QuestProgressSink</c> it decorates <see cref="ITelemetrySink"/>, forwards every call
    /// untouched, and is a second reader of an event that already exists.
    ///
    /// <para><b>Enqueue and return.</b> The sink contract is must-not-block and must-not-throw, and
    /// it is called from inside the match's dispatch as the match tears down. An evaluation that
    /// threw or blocked there would cost a player their match-end screen.</para>
    ///
    /// <para><b>The queue must drain on a deploy</b>, or every deploy loses the scores of the matches
    /// that just ended. <see cref="StopAsync"/> waits for it as long as the host's shutdown timeout
    /// allows, which <c>ServerApp</c> sets, and <c>fly.toml</c>'s <c>kill_timeout</c> outlasts.</para>
    /// </summary>
    public sealed class AchievementSink : BackgroundService, ITelemetrySink
    {
        private readonly ITelemetrySink _inner;
        private readonly AchievementService _achievements;
        private readonly FavorService? _favor;

        private readonly Channel<(MatchCompleteEvent Complete, KingSeating? King)> _queue =
            Channel.CreateUnbounded<(MatchCompleteEvent, KingSeating?)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly TaskCompletionSource _drained =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _started;

        /// <param name="favor">Null leaves the King's Favor out: achievements are unchanged.</param>
        public AchievementSink(ITelemetrySink inner, AchievementService achievements, FavorService? favor = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
            _favor = favor;
        }

        void ITelemetrySink.MatchComplete(MatchCompleteEvent e) => MatchComplete(e);

        /// <summary>
        /// <paramref name="king"/> is the match's seating, or null when it played for no King. It
        /// rides beside the event rather than on it, because the event is written to telemetry and
        /// the seating carries the players' names.
        /// </summary>
        public void MatchComplete(MatchCompleteEvent e, KingSeating? king = null)
        {
            _inner.MatchComplete(e);
            try
            {
                if (!_queue.Writer.TryWrite((e, king)))
                    Console.Error.WriteLine(
                        "[achievements] queue was closed before match {0} could be evaluated.", e.Match.Value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[achievements] match {0} was not queued: {1}", e.Match.Value, ex.Message);
            }
        }

        /// <summary>A void has no result, so it earns nothing and banks nothing.</summary>
        public void MatchVoid(MatchVoidEvent e) => _inner.MatchVoid(e);

        public void CardsSeen(CardSeenEvent e) => _inner.CardsSeen(e);

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _started = true;
            return Task.Run(DrainAsync, CancellationToken.None);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach ((MatchCompleteEvent complete, KingSeating? king) in
                               _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await EvaluateAsync(complete, king).ConfigureAwait(false);
            }
            finally
            {
                _drained.TrySetResult();
            }
        }

        /// <summary>
        /// Evaluate one finished match for each attributed seat: achievements first, then the
        /// Favor. Public so the decision can be exercised without a hosted service.
        /// </summary>
        public async Task EvaluateAsync(MatchCompleteEvent complete, KingSeating? king = null)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                SeatOutcome outcome = seat == 0 ? complete.P0 : complete.P1;
                if (!outcome.Account.IsAttributed) continue;
                AchievementMatchFacts facts = AchievementMatchFacts.ForSeat(complete, seat);
                try
                {
                    await _achievements.ApplyAsync(facts, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One seat failing must not cost the other theirs, or take down the worker.
                    Console.Error.WriteLine("[achievements] match {0} was not applied for player_id={1}: {2}",
                        facts.MatchId, facts.PlayerId, ex.Message);
                }

                if (king is null || _favor is null) continue;
                try
                {
                    await _favor.ApplyAsync(facts, king, seat == 0 ? king.P0Name : king.P1Name, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Separate from the achievements above: a Favor failure never costs one.
                    Console.Error.WriteLine("[king] match {0} was not banked for player_id={1}: {2}",
                        facts.MatchId, facts.PlayerId, ex.Message);
                }
            }
        }

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
                    "[achievements] shutdown did not wait for the queue to drain; " +
                    "unevaluated matches in it are dropped, with their King's Favor scores.");
            }
        }
    }
}
