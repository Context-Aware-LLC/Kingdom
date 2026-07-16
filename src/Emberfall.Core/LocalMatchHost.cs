using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// In-process core host for the M1 vertical slice and practice-vs-AI (ARCHITECTURE §6). Exercises
    /// the exact <c>Submit → Execute</c> path the server uses, which is precisely why the golden-replay
    /// harness drives it rather than a parallel "test-only processor" (golden-replay.md §2).
    /// </summary>
    public sealed class LocalMatchHost : IMatchHost
    {
        private readonly ICommandProcessor _processor;
        private readonly ICardDatabase _db;
        private readonly MatchSetup _setup;
        private GameState? _state;

        public LocalMatchHost(ICommandProcessor processor, ICardDatabase db, MatchSetup setup)
        {
            _processor = processor;
            _db = db;
            _setup = setup;
        }

        public ulong StateVersion => _state?.StateVersion ?? 0UL;

        /// <summary>The live authoritative state (read-only intent). Tests hash this after the replay.</summary>
        public GameState State => _state ?? throw new System.InvalidOperationException("BeginMatch not called.");

        public event GameEventStream? OnEvents;

        public GameState BeginMatch(MatchId id, PlayerId p0, PlayerId p1, ulong seed)
        {
            _state = MatchEngine.BeginMatch(_setup, _db, id, p0, p1, seed);
            return _state;
        }

        public CommandResult Submit(PlayerIntent intent)
        {
            if (_state is null) throw new System.InvalidOperationException("BeginMatch not called.");
            var result = _processor.Execute(_state, intent);
            if (result.Accepted && result.Events.Count > 0)
                OnEvents?.Invoke(result.Events);
            return result;
        }

        /// <summary>
        /// The projected wire payload (ADR-0006, WS A3). NOTE (a3-projector-design.md §9): in this
        /// in-process host, <see cref="State"/> and <see cref="OnEvents"/> remain RAW, unprojected
        /// paths until A4/B1 route consumers through IMatchSession/ProjectEvent — local consumers
        /// binding to them instead of Snapshot(viewer) is the tracked honest-AI gap (§7.1 test 6).
        /// </summary>
        public GameState Snapshot(PlayerId viewer)
            => Projection.GameStateProjector.Project(State, viewer);
    }
}
