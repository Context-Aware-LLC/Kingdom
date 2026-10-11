using System;
using System.Threading.Tasks;

namespace Kingdom.Core.Contracts
{
    /// <summary>
    /// The async CLIENT seam (dev-docs/contracts/gameplay-and-session.md:5).
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

        /// <summary>Ordered projected semantic-event batch for presentation playback. Fires AFTER OnSnapshot:
        /// <see cref="Current"/> is already the post-command state when the batch lands, so event
        /// handlers can resolve ids against it (the old in-place-mutation coincidence, made a rule).</summary>
        event GameEventStream OnEvents;

        /// <summary>
        /// The authority (re)armed the active player's turn clock — on turn start, at rope, and on
        /// reconnect re-sync (a5 <c>TurnTimerMsg</c>). Presentation binds a visible countdown to
        /// this; nothing else on the seam carries wall-clock timing.
        ///
        /// NEVER fires for <see cref="LocalMatchSession"/>: hot-seat has no server clock, so the
        /// E2 visible clock is networked-only and its view is verified against a stub session that
        /// fires this event. Ownership (M2-KICKOFF §E2/§E3, resolving the code-comment migration
        /// the G1 review flagged): the <b>visible clock is E2</b> — a C1 entry requirement — while
        /// rope animation, opponent-thinking and reconnect UI are E3.
        /// </summary>
        event Action<TurnTimer> OnTurnTimer;
    }

    /// <summary>
    /// A turn-clock arming as delivered across the client seam. <see cref="DeadlineMs"/> is an
    /// ABSOLUTE deadline in the shared Unix-epoch millisecond clock both server
    /// (<c>ITimeSource</c>) and client (<c>IClientClock</c>) use, so presentation counts down with
    /// <c>remaining = DeadlineMs - clock.Now</c>. Cross-machine clock skew is cosmetic — the
    /// server is the sole timing authority (decision 23) and enforces the real deadline; skew
    /// correction is E3 hardening, not this slice.
    ///
    /// Reference type (not <c>record struct</c>) deliberately: Unity compiles the shared src as
    /// C#9 (see 0ceb191). Timer events fire a handful of times per turn, so the allocation is
    /// immaterial.
    /// </summary>
    public sealed record TurnTimer(PlayerId ActivePlayer, long DeadlineMs);
}
