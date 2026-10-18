using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kingdom.Net.Server.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// Watches match telemetry for achievements. Like <c>QuestProgressSink</c> it decorates
    /// <see cref="ITelemetrySink"/>, forwards every call untouched, and is a second reader of an event
    /// that already exists.
    ///
    /// <para><b>Enqueue and return.</b> The sink contract is must-not-block and must-not-throw, and
    /// it is called from inside the match's dispatch as the match tears down. An evaluation that
    /// threw or blocked there would cost a player their match-end screen.</para>
    /// </summary>
    public sealed class AchievementSink : BackgroundService, ITelemetrySink
    {
        private readonly ITelemetrySink _inner;
        private readonly AchievementService _achievements;

        private readonly Channel<MatchCompleteEvent> _queue =
            Channel.CreateUnbounded<MatchCompleteEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly TaskCompletionSource _drained =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _started;

        public AchievementSink(ITelemetrySink inner, AchievementService achievements)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        }

        public void MatchComplete(MatchCompleteEvent e)
        {
            _inner.MatchComplete(e);
            try
            {
                if (!_queue.Writer.TryWrite(e))
                    Console.Error.WriteLine(
                        "[achievements] queue was closed before match {0} could be evaluated.", e.Match.Value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[achievements] match {0} was not queued: {1}", e.Match.Value, ex.Message);
            }
        }

        /// <summary>A void has no result, so it earns nothing.</summary>
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
                await foreach (MatchCompleteEvent complete in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await EvaluateAsync(complete).ConfigureAwait(false);
            }
            finally
            {
                _drained.TrySetResult();
            }
        }

        /// <summary>
        /// Evaluate one finished match. Public so the decision can be exercised without a hosted
        /// service.
        /// </summary>
        public async Task EvaluateAsync(MatchCompleteEvent complete)
        {
            foreach (AchievementMatchFacts facts in AchievementMatchFacts.From(complete))
            {
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
                    "unevaluated matches in it are dropped.");
            }
        }
    }
}
