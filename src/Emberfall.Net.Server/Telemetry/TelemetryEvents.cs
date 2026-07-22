using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// Which sample a finished match belongs to (design/telemetry-spec.md §1). The two combine for
    /// the §3 exit bar, but they are kept separable because only <see cref="AiSoak"/> answers the
    /// fast-kill prong and only <see cref="Ladder"/> is evidence about humans.
    ///
    /// <para>Derived server-side from the seats' synthetic tokens, never declared by the client — a
    /// client that could label its own matches could label them out of the ladder sample.</para>
    /// </summary>
    public enum TelemetrySource { Ladder, AiSoak }

    /// <summary>One seat's result. Mirrors the `result` column in telemetry-spec §1's per-seat block.</summary>
    public enum SeatResult { Win, Loss, Draw }

    /// <summary>
    /// Which sample a pairing belongs to, decided from WHO is sitting there.
    ///
    /// <para>The alternative — believing the client's <c>HelloMsg.Platform</c>/<c>BuildId</c>, which
    /// <c>NetSoak</c> does set to "netsoak" — would let any client label its own matches, and the
    /// first thing a bad actor would label away is a losing ladder game. The soak's device tokens
    /// are fixed by <c>Emberfall.Tools.NetSoak</c> precisely so this classification can be derived
    /// rather than declared.</para>
    /// </summary>
    public static class TelemetrySources
    {
        /// <summary>The tokens <c>NetSoak.SoakDevice(seat)</c> mints. Kept in sync by a test.</summary>
        public const string SoakTokenSeat0 = "ai-soak-seat-0";
        public const string SoakTokenSeat1 = "ai-soak-seat-1";

        private static readonly AccountId Soak0 = AccountId.FromDeviceToken(SoakTokenSeat0);
        private static readonly AccountId Soak1 = AccountId.FromDeviceToken(SoakTokenSeat1);

        /// <summary>
        /// <see cref="TelemetrySource.AiSoak"/> only when BOTH seats are synthetic. A human paired
        /// against a soak seat is not an AI-vs-AI match, and burying that row in the soak sample
        /// would quietly delete a real ladder result.
        /// </summary>
        public static TelemetrySource Classify(AccountId p0, AccountId p1)
        {
            bool synthetic0 = p0 == Soak0 || p0 == Soak1;
            bool synthetic1 = p1 == Soak0 || p1 == Soak1;
            return synthetic0 && synthetic1 ? TelemetrySource.AiSoak : TelemetrySource.Ladder;
        }
    }

    /// <summary>
    /// Who the two seats were and what sample they belong to. Server-side only — this is the
    /// C-G1-8 attribution that must never reach <c>PlayerId</c>, the wire, or <c>GameState</c>.
    /// </summary>
    public sealed record MatchAttribution(AccountId P0, AccountId P1, TelemetrySource Source)
    {
        /// <summary>
        /// The test/pre-identity default: both seats unattributed, so the emitter drops the row
        /// rather than writing a real-looking one (see <see cref="AccountId.Unattributed"/>).
        /// </summary>
        public static MatchAttribution None { get; } =
            new MatchAttribution(AccountId.Unattributed, AccountId.Unattributed, TelemetrySource.Ladder);

        public bool IsAttributed => P0.IsAttributed && P1.IsAttributed;
    }

    /// <summary>Per-seat block of <see cref="MatchCompleteEvent"/> (telemetry-spec §1).</summary>
    public sealed record SeatOutcome(
        AccountId Account,
        CardClass Class,
        string DeckId,
        string DeckHash,
        SeatResult Result,
        IReadOnlyList<string> MulliganKept);

    /// <summary>
    /// `match_complete` — emitted once per finished match. THIS EVENT IS THE ≥200-MATCH SAMPLE the
    /// C4 exit bar is judged on (telemetry-spec §1, M2-KICKOFF §5), which is the whole reason
    /// C-G1-8 exists: match one is unrecoverable, so an unemitted match is a permanently missing
    /// row, not a deferred one.
    ///
    /// <para><b>heroId is deliberately absent.</b> Spec §1 lists `heroId` / `classId`; in B1 heroes
    /// are 1:1 with classes, so a heroId column would carry exactly the information
    /// <see cref="SeatOutcome.Class"/> already carries. The schema is additive-only
    /// (telemetry-spec §5), so adding it the day a class gets a second hero is legal and cheap;
    /// emitting a redundant column now is not.</para>
    /// </summary>
    public sealed record MatchCompleteEvent(
        MatchId Match,
        DateTimeOffset EndedAtUtc,
        MatchEndReason EndReason,
        MatchOutcome Outcome,
        int TurnCount,
        TelemetrySource Source,
        SeatOutcome P0,
        SeatOutcome P1);

    /// <summary>
    /// `match_void` — the decision-24 double-disconnect void. NOT a `match_complete` with a funny
    /// reason: a void has no winner, no loser, and must never enter the win-rate sample. It is
    /// emitted anyway because the *rate* of voids is a health metric (telemetry-spec §4's
    /// disconnect family) and because a match that vanished with no row at all is indistinguishable
    /// from telemetry being broken.
    /// </summary>
    public sealed record MatchVoidEvent(
        MatchId Match,
        DateTimeOffset EndedAtUtc,
        int TurnCount,
        TelemetrySource Source,
        AccountId P0,
        AccountId P1);
}
