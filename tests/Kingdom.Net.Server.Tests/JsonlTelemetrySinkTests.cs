using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// C-G1-8 step 3 — the rows survive (dev-docs/data/player-data-and-telemetry.md:17).
    ///
    /// Step 2 proved the events are emitted; these prove they still exist afterwards. The distinction
    /// is the entire condition: before the volume landed, telemetry died on every machine restart, so
    /// a server could emit perfectly and still read n = 0 at G2. Emission without durability is the
    /// failure this was written about, wearing a passing test.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public class JsonlTelemetrySinkTests
    {
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "kingdom-telemetry-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a leaked temp dir is not worth failing a green run */ }
        }

        // ── Durability: the whole point of step 3 ────────────────────────────────

        [Test]
        public void A_New_Sink_Over_The_Same_Dir_Appends_Rather_Than_Truncating()
        {
            // THE test. A restarted server constructs a brand-new sink against the same volume; if
            // that truncated, every restart would silently reset the sample to zero — which is
            // exactly the pre-volume behaviour this condition exists to end.
            new JsonlTelemetrySink(_dir, "local").MatchComplete(Complete("m1"));
            new JsonlTelemetrySink(_dir, "local").MatchComplete(Complete("m2"));
            new JsonlTelemetrySink(_dir, "local").MatchComplete(Complete("m3"));

            var ids = Rows().Select(r => r.GetProperty("matchId").GetString()).ToArray();
            Assert.That(ids, Is.EqualTo(new[] { "m1", "m2", "m3" }),
                "three restarts must leave three rows in order, not one row three times");
        }

        [Test]
        public void Every_Row_Is_Its_Own_Line_And_Valid_Json()
        {
            var sink = new JsonlTelemetrySink(_dir, "local");
            sink.MatchComplete(Complete("m1"));
            sink.MatchVoid(Void("m2"));
            sink.MatchComplete(Complete("m3"));

            string[] lines = File.ReadAllLines(sink.Path);
            Assert.That(lines.Length, Is.EqualTo(3), "JSONL is one event per line — a reader splits on \\n");
            foreach (string line in lines)
                Assert.DoesNotThrow(() => JsonDocument.Parse(line), "each line must parse standalone: " + line);
        }

        [Test]
        public void Concurrent_Writes_Do_Not_Interleave()
        {
            // One sink is shared by every MatchSession on the box, and sessions finish independently.
            // A torn line is unrecoverable — the row is gone AND it corrupts its neighbour.
            var sink = new JsonlTelemetrySink(_dir, "local");
            const int n = 60;

            Parallel.For(0, n, i => sink.MatchComplete(Complete("m" + i)));

            Assert.That(sink.DroppedRows, Is.Zero);
            var rows = Rows();
            Assert.That(rows.Count, Is.EqualTo(n));
            Assert.That(rows.Select(r => r.GetProperty("matchId").GetString()).Distinct().Count(),
                Is.EqualTo(n), "every match wrote exactly one intact row");
        }

        // ── Schema: telemetry-spec §1 ────────────────────────────────────────────

        [Test]
        public void MatchComplete_Carries_The_Spec_Field_Names_And_Spellings()
        {
            new JsonlTelemetrySink(_dir, "local").MatchComplete(Complete("m1"));
            var r = Rows().Single();

            Assert.That(r.GetProperty("type").GetString(), Is.EqualTo("match_complete"));
            Assert.That(r.GetProperty("matchId").GetString(), Is.EqualTo("m1"));
            Assert.That(r.GetProperty("endReason").GetString(), Is.EqualTo("concede"));
            Assert.That(r.GetProperty("winnerSeat").GetString(), Is.EqualTo("p0"));
            Assert.That(r.GetProperty("turnCount").GetInt32(), Is.EqualTo(7));
            Assert.That(r.GetProperty("source").GetString(), Is.EqualTo("ladder"));

            var p0 = r.GetProperty("players")[0];
            Assert.That(p0.GetProperty("classId").GetString(), Is.EqualTo("EMBERWARD"));
            Assert.That(p0.GetProperty("result").GetString(), Is.EqualTo("win"));
            Assert.That(p0.GetProperty("deckId").GetString(), Is.EqualTo("prebuilt-emberward"));
            Assert.That(p0.GetProperty("mulliganKept").EnumerateArray().Select(x => x.GetInt32()),
                Is.EqualTo(FixtureCard.Ids("card_a", "card_b")));
            Assert.That(r.GetProperty("players")[1].GetProperty("result").GetString(), Is.EqualTo("loss"));
        }

        [Test]
        public void A_Seat_Carries_Its_Mulligan_And_Hero_Power_Counts()
        {
            MatchCompleteEvent e = Complete("m1");
            e = e with { P0 = e.P0 with { MulliganReplaced = 2, HeroPowersUsed = 3 } };
            new JsonlTelemetrySink(_dir, "local").MatchComplete(e);

            var p0 = Rows().Single().GetProperty("players")[0];
            Assert.That(p0.GetProperty("mulliganReplaced").GetInt32(), Is.EqualTo(2));
            Assert.That(p0.GetProperty("heroPowersUsed").GetInt32(), Is.EqualTo(3));
        }

        [Test]
        public void A_Seat_Carries_Its_Findings_Counters()
        {
            MatchCompleteEvent e = Complete("m1");
            e = e with
            {
                P0 = e.P0 with
                {
                    FinalHealth = 1,
                    FatigueDraws = 2,
                    CardsBurned = 3,
                    TurnsTimedOut = 4,
                    TurnsEndedWithTenUnspent = 5,
                }
            };
            new JsonlTelemetrySink(_dir, "local").MatchComplete(e);

            var p0 = Rows().Single().GetProperty("players")[0];
            Assert.Multiple(() =>
            {
                Assert.That(p0.GetProperty("finalHealth").GetInt32(), Is.EqualTo(1));
                Assert.That(p0.GetProperty("fatigueDraws").GetInt32(), Is.EqualTo(2));
                Assert.That(p0.GetProperty("cardsBurned").GetInt32(), Is.EqualTo(3));
                Assert.That(p0.GetProperty("turnsTimedOut").GetInt32(), Is.EqualTo(4));
                Assert.That(p0.GetProperty("turnsEndedWithTenUnspent").GetInt32(), Is.EqualTo(5));
            });
        }

        [Test]
        public void Enum_Spellings_Are_Canonical_Lowercase_Not_CSharp_Member_Names()
        {
            // §5 fixes field names and type strings as additive-only. If these came from
            // ToString(), renaming a C# member would silently re-spell a pinned column.
            new JsonlTelemetrySink(_dir, "local").MatchComplete(
                Complete("m1", reason: MatchEndReason.Timeout, outcome: MatchOutcome.Draw,
                         source: TelemetrySource.AiSoak, p0Result: SeatResult.Draw, p1Result: SeatResult.Draw));

            var r = Rows().Single();
            Assert.That(r.GetProperty("endReason").GetString(), Is.EqualTo("timeout"));
            Assert.That(r.GetProperty("winnerSeat").GetString(), Is.EqualTo("draw"));
            Assert.That(r.GetProperty("source").GetString(), Is.EqualTo("ai_soak"),
                "snake_case per the spec's enum, not the C# 'AiSoak'");
        }

        [Test]
        public void A_Void_Is_A_Separate_Type_With_No_Seat_Results()
        {
            // A void has no winner and must never join the win-rate sample. Emitting per-seat
            // `result` here would make these rows look joinable.
            new JsonlTelemetrySink(_dir, "local").MatchVoid(Void("m1"));
            var r = Rows().Single();

            Assert.That(r.GetProperty("type").GetString(), Is.EqualTo("match_void"));
            Assert.That(r.TryGetProperty("winnerSeat", out _), Is.False);
            foreach (var p in r.GetProperty("players").EnumerateArray())
                Assert.That(p.ValueKind, Is.EqualTo(JsonValueKind.String),
                    "void players are bare account ids, not seat blocks with a result");
        }

        // ── card_seen: telemetry-spec §2 ─────────────────────────────────────────

        [Test]
        public void A_CardSeen_Batch_Writes_One_Line_Per_Card_Not_One_Nested_Blob()
        {
            // Batching is an I/O decision, not a schema one. The burst pays for ONE open+fsync, but
            // what lands is still the spec's one row per (match, seat, cardDefId) — anything nested
            // would force every consumer to unnest before it could group by cardDefId, which is the
            // only thing this event is ever used for.
            new JsonlTelemetrySink(_dir, "local").CardsSeen(Cards("m1", Row(FixtureCard.Id("card_a")), Row(FixtureCard.Id("card_b")), Row(FixtureCard.Id("card_c"))));

            var rows = Rows();
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows.Select(r => r.GetProperty("cardDefId").GetInt32()),
                Is.EqualTo(FixtureCard.Ids("card_a", "card_b", "card_c")), "and in the order emitted");
            foreach (var r in rows)
            {
                Assert.That(r.GetProperty("type").GetString(), Is.EqualTo("card_seen"));
                Assert.That(r.GetProperty("matchId").GetString(), Is.EqualTo("m1"));
                Assert.That(r.GetProperty("env").GetString(), Is.EqualTo("local"));
            }
        }

        [Test]
        public void A_CardSeen_Row_Carries_Every_Field_The_PerCard_Metrics_Need()
        {
            new JsonlTelemetrySink(_dir, "local").CardsSeen(Cards("m1",
                Row(FixtureCard.Id("card_a"), inDeck: true, drawn: true, kept: true, firstPlayedTurn: 3)));
            var r = Rows().Single();

            Assert.That(r.GetProperty("accountId").GetString(), Is.EqualTo(AccountId.FromDeviceToken("dev-0").Value));
            Assert.That(r.GetProperty("classId").GetString(), Is.EqualTo("EMBERWARD"));
            Assert.That(r.GetProperty("source").GetString(), Is.EqualTo("ladder"));
            Assert.That(r.GetProperty("inDeck").GetBoolean(), Is.True);
            Assert.That(r.GetProperty("drawnThisMatch").GetBoolean(), Is.True);
            Assert.That(r.GetProperty("matchWon").GetBoolean(), Is.True);
            Assert.That(r.GetProperty("keptInMulligan").GetBoolean(), Is.True);
            Assert.That(r.GetProperty("firstPlayedTurn").GetInt32(), Is.EqualTo(3));
        }

        [Test]
        public void Never_Played_Is_An_Explicit_Null_Not_A_Missing_Key()
        {
            // "Never played" is an ANSWER the average-turn-played metric divides by. A missing key
            // is indistinguishable from a field the writer forgot, and 0 would be a lie — it reads
            // as a card played on turn zero.
            new JsonlTelemetrySink(_dir, "local").CardsSeen(Cards("m1", Row(FixtureCard.Id("card_a"), firstPlayedTurn: null)));
            var r = Rows().Single();

            Assert.That(r.TryGetProperty("firstPlayedTurn", out var v), Is.True, "the key is always present");
            Assert.That(v.ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        public void A_Failed_CardSeen_Batch_Counts_Every_Row_It_Lost()
        {
            // DroppedRows is the only signal that a sample is incomplete. Counting a 40-row batch as
            // one drop would under-report the hole by a factor of forty.
            var sink = new JsonlTelemetrySink(_dir, "local");
            File.Delete(sink.Path);
            Directory.CreateDirectory(sink.Path);

            Assert.DoesNotThrow(() => sink.CardsSeen(Cards("m1", Row(FixtureCard.Id("a")), Row(FixtureCard.Id("b")), Row(FixtureCard.Id("c")), Row(FixtureCard.Id("d")))));
            Assert.That(sink.DroppedRows, Is.EqualTo(4));
        }

        [Test]
        public void An_Empty_Batch_Writes_Nothing_At_All()
        {
            var sink = new JsonlTelemetrySink(_dir, "local");
            sink.CardsSeen(Cards("m1"));

            Assert.That(sink.DroppedRows, Is.Zero);
            Assert.That(File.Exists(sink.Path) && new FileInfo(sink.Path).Length > 0, Is.False,
                "a zero-row batch must not leave a blank line for a reader to trip over");
        }

        [Test]
        public void CardSeen_Batches_From_Different_Matches_Do_Not_Interleave()
        {
            // The real concurrency risk this event introduces: one sink, many sessions, and now a
            // multi-line write. If two bursts interleaved, both matches' card samples would be
            // unreadable — and a torn line takes its neighbour with it.
            var sink = new JsonlTelemetrySink(_dir, "local");
            const int matches = 20, perMatch = 30;

            Parallel.For(0, matches, i => sink.CardsSeen(
                Cards("m" + i, Enumerable.Range(0, perMatch).Select(c => Row(FixtureCard.Id("card_") + c)).ToArray())));

            Assert.That(sink.DroppedRows, Is.Zero);
            var rows = Rows();
            Assert.That(rows.Count, Is.EqualTo(matches * perMatch));
            // Every match's block must be contiguous — a batch is written under one lock or not at all.
            var ids = rows.Select(r => r.GetProperty("matchId").GetString()).ToList();
            var blocks = new List<string?>();
            foreach (var id in ids) if (blocks.Count == 0 || blocks[^1] != id) blocks.Add(id);
            Assert.That(blocks.Count, Is.EqualTo(matches), "each match wrote one unbroken run of rows");
        }

        // ── Provenance ───────────────────────────────────────────────────────────

        [Test]
        public void Every_Row_Carries_Its_Env_Because_The_Sample_Is_Ground_On_Two_Hosts()
        {
            // The balance sample is ground locally, the durability proof runs on Fly (2026-07-22).
            // `source` separates ai_soak from ladder and says nothing about which machine wrote it.
            new JsonlTelemetrySink(_dir, "fly").MatchComplete(Complete("m1"));
            new JsonlTelemetrySink(_dir, "local").MatchVoid(Void("m2"));

            var rows = Rows();
            Assert.That(rows[0].GetProperty("env").GetString(), Is.EqualTo("fly"));
            Assert.That(rows[1].GetProperty("env").GetString(), Is.EqualTo("local"));
        }

        [Test]
        public void An_Empty_Env_Label_Is_Refused_At_Construction()
        {
            // Provenance cannot be backfilled: fail where it is fixable, not at analysis time.
            Assert.Throws<ArgumentException>(() => new JsonlTelemetrySink(_dir, ""));
            Assert.Throws<ArgumentException>(() => new JsonlTelemetrySink(_dir, "   "));
        }

        // ── Failure posture ──────────────────────────────────────────────────────

        [Test]
        public void A_Write_Failure_Is_Counted_And_Never_Thrown()
        {
            // The interface forbids throwing: the match is already over and correct, and an escaping
            // exception would take down the session pump. But a silent swallow reproduces the very
            // ambiguity this condition is about, so the drop has to be countable.
            var sink = new JsonlTelemetrySink(_dir, "local");

            // Make the target unwritable by putting a DIRECTORY where the file belongs.
            File.Delete(sink.Path);
            Directory.CreateDirectory(sink.Path);

            Assert.DoesNotThrow(() => sink.MatchComplete(Complete("m1")));
            Assert.That(sink.DroppedRows, Is.EqualTo(1));
            Assert.That(sink.LastError, Is.Not.Null, "a drop nobody can see is the failure mode, not the fix");
        }

        [Test]
        public void A_Healthy_Sink_Reports_Zero_Drops()
        {
            var sink = new JsonlTelemetrySink(_dir, "local");
            sink.MatchComplete(Complete("m1"));

            Assert.That(sink.DroppedRows, Is.Zero);
            Assert.That(sink.LastError, Is.Null);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private IReadOnlyList<JsonElement> Rows()
        {
            string path = Path.Combine(_dir, JsonlTelemetrySink.FileName);
            return File.ReadAllLines(path)
                .Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .ToList();
        }

        private static MatchCompleteEvent Complete(
            string id,
            MatchEndReason reason = MatchEndReason.Concede,
            MatchOutcome outcome = MatchOutcome.Player0Win,
            TelemetrySource source = TelemetrySource.Ladder,
            SeatResult p0Result = SeatResult.Win,
            SeatResult p1Result = SeatResult.Loss)
            => new MatchCompleteEvent(
                new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000),
                reason, outcome, 7, source,
                new SeatOutcome(AccountId.FromDeviceToken("dev-0"), CardClass.EMBERWARD,
                    "prebuilt-emberward", "hash0", p0Result, FixtureCard.Ids("card_a", "card_b")),
                new SeatOutcome(AccountId.FromDeviceToken("dev-1"), CardClass.TIDECALLER,
                    "prebuilt-tidecaller", "hash1", p1Result, Array.Empty<int>()));

        private static CardSeenEvent Cards(string id, params CardSeenRow[] rows)
            => new CardSeenEvent(
                new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000),
                TelemetrySource.Ladder, rows);

        private static CardSeenRow Row(
            int defId, bool inDeck = true, bool drawn = false, bool kept = false,
            int? firstPlayedTurn = null, bool won = true)
            => new CardSeenRow(
                AccountId.FromDeviceToken("dev-0"), CardClass.EMBERWARD, won,
                defId, inDeck, drawn, kept, firstPlayedTurn);

        private static MatchVoidEvent Void(string id)
            => new MatchVoidEvent(
                new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000),
                4, TelemetrySource.Ladder,
                AccountId.FromDeviceToken("dev-0"), AccountId.FromDeviceToken("dev-1"));
    }
}
