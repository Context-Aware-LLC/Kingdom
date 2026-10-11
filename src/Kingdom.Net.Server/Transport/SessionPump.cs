using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Transport
{
    /// <summary>
    /// The commit-2 wrapper that gives <see cref="MatchSession.Dispatch"/> its single logical
    /// thread (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §4): one unbounded single-reader channel, one consumer
    /// task, plus the ~1 Hz clock pulse. Receive loops, timers, and the gate all Post; only the
    /// consumer ever touches the session.
    /// </summary>
    public sealed class SessionPump
    {
        private static readonly TimeSpan ClockPeriod = TimeSpan.FromSeconds(1);

        private readonly MatchSession _session;
        private readonly Channel<SessionCommand> _inbox;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public SessionPump(MatchSession session)
        {
            _session = session;
            _inbox = Channel.CreateUnbounded<SessionCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,
            });
            RunTask = Task.Run(ConsumeAsync);
            _ = Task.Run(ClockAsync);
        }

        public MatchSession Session => _session;

        /// <summary>Completes when the match has ended and the inbox has drained.</summary>
        public Task RunTask { get; }

        public void Post(SessionCommand cmd) => _inbox.Writer.TryWrite(cmd);

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var cmd in _inbox.Reader.ReadAllAsync(_cts.Token))
                {
                    _session.Dispatch(cmd);
                    if (_session.Ended)
                    {
                        _inbox.Writer.TryComplete();
                        _cts.Cancel();   // stop the clock loop
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ClockAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(ClockPeriod);
                while (await timer.WaitForNextTickAsync(_cts.Token))
                    Post(new SessionCommand.ClockTick());
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
