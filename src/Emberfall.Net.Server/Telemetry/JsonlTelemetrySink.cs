using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// The telemetry buffer (C-G1-8 step 3; dev-docs/data/player-data-and-telemetry.md:17). Appends one canonical JSON object per
    /// line to a single file under the data dir — on Fly, the `emberfall_data` volume at `/data`,
    /// the same volume that already holds replays.
    ///
    /// <para><b>This is the crash-safe BUFFER, not the durability boundary — corrected 2026-07-22.</b>
    /// The paragraphs below argue the fsync keeps a row from dying on a machine swap, and that still
    /// holds — but a Fly volume is single-machine and un-replicated, so it was never the actual
    /// durability guarantee. <see cref="TelemetryDrain"/> ships every line off-box into durable Postgres
    /// (hosted Supabase); THAT is where a row's permanence lives. The fsync here only has to keep the
    /// buffer intact until the drain has shipped it. See the project-memory decision
    /// `telemetry-durability-supabase-decision`.</para>
    ///
    /// <para><b>One rolling file, not one per match.</b> Replays are per-match because you read
    /// exactly one of them. This event *is* the ≥200-match sample (§1) and every consumer reads all
    /// of it, so 200 files would be worse at every step — glob, merge, and the "did I miss one?"
    /// question that a single append-only log answers by construction.</para>
    ///
    /// <para><b>The write is synchronous and fsync'd, which the interface's "must not block" allows
    /// on purpose.</b> That contract exists so a slow sink cannot stall the session pump for every
    /// other match on the box — and it is written about *frequency*. `match_complete` fires ONCE per
    /// finished match, not per action, so this is one small append per several minutes of play. A
    /// background queue would buy microseconds of dispatch latency and pay for them with a window of
    /// rows sitting in RAM — rows that a `fly deploy` machine swap (which REPLACES the machine, see
    /// `fly.toml`) would take with it. Losing telemetry to a restart is the exact failure this whole
    /// condition was written about, so the trade runs the other way here: block for a millisecond,
    /// never hold an unwritten row.</para>
    ///
    /// <para><b>Never throws.</b> Per the interface contract, and because <c>MatchSession.Emit</c>
    /// already treats a telemetry failure as a dropped sample rather than a dropped match. A full
    /// disk or a missing mount must not end a game that finished correctly.</para>
    ///
    /// <para><b>Failures are counted, not silent.</b> <see cref="DroppedRows"/> and
    /// <see cref="LastError"/> exist because the premise of this condition is that n=0 and
    /// "telemetry is broken" are indistinguishable from the outside. A sink that swallows errors
    /// *and* says nothing reproduces that ambiguity one layer down.</para>
    ///
    /// <para><b>No `schemaVersion` field, deliberately.</b> §5 makes the schema additive-only, so
    /// the `type` discriminator plus optional-new-fields is sufficient to read any row ever written.
    /// C-G1-7 landed the counter-example: the replay fixtures' `schemaVersion` v1 "was written and
    /// never read" — dead weight that still had to be maintained. A version field earns its place
    /// when something branches on it; this one would not.</para>
    /// </summary>
    public sealed class JsonlTelemetrySink : ITelemetrySink
    {
        /// <summary>Default file name under the data dir.</summary>
        public const string FileName = "telemetry.jsonl";

        private readonly object _gate = new object();
        private readonly string _env;

        /// <summary>Full path of the file being appended to.</summary>
        public string Path { get; }

        /// <summary>Rows that could not be written. Non-zero means the sample is incomplete.</summary>
        public int DroppedRows { get; private set; }

        /// <summary>The most recent write failure, or null if there has never been one.</summary>
        public Exception? LastError { get; private set; }

        /// <param name="dataDir">Directory to write into; created if absent.</param>
        /// <param name="env">
        /// Which host produced these rows — `fly` or `local`. Not cosmetic: the balance sample is
        /// deliberately ground locally while the durability proof runs on Fly (decision 2026-07-22),
        /// so a merged file has rows from two machines. `source` separates ai_soak from ladder but
        /// says nothing about *where*, and provenance is impossible to backfill once the rows exist.
        /// </param>
        public JsonlTelemetrySink(string dataDir, string env)
        {
            if (string.IsNullOrWhiteSpace(env))
                throw new ArgumentException("An env label is required; provenance cannot be backfilled.", nameof(env));

            _env = env;
            Directory.CreateDirectory(dataDir);
            Path = System.IO.Path.Combine(dataDir, FileName);
        }

        public void MatchComplete(MatchCompleteEvent e)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("type"); w.Str("match_complete");
            w.Name("env"); w.Str(_env);
            w.Name("matchId"); w.Str(e.Match.Value);
            w.Name("endedAtUtc"); w.Str(Timestamp(e.EndedAtUtc));
            w.Name("endReason"); w.Str(EndReason(e.EndReason));
            w.Name("winnerSeat"); w.Str(WinnerSeat(e.Outcome));
            w.Name("turnCount"); w.I32(e.TurnCount);
            w.Name("source"); w.Str(Source(e.Source));
            w.Name("players");
            w.BeginArray();
            WriteSeat(w, e.P0);
            WriteSeat(w, e.P1);
            w.EndArray();
            w.EndObject();
            Append(w);
        }

        public void MatchVoid(MatchVoidEvent e)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("type"); w.Str("match_void");
            w.Name("env"); w.Str(_env);
            w.Name("matchId"); w.Str(e.Match.Value);
            w.Name("endedAtUtc"); w.Str(Timestamp(e.EndedAtUtc));
            w.Name("turnCount"); w.I32(e.TurnCount);
            w.Name("source"); w.Str(Source(e.Source));
            // Accounts only: a void has no per-seat result, and inventing `result: "draw"` here
            // would make these rows look joinable into the win-rate sample they must stay out of.
            w.Name("players");
            w.BeginArray();
            w.Str(e.P0.Value);
            w.Str(e.P1.Value);
            w.EndArray();
            w.EndObject();
            Append(w);
        }

        /// <summary>
        /// The §2 per-card sample. <b>One JSON object per LINE per card</b> — the spec's row shape,
        /// and what every consumer of a `.jsonl` file expects — but <b>one open/append/fsync for the
        /// whole burst</b>. Doing it per row would mean 40–60 fsyncs per match; doing it as a single
        /// nested object would force an analyst to unnest before they could group by `cardDefId`,
        /// which is the only thing anybody does with this event.
        ///
        /// <para>An empty batch writes nothing rather than opening the file to write zero bytes —
        /// and cannot happen for a real match, since both decks are always in the ledger.</para>
        /// </summary>
        public void CardsSeen(CardSeenEvent e)
        {
            var rows = e.Rows;
            if (rows.Count == 0) return;

            var lines = new List<byte[]>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                // A fresh writer per row rather than a Reset() on the shared one: JsonWriter is the
                // wire's canonical emitter and this is not a reason to grow its surface.
                var w = new JsonWriter();
                w.BeginObject();
                w.Name("type"); w.Str("card_seen");
                w.Name("env"); w.Str(_env);
                w.Name("matchId"); w.Str(e.Match.Value);
                w.Name("endedAtUtc"); w.Str(Timestamp(e.EndedAtUtc));
                w.Name("source"); w.Str(Source(e.Source));
                w.Name("accountId"); w.Str(r.Account.Value);
                w.Name("classId"); w.Str(r.Class.ToString());
                w.Name("cardDefId"); w.I32(r.CardDefId);
                w.Name("inDeck"); w.Bool(r.InDeck);
                w.Name("drawnThisMatch"); w.Bool(r.DrawnThisMatch);
                w.Name("matchWon"); w.Bool(r.MatchWon);
                w.Name("keptInMulligan"); w.Bool(r.KeptInMulligan);
                // Written as an explicit null, not omitted: "never played" is an ANSWER that the
                // average-turn-played metric divides by, and a missing key is indistinguishable
                // from a field this writer forgot.
                w.Name("firstPlayedTurn");
                if (r.FirstPlayedTurn is int t) w.I32(t); else w.Null();
                w.EndObject();
                lines.Add(w.ToUtf8());
            }
            AppendAll(lines);
        }

        private static void WriteSeat(JsonWriter w, SeatOutcome s)
        {
            w.BeginObject();
            w.Name("accountId"); w.Str(s.Account.Value);
            w.Name("classId"); w.Str(s.Class.ToString());
            w.Name("deckId"); w.Str(s.DeckId);
            w.Name("deckHash"); w.Str(s.DeckHash);
            w.Name("result"); w.Str(Result(s.Result));
            w.Name("mulliganKept");
            w.BeginArray();
            IReadOnlyList<int> kept = s.MulliganKept;
            for (int i = 0; i < kept.Count; i++) w.I32(kept[i]);
            w.EndArray();
            w.EndObject();
        }

        /// <summary>
        /// Append one row, then fsync. <c>Flush(true)</c> rather than a plain flush because the
        /// failure we are insuring against is the machine going away — a row sitting in the OS page
        /// cache is not on the volume, and `fly deploy` swaps the machine out from under it.
        /// </summary>
        private void Append(JsonWriter w) => AppendAll(new[] { w.ToUtf8() });

        /// <summary>
        /// Append N rows under ONE open and ONE fsync. A partial write — disk full halfway through a
        /// `card_seen` burst — counts every row in the batch as dropped, including any that reached
        /// the file: the batch is one match's card sample, and a half-written sample would be worse
        /// than a missing one, because it looks complete to whoever groups by `cardDefId`.
        /// </summary>
        private void AppendAll(IReadOnlyList<byte[]> rows)
        {
            try
            {
                lock (_gate)
                {
                    using var s = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        s.Write(rows[i], 0, rows[i].Length);
                        s.WriteByte((byte)'\n');
                    }
                    s.Flush(true);
                }
            }
            catch (Exception ex)
            {
                // Swallow per the ITelemetrySink contract — but count it, so an incomplete sample
                // is a number somebody can read rather than a silence indistinguishable from n=0.
                lock (_gate)
                {
                    DroppedRows += rows.Count;
                    LastError = ex;
                }
            }
        }

        // ── Canonical enum spellings (telemetry-spec §1) ──────────────────────────
        //
        // Mapped explicitly rather than via ToString(). Two reasons: the spec's spellings are
        // lowercase/snake and the C# members are not, and an explicit map means adding or renaming
        // an enum member cannot silently re-spell a field that §5 has frosted as additive-only.

        private static string Timestamp(DateTimeOffset t) =>
            t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        private static string EndReason(MatchEndReason r) => r switch
        {
            MatchEndReason.Win => "win",
            MatchEndReason.Concede => "concede",
            MatchEndReason.Timeout => "timeout",
            MatchEndReason.Disconnect => "disconnect",
            _ => "unknown",
        };

        private static string WinnerSeat(MatchOutcome o) => o switch
        {
            MatchOutcome.Player0Win => "p0",
            MatchOutcome.Player1Win => "p1",
            MatchOutcome.Draw => "draw",
            // InProgress reaching here would mean EndMatch ran on a live match — a bug worth being
            // able to see in the data rather than one rounded off to a plausible value.
            _ => "unknown",
        };

        private static string Result(SeatResult r) => r switch
        {
            SeatResult.Win => "win",
            SeatResult.Loss => "loss",
            SeatResult.Draw => "draw",
            _ => "unknown",
        };

        private static string Source(TelemetrySource s) => s switch
        {
            TelemetrySource.AiSoak => "ai_soak",
            TelemetrySource.BotFill => "bot_fill",
            _ => "ladder",
        };
    }
}
