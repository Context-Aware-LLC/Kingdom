using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;

namespace Emberfall.Core
{
    /// <summary>
    /// In-process match construction — the ONLY blessed way to stand up a local match above the
    /// core (a4-session-seam-design.md §3). Creates the host + processor, runs BeginMatch, and
    /// returns one <see cref="IMatchSession"/> per viewer seat. Nothing this returns can reach
    /// raw authoritative state: both sessions deliver projections only, which is what discharges
    /// the a3 §9.2 honest-AI gap (a3-projector-design.md §7.1 test 6).
    /// </summary>
    public static class LocalMatch
    {
        /// <summary>
        /// Start a local match and get the per-viewer session pair. <paramref name="seed"/> 0
        /// means "mint one" (a4 design §4 — the old MatchRunner convenience relocated here);
        /// golden/replay callers always pass an explicit seed and drive LocalMatchHost directly.
        /// </summary>
        public static (IMatchSession p0, IMatchSession p1) Start(
            ICardDatabase db, MatchSetup setup, PlayerId p0, PlayerId p1,
            ulong seed = 0, MatchId? id = null)
        {
            if (seed == 0)
            {
                // Non-deterministic BY DESIGN: this is the casual "just start a match" door.
                var bytes = Guid.NewGuid().ToByteArray();
                seed = BitConverter.ToUInt64(bytes, 0) | 1UL;   // never 0
            }

            var matchId = id ?? new MatchId(Guid.NewGuid().ToString("N"));
            var processor = new CommandProcessor(db);
            var host = new LocalMatchHost(processor, db, setup);
            host.BeginMatch(matchId, p0, p1, seed);

            var coordinator = new Coordinator(host, processor);
            var s0 = new LocalMatchSession(coordinator, p0, 0);
            var s1 = new LocalMatchSession(coordinator, p1, 1);
            coordinator.Attach(s0, s1);
            return (s0, s1);
        }

        /// <summary>
        /// Shared authority-side plumbing behind the session pair. Holds the CONCRETE
        /// CommandProcessor on purpose (a4 design §11 finding 1): the emit-time visibility
        /// sidecar (<see cref="CommandProcessor.LastEventVisibility"/>) is not on
        /// ICommandProcessor and is overwritten by the next Execute, so each accepted command
        /// is projected for BOTH viewers synchronously inside the submit, before any other
        /// submit can run. Single-threaded by contract (Unity main thread / test thread).
        /// </summary>
        internal sealed class Coordinator
        {
            private readonly LocalMatchHost _host;
            private readonly CommandProcessor _processor;
            private LocalMatchSession[] _sessions = Array.Empty<LocalMatchSession>();

            internal Coordinator(LocalMatchHost host, CommandProcessor processor)
            {
                _host = host;
                _processor = processor;
            }

            internal void Attach(params LocalMatchSession[] sessions)
            {
                _sessions = sessions;
                foreach (var s in sessions)
                    s.Refresh(_host.Snapshot(s.Viewer));
            }

            internal CommandResult Submit(PlayerIntent intent, int submitterSeat)
            {
                var raw = _host.Submit(intent);
                if (!raw.Accepted)
                    return raw;   // no events, no mutation — safe to hand back as-is

                // Capture the sidecar NOW (index-parallel to raw.Events; clobbered next Execute).
                var vis = _processor.LastEventVisibility;

                // Project events per viewer, then push snapshots BEFORE events (IMatchSession
                // contract): a subscriber resolving event ids against session.Current must see
                // the post-command state.
                var projected = new IReadOnlyList<GameEvent>[_sessions.Length];
                for (int seat = 0; seat < _sessions.Length; seat++)
                {
                    var list = new List<GameEvent>(raw.Events.Count);
                    for (int i = 0; i < raw.Events.Count; i++)
                        list.Add(GameStateProjector.ProjectEvent(raw.Events[i], seat, vis[i]));
                    projected[seat] = list;
                }

                foreach (var s in _sessions)
                    s.Refresh(_host.Snapshot(s.Viewer));
                for (int seat = 0; seat < _sessions.Length; seat++)
                    _sessions[seat].Deliver(projected[seat]);

                // The submitter's result carries THEIR projected batch (a4 design §3 — callers
                // must not be able to dodge redaction by reading the result instead of OnEvents).
                return CommandResult.Ok(projected[submitterSeat], raw.StateVersion);
            }
        }
    }

    /// <summary>
    /// The local <see cref="IMatchSession"/>: resolves <see cref="SubmitAsync"/> synchronously
    /// (already-completed task) and delivers only projected state/events for its viewer.
    /// </summary>
    public sealed class LocalMatchSession : IMatchSession
    {
        private readonly LocalMatch.Coordinator _coordinator;
        private readonly int _seat;
        private GameState _current = null!;

        internal LocalMatchSession(LocalMatch.Coordinator coordinator, PlayerId viewer, int seat)
        {
            _coordinator = coordinator;
            Viewer = viewer;
            _seat = seat;
        }

        public PlayerId Viewer { get; }

        public ulong StateVersion => _current.StateVersion;

        public GameState Current => _current;

        public event Action<GameState>? OnSnapshot;
        public event GameEventStream? OnEvents;

        // Hot-seat is the authority in-process with no turn clock: there is no server deadline to
        // report, so this never fires (IMatchSession.OnTurnTimer contract). The E2 visible clock is
        // networked-only; its view is exercised against a stub session, not this one.
        public event Action<TurnTimer>? OnTurnTimer;

        public Task<CommandResult> SubmitAsync(PlayerIntent intent)
            => Task.FromResult(_coordinator.Submit(intent, _seat));

        /// <summary>Swap in a fresh projection. OnSnapshot fires only when it replaces a prior one
        /// (the constructor-time initial state is read via <see cref="Current"/>, not an event).</summary>
        internal void Refresh(GameState projected)
        {
            bool initial = _current is null;
            _current = projected;
            if (!initial) OnSnapshot?.Invoke(projected);
        }

        internal void Deliver(IReadOnlyList<GameEvent> projectedBatch)
        {
            if (projectedBatch.Count > 0) OnEvents?.Invoke(projectedBatch);
        }
    }
}
