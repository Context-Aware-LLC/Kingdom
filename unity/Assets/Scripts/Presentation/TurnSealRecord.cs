using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>A receipt belongs to the turn after the timeout, whichever seat owns it.
    /// Snapshots arrive before events; a reconnect snapshot alone never invents a timeout.</summary>
    public sealed class TurnSealRecord
    {
        private int _turn;
        private bool _playing;
        private ulong _lastTimeout;
        private bool _seenTimeout;
        public bool ApprovedByDefault { get; private set; }
        public ulong Revision { get; private set; }

        public void OnStateUpdated(GameState state)
        {
            bool playing = state != null && state.Phase == TurnPhase.Main
                && state.Outcome == MatchOutcome.InProgress;
            int turn = state == null ? 0 : state.TurnNumber;
            if (!playing || _turn != turn)
            {
                ApprovedByDefault = false;
                Revision++;
            }
            _turn = turn;
            _playing = playing;
        }

        public bool OnEvent(GameEvent e)
        {
            if (!_playing || e.Kind != GameEventKind.TurnEnded || !e.TimedOut
                || (_seenTimeout && e.Sequence <= _lastTimeout)) return false;
            _seenTimeout = true;
            _lastTimeout = e.Sequence;
            ApprovedByDefault = true;
            Revision++;
            return true;
        }
    }
}
