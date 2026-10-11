using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Quests
{
    /// <summary>
    /// Watches match telemetry for quest progress (dev-docs/specs/daily-quests.md §7). It decorates
    /// <see cref="ITelemetrySink"/> and forwards every call to the sink it wraps, untouched — quest
    /// progress is a SECOND reader of events that already exist, and it must never be able to change
    /// what the telemetry sample records.
    ///
    /// <para><b>Why this seam and not <c>IRankedMatchSink</c>, where the Gold faucet sits.</b>
    /// <c>RankedMatchResult</c> carries the outcome and nothing else, and most of what a quest may
    /// ask (spec §3) is the per-card and per-seat detail only the telemetry events hold. Reaching
    /// for the ranked sink would mean widening it until it was the telemetry event, badly.</para>
    ///
    /// <para><b>Enqueue and return.</b> The sink contract is must-not-block and must-not-throw, and
    /// <c>MatchSession.Emit</c> swallows anything that breaks it — so a quest evaluation that threw
    /// or blocked here would cost a player their match-end screen and leave no trace.</para>
    /// </summary>
    public sealed class QuestProgressSink : BackgroundService, ITelemetrySink
    {
        /// <summary>
        /// How many finished matches may be waiting for their card rows at once. A match emits
        /// <c>match_complete</c> and then <c>card_seen</c> from adjacent lines of the same method,
        /// so the real occupancy is one; this only bounds the leak if a <c>card_seen</c> is ever
        /// dropped by its own <c>Emit</c> guard while its <c>match_complete</c> got through.
        /// </summary>
        private const int MaxPending = 256;

        private readonly ITelemetrySink _inner;
        private readonly QuestProgressService _quests;
        private readonly ICardDatabase _cards;

        private readonly Channel<(MatchCompleteEvent Complete, CardSeenEvent Cards)> _queue =
            Channel.CreateUnbounded<(MatchCompleteEvent, CardSeenEvent)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly object _pendingLock = new object();
        private readonly Dictionary<string, MatchCompleteEvent> _pending =
            new Dictionary<string, MatchCompleteEvent>(StringComparer.Ordinal);
        private readonly Queue<string> _pendingOrder = new Queue<string>();

        private readonly TaskCompletionSource _drained =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _started;

        public QuestProgressSink(ITelemetrySink inner, QuestProgressService quests, ICardDatabase cards)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _quests = quests ?? throw new ArgumentNullException(nameof(quests));
            _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        }

        public void MatchComplete(MatchCompleteEvent e)
        {
            _inner.MatchComplete(e);
            try
            {
                lock (_pendingLock)
                {
                    _pending[e.Match.Value] = e;
                    _pendingOrder.Enqueue(e.Match.Value);
                    while (_pendingOrder.Count > MaxPending)
                        _pending.Remove(_pendingOrder.Dequeue());
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[quests] match {0} was not held for pairing: {1}",
                    e.Match.Value, ex.Message);
            }
        }

        /// <summary>
        /// The card sample arrives second and is what triggers evaluation — it carries the played
        /// cards, and its partner carries the turn count. A card event whose <c>match_complete</c>
        /// was never seen is dropped: half the facts is not a smaller quest update, it is a wrong one.
        /// </summary>
        public void CardsSeen(CardSeenEvent e)
        {
            _inner.CardsSeen(e);
            try
            {
                MatchCompleteEvent? complete;
                lock (_pendingLock)
                {
                    _pending.Remove(e.Match.Value, out complete);
                }
                if (complete is null) return;
                if (!_queue.Writer.TryWrite((complete, e)))
                    Console.Error.WriteLine(
                        "[quests] queue was closed before match {0} could be evaluated.", e.Match.Value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[quests] match {0} was not queued: {1}", e.Match.Value, ex.Message);
            }
        }

        /// <summary>A void has no winner and emits no card sample, so it advances nothing.</summary>
        public void MatchVoid(MatchVoidEvent e)
        {
            _inner.MatchVoid(e);
            lock (_pendingLock) { _pending.Remove(e.Match.Value); }
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
                await foreach (var pair in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await EvaluateAsync(pair.Complete, pair.Cards).ConfigureAwait(false);
            }
            finally
            {
                _drained.TrySetResult();
            }
        }

        /// <summary>
        /// Evaluate one paired match. Public so the decision can be exercised without a hosted
        /// service — the queue is how the work gets off the match thread, not part of what a correct
        /// evaluation is.
        /// </summary>
        public async Task EvaluateAsync(MatchCompleteEvent complete, CardSeenEvent cards)
        {
            foreach (QuestMatchFacts facts in QuestMatchFacts.From(complete, cards, _cards))
            {
                try
                {
                    await _quests.ApplyAsync(facts, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One player's quest failing must not cost the other seat theirs, and must not
                    // take down the worker — the next match in the queue belongs to somebody else.
                    Console.Error.WriteLine("[quests] match {0} was not applied for player_id={1}: {2}",
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
                    "[quests] shutdown did not wait for the quest queue to drain; " +
                    "unevaluated matches in it are dropped.");
            }
        }
    }
}
