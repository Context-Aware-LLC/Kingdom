using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Policy;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>
    /// A learned seat policy that degrades to Greedy LOUDLY when its scorer stops answering.
    ///
    /// WHY A WRAPPER AND NOT JUST THE CHECKPOINT POLICY. The model cannot answer every position:
    /// its encoder refuses a keyword it was never trained on, an ongoing effect from a card its
    /// vocabulary lacks, or a move wider than the export's target cap
    /// (<see cref="Kingdom.Policy.PolicyEncodingException"/>), and the runtime can fail outright. A
    /// bot seat that throws takes the match with it, which is a far worse outcome for the player
    /// than one weaker move. So the fallback exists. The DANGER is that a fallback which merely works is invisible: every
    /// bot-fill match silently becomes a Greedy match, the win rates look normal, and nobody learns
    /// that the model has not served a decision in a week.
    ///
    /// CLAUDE.md's standing lesson, from the collection-sync degrade that displaced its own error:
    /// a graceful degrade is not free, it moves the failure somewhere less legible. So this one is
    /// deliberately noisy. It writes a warning the first time, counts every fallback decision, and
    /// exposes <see cref="FallbackDecisions"/> so a caller can surface it. **Never make this
    /// quieter without giving the count somewhere else to go.**
    ///
    /// STARTUP IS A DIFFERENT CASE and is handled by the caller, not here. A checkpoint configured
    /// but unloadable is a CONFIGURATION error, and starting anyway with a silent Greedy fallback
    /// would mean an operator who asked for the learned bot never finds out they did not get it.
    /// <c>ServerApp</c> throws instead.
    /// </summary>
    internal sealed class LearnedBotSeatPolicy : IObservedSeatPolicy
    {
        private readonly IObservedSeatPolicy _learned;
        private readonly ISeatPolicy _fallback;
        private readonly Action<string> _warn;
        private readonly LearnedBotCounters? _counters;
        private int _fallbackDecisions;
        private bool _warned;

        public LearnedBotSeatPolicy(
            IObservedSeatPolicy learned,
            ISeatPolicy fallback,
            Action<string>? warn = null,
            LearnedBotCounters? counters = null)
        {
            _learned = learned ?? throw new ArgumentNullException(nameof(learned));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
            _warn = warn ?? Console.Error.WriteLine;
            _counters = counters;
        }

        /// <summary>
        /// The LEARNED identity, even after a fallback. The identity names the policy this seat was
        /// configured as, and a match that silently relabelled itself mid-game would make the
        /// fallback invisible in exactly the records used to audit it — the count is the honest
        /// signal, not a mutating label.
        /// </summary>
        public string Identity => _learned.Identity;

        /// <summary>Decisions this seat could not serve from the model. Zero is the expected value;
        /// anything else means the scorer is unwell and the bot is not what it claims to be.</summary>
        public int FallbackDecisions => Volatile.Read(ref _fallbackDecisions);

        public async ValueTask<PlayerIntent?> ChooseAsync(
            GameState state, PlayerId player, CancellationToken cancellationToken = default)
        {
            try
            {
                PlayerIntent? chosen = await _learned.ChooseAsync(state, player, cancellationToken)
                    .ConfigureAwait(false);
                // Null is "not this seat's move", which the runner polls for constantly.
                if (chosen is not null) _counters?.Served();
                return chosen;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The match is shutting down. Not a scorer failure and not something to paper over
                // with a Greedy move.
                throw;
            }
            catch (Exception ex)
            {
                int count = Interlocked.Increment(ref _fallbackDecisions);
                _counters?.FellBack();
                if (!_warned)
                {
                    _warned = true;
                    _warn($"[bots] learned policy '{_learned.Identity}' failed to answer and this "
                          + $"seat is now falling back to {GreedyAi.PolicyVersion}: "
                          + $"{ex.GetType().Name}: {ex.Message}");
                }
                else if (count % 100 == 0)
                {
                    _warn($"[bots] learned policy '{_learned.Identity}' has fallen back on "
                          + $"{count} decisions");
                }
                return await _fallback.ChooseAsync(state, player, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Both policies observe, because a fallback can be temporary. If the learned policy stopped
        /// seeing transitions the moment it first failed, its deck memory would be stale by the time
        /// the scorer recovered and it would resume play on a false picture of the game.
        /// </summary>
        public void ObserveTransition(
            GameState before, System.Collections.Generic.IReadOnlyList<GameEvent> events,
            GameState after, PlayerId player)
        {
            try
            {
                _learned.ObserveTransition(before, events, after, player);
            }
            catch (Exception ex)
            {
                // Memory tracking is pure bookkeeping over projected state; if it throws, the model
                // is unusable for the rest of the match but the seat must still finish the game.
                if (!_warned)
                {
                    _warned = true;
                    _warn($"[bots] learned policy '{_learned.Identity}' failed while observing a "
                          + $"transition: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (_fallback is IObservedSeatPolicy observed)
                observed.ObserveTransition(before, events, after, player);
        }
    }
}
