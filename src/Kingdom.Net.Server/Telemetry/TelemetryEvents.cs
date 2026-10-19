using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// Which sample a finished match belongs to (dev-docs/data/player-data-and-telemetry.md:17). The two combine for
    /// the §3 exit bar, but they are kept separable because only <see cref="AiSoak"/> answers the
    /// fast-kill prong and only <see cref="Ladder"/> is evidence about humans.
    ///
    /// <para>Derived server-side from the seats' synthetic tokens, never declared by the client — a
    /// client that could label its own matches could label them out of the ladder sample.</para>
    ///
    /// <para><b><see cref="BotFill"/></b> (AI opponent matchmaking fallback, 2026-08-09) is a real
    /// human paired against a server-driven bot after <c>MatchGate.TryBotFillTimedOutTickets</c>
    /// timed the human's ticket out. It is NOT <see cref="Ladder"/> — the bot's play is a fixed
    /// deck under <c>Kingdom.Net.Server.Bots.GreedyAi</c>, so folding these rows into the ladder
    /// sample would contaminate the balance data with matches that are not human-vs-human. It is
    /// also NOT <see cref="AiSoak"/> — that source means BOTH seats are synthetic soak accounts;
    /// here one seat is a real human. Filter it out of both when reading the balance dataset.</para>
    ///
    /// <para><b><see cref="Friendly"/></b> (#409) is two humans who CHOSE each other through a
    /// friend challenge. Never derived by <see cref="TelemetrySources.Classify"/>: it is set by
    /// <c>MatchGate</c> on the one path that seats a redeemed challenge ticket. It is NOT
    /// <see cref="Ladder"/>, and that is the whole mechanism of Sam's ruling (2026-10-01) that a
    /// friend match pays quests and Gold but not rank — <c>RankedMatchResult.IsEligible</c> asks
    /// for Ladder, so a Friendly result moves no rating without anything having to remember to
    /// skip it. Keep it out of the balance sample too: two friends can agree on the result.</para>
    /// </summary>
    public enum TelemetrySource { Ladder, AiSoak, BotFill, Friendly }

    /// <summary>One seat's result. Mirrors the `result` column in telemetry-spec §1's per-seat block.</summary>
    public enum SeatResult { Win, Loss, Draw }

    /// <summary>
    /// Which sample a pairing belongs to, decided from WHO is sitting there.
    ///
    /// <para>The alternative — believing the client's <c>HelloMsg.Platform</c>/<c>BuildId</c>, which
    /// <c>NetSoak</c> does set to "netsoak" — would let any client label its own matches, and the
    /// first thing a bad actor would label away is a losing ladder game. The soak's device tokens
    /// are fixed by <c>Kingdom.Tools.NetSoak</c> precisely so this classification can be derived
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
        /// <see cref="TelemetrySource.AiSoak"/> only when BOTH seats are synthetic soak accounts. A
        /// human paired against a soak seat is not an AI-vs-AI match, and burying that row in the
        /// soak sample would quietly delete a real ladder result.
        ///
        /// <para><paramref name="isBotAccount"/> is the seam <c>MatchGate</c> plumbs its live
        /// <c>IBotPool</c> through (design doc; bot-fill telemetry finding) — null (every existing
        /// caller/test) preserves the original two-way Ladder/AiSoak split exactly. When supplied
        /// and either seat is a roster bot, the pairing is <see cref="TelemetrySource.BotFill"/>
        /// UNLESS both seats are already synthetic soak accounts, which stays <see cref="TelemetrySource.AiSoak"/>
        /// — soak's own fixed device tokens are never real bot accounts, so the two conditions do
        /// not actually overlap in production, but the precedence is fixed here rather than left to
        /// evaluation order at each call site.</para>
        /// </summary>
        public static TelemetrySource Classify(AccountId p0, AccountId p1, Func<AccountId, bool>? isBotAccount = null)
        {
            bool synthetic0 = p0 == Soak0 || p0 == Soak1;
            bool synthetic1 = p1 == Soak0 || p1 == Soak1;
            if (synthetic0 && synthetic1) return TelemetrySource.AiSoak;

            if (isBotAccount is not null && (isBotAccount(p0) || isBotAccount(p1)))
                return TelemetrySource.BotFill;

            return TelemetrySource.Ladder;
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
    /// <param name="EnemyMinionsDestroyed">
    /// How many of the OPPONENT's board minions died during the match, whatever killed them (Sam,
    /// 2026-10-10 — the counter the "destroy minions" quest reads). Counted by
    /// <see cref="MatchTelemetryRecorder"/>; it is a count of deaths on the other side of the
    /// board, not an attribution of who dealt the blow, which the engine does not record. Defaults
    /// to 0 so a row written before the field existed reads as none.
    /// </param>
    /// <param name="MulliganReplaced">
    /// How many cards the seat threw back at mulligan. <see cref="MulliganKept"/> cannot answer
    /// this on its own: it lists what was kept, and the opening hand's size depends on who went
    /// first. A timed-out mulligan replaced nothing.
    /// </param>
    /// <param name="HeroPowersUsed">How many times the seat's Hero Power was accepted.</param>
    /// <param name="FinalHealth">The seat's hero Health when the match ended; 0 or below for a
    /// hero that died.</param>
    /// <param name="FatigueDraws">How many times the seat drew from an empty deck.</param>
    /// <param name="CardsBurned">How many cards the seat drew into a full hand.</param>
    /// <param name="TurnsTimedOut">How many of the seat's turns the turn clock ended.</param>
    /// <param name="TurnsEndedWithTenUnspent">How many turns the seat ended, by hand or by the
    /// clock, with 10 mana available.</param>
    /// <param name="SelfDamage">Damage the seat's own cards, hero and Hero Power dealt its own hero.</param>
    /// <param name="Overkills">Hits of 10 or more the seat's cards landed on a minion at 1 Health.</param>
    /// <param name="BigMinionsBounced">The seat's minions costing 8 or more returned from the board
    /// to hand, by anyone.</param>
    /// <param name="LegendariesLost">The seat's Legendary cards the other side took control of.</param>
    /// <param name="MostBulwarks">The most Bulwark minions the seat had on the board at once.</param>
    /// <param name="FellTo">What dealt the blow that took this seat's hero to 0 Health; None when it
    /// was never taken there, so a row written before the field existed reads as standing.</param>
    public sealed record SeatOutcome(
        AccountId Account,
        CardClass Class,
        string DeckId,
        string DeckHash,
        SeatResult Result,
        IReadOnlyList<int> MulliganKept,
        int EnemyMinionsDestroyed = 0,
        int MulliganReplaced = 0,
        int HeroPowersUsed = 0,
        int FinalHealth = 0,
        int FatigueDraws = 0,
        int CardsBurned = 0,
        int TurnsTimedOut = 0,
        int TurnsEndedWithTenUnspent = 0,
        int SelfDamage = 0,
        int Overkills = 0,
        int BigMinionsBounced = 0,
        int LegendariesLost = 0,
        int MostBulwarks = 0,
        FinalBlow FellTo = FinalBlow.None);

    /// <summary>
    /// What landed the hit that took a hero from above 0 Health to 0 or below, as seen from the hero
    /// that fell. Flags, because one blow can be several things: a Toll on a minion taken from you
    /// is <see cref="Toll"/> and <see cref="StolenMinion"/> at once.
    /// </summary>
    [Flags]
    public enum FinalBlow
    {
        None = 0,
        /// <summary>Drawing from an empty deck.</summary>
        Fatigue = 1,
        /// <summary>A spell card.</summary>
        Spell = 2,
        /// <summary>Something the fallen hero's own side controlled.</summary>
        OwnSide = 4,
        /// <summary>The other side's Hero Power.</summary>
        HeroPower = 8,
        /// <summary>A minion's Toll, rung by its death.</summary>
        Toll = 16,
        /// <summary>A minion the fallen hero's side started the match with, under the other side's control.</summary>
        StolenMinion = 32,
        /// <summary>A blow none of the others name, so a hero that fell is never <see cref="None"/>.</summary>
        Other = 64,
    }

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
    /// One `card_seen` row (telemetry-spec §2): a single (match, seat, cardDefId) triple with the
    /// flags observed that match. Duplicates collapse — a deck holding two copies of a card yields
    /// ONE row, and every flag means "any copy", because §3's per-card centralization metric is
    /// keyed on the card, not the copy.
    /// </summary>
    /// <param name="FirstPlayedTurn">
    /// Null means never played. Feeds §2's average-turn-played, which is undefined for a card that
    /// stayed in the deck — writing 0 there would drag that average toward a turn nobody played on.
    /// </param>
    public sealed record CardSeenRow(
        AccountId Account,
        CardClass Class,
        bool MatchWon,
        int CardDefId,
        bool InDeck,
        bool DrawnThisMatch,
        bool KeptInMulligan,
        int? FirstPlayedTurn);

    /// <summary>
    /// `card_seen` — the per-card sample behind `balance-guardrails §6.1` (telemetry-spec §2). All
    /// of a finished match's rows, both seats, in ONE event.
    ///
    /// <para><b>Batched on purpose.</b> A match yields ~40–60 of these (two 30-card decks, deduped)
    /// against `match_complete`'s one. Delivered row-by-row that would be 40–60 sink calls at the
    /// exact moment a match is tearing down — and for <see cref="JsonlTelemetrySink"/> 40–60
    /// open/append/fsync cycles, since that sink deliberately fsyncs every write. Batching keeps the
    /// spec's one-row-per-card SHAPE (the sink still writes one JSON object per line) while paying
    /// for it once. The burst is the reason this is a separate event and not a field on
    /// <see cref="MatchCompleteEvent"/>: a consumer computing win rates must not have to parse the
    /// card sample to get there.</para>
    ///
    /// <para><b>Emitted only for a match that ENDED.</b> A void has no winner, so
    /// <see cref="CardSeenRow.MatchWon"/> has no honest value and every win-when-drawn denominator
    /// built from it would be wrong — see <see cref="MatchVoidEvent"/>.</para>
    /// </summary>
    public sealed record CardSeenEvent(
        MatchId Match,
        DateTimeOffset EndedAtUtc,
        TelemetrySource Source,
        IReadOnlyList<CardSeenRow> Rows);

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
