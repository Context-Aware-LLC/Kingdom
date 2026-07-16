using System;
using System.Threading.Tasks;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// The async CLIENT seam (interfaces.md v3 §2, M2 WS A4; engineering/a4-session-seam-design.md).
    /// Callers (MatchRunner, presentation, AI intent sources) bind to THIS, never to a concrete
    /// host: the local session resolves instantly, a networked session resolves on the server's
    /// reply, and neither difference leaks past the seam (decisions/ADR-0004 — confirm-then-show).
    ///
    /// Everything delivered here is PROJECTED for this session's viewer (decisions/ADR-0006):
    /// <see cref="Current"/>, <see cref="OnSnapshot"/>, <see cref="OnEvents"/>, and the
    /// <see cref="CommandResult.Events"/> inside a resolved <see cref="SubmitAsync"/> task are
    /// all post-redaction. There is no raw-state surface on this interface.
    /// </summary>
    public interface IMatchSession
    {
        ulong StateVersion { get; }

        /// <summary>Latest projected snapshot for this session's viewer.</summary>
        GameState Current { get; }

        /// <summary>
        /// Submit an intent; the task resolves when the AUTHORITY confirms (instantly for the
        /// local session). A reject resolves the task normally with the rejection — it never
        /// faults — and fires no <see cref="OnSnapshot"/> (a rejected command mutates nothing).
        /// </summary>
        Task<CommandResult> SubmitAsync(PlayerIntent intent);

        /// <summary>A fresh projected snapshot arrived (post-command / reconnect).</summary>
        event Action<GameState> OnSnapshot;

        /// <summary>Ordered projected D7 batch for presentation playback. Fires AFTER OnSnapshot:
        /// <see cref="Current"/> is already the post-command state when the batch lands, so event
        /// handlers can resolve ids against it (the old in-place-mutation coincidence, made a rule).</summary>
        event GameEventStream OnEvents;
    }
}
