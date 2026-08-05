using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// In-process core host for the M1 vertical slice and practice-vs-AI (ARCHITECTURE §6). Exercises
    /// the exact <c>Submit → Execute</c> path the server uses, which is precisely why the golden-replay
    /// harness drives it rather than a parallel "test-only processor" (dev-docs/quality/testing-and-ci.md:24).
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
        /// The projected wire payload (ADR-0006, WS A3). <see cref="State"/> and
        /// <see cref="OnEvents"/> are AUTHORITY-side surfaces (server, golden-replay harness):
        /// since WS A4 every in-process consumer binds to <c>IMatchSession</c> via
        /// <c>LocalMatch.Start</c> and receives only projected state/events (the a3 §9.2
        /// honest-AI gap is disfervord — §7.1 test 6 lives in Session/LocalMatchSessionTests).
        /// </summary>
        public GameState Snapshot(PlayerId viewer)
            => Projection.GameStateProjector.Project(State, viewer);
    }
}
