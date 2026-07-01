using System;
using System.Collections;
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// Unity-side session owner (ARCHITECTURE §6 LocalMatchHost path). Owns a LocalMatchHost,
    /// bridges the two IIntentSources to it each frame via a coroutine, and fires presentation
    /// events on every state change. Swapped out for NetworkedMatchHost in M2 without touching
    /// any callers — they all speak through the events below.
    /// </summary>
    public sealed class MatchRunner : MonoBehaviour
    {
        /// <summary>Fires for every accepted command's event batch, in emission order.</summary>
        public event Action<IReadOnlyList<GameEvent>> OnMatchEvent;

        /// <summary>Fires after every successful state mutation (accepted command or match start).</summary>
        public event Action<GameState> OnStateUpdated;

        /// <summary>Fires once when the match reaches a terminal outcome.</summary>
        public event Action<MatchOutcome> OnMatchEnded;

        private LocalMatchHost _host;
        private IIntentSource[] _sources; // [0]=P0, [1]=P1
        private bool _running;

        public GameState State => _host != null ? _host.State : null;

        /// <summary>
        /// Wire and start a local match. Broadcasts the initial state immediately, then pumps
        /// intents each frame via a coroutine until the match ends.
        /// </summary>
        public void StartMatch(
            ICardDatabase db,
            MatchSetup setup,
            IIntentSource p0Source,
            IIntentSource p1Source,
            ulong seed = 0)
        {
            _sources = new IIntentSource[] { p0Source, p1Source };

            if (seed == 0)
                seed = (ulong)UnityEngine.Random.Range(1, int.MaxValue);

            var processor = new CommandProcessor(db);
            _host = new LocalMatchHost(processor, db, setup);
            _host.OnEvents += events => OnMatchEvent?.Invoke(events);

            var state = _host.BeginMatch(
                new MatchId(Guid.NewGuid().ToString("N")),
                new PlayerId("P0"),
                new PlayerId("P1"),
                seed);

            _running = true;
            NotifySources(state);
            OnStateUpdated?.Invoke(state);
            StartCoroutine(MatchLoop());
        }

        private IEnumerator MatchLoop()
        {
            while (_running)
            {
                var state = _host.State;

                if (state.Outcome != MatchOutcome.InProgress)
                {
                    _running = false;
                    OnMatchEnded?.Invoke(state.Outcome);
                    yield break;
                }

                var source = PickSource(state);
                if (source == null || !source.TryGetIntent(out var intent))
                {
                    yield return null;
                    continue;
                }

                var result = _host.Submit(intent);
                if (!result.Accepted)
                    source.OnRejected(result.Reason.Value);

                var newState = _host.State;
                NotifySources(newState);
                OnStateUpdated?.Invoke(newState);
                yield return null;
            }
        }

        private void NotifySources(GameState state)
        {
            foreach (var s in _sources)
                s.NotifyStateUpdate(state);
        }

        private IIntentSource PickSource(GameState state)
        {
            if (state.Phase == TurnPhase.Mulligan)
            {
                if (!state.Mulligan0Done) return _sources[0];
                if (!state.Mulligan1Done) return _sources[1];
                return null;
            }
            return _sources[state.SeatOf(state.ActivePlayer)];
        }

        private void OnDestroy()
        {
            _running = false;
        }
    }
}
