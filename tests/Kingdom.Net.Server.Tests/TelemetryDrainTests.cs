using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// C-G1-8 step 5 — the drain's parse layer, tested WITHOUT a database.
    ///
    /// The real risk in the drain is not the SQL; it is the coupling between the format
    /// <see cref="JsonlTelemetrySink"/> writes and the shape <see cref="TelemetryDrain"/> reads back.
    /// These tests drive that coupling directly: the sink writes real lines, and the drain's own
    /// <c>TryBuildLine</c> parses them into the exact insert commands. If someone re-spells a field on
    /// one side, a test here goes red rather than a row silently going missing in production.
    ///
    /// The live-Supabase round-trip (schema apply + insert + read back) is a separate, credential-gated
    /// manual check — it cannot run in CI and is documented in the drain's landing notes.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public class TelemetryDrainTests
    {
        private string _dir = null!;

        [SetUp]
        public void SetUp() =>
            _dir = Path.Combine(Path.GetTempPath(), "kingdom-drain-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        // ── The round-trip: sink writes, drain parses ────────────────────────────

        [Test]
        public void MatchComplete_Line_Maps_To_Raw_Match_And_Two_Seats()
        {
            var cmds = ParseFirstLineOf(sink => sink.MatchComplete(Complete("m1")));

            // raw_event + match + seat p0 + seat p1
            Assert.That(cmds.Count, Is.EqualTo(4));

            AssertRawEvent(cmds[0], "match_complete");

            var match = cmds[1];
            Assert.That(Sql(match), Does.Contain("insert into match "));
            Assert.That(Val(match, 0), Is.EqualTo("m1"));
            Assert.That(Val(match, 1), Is.EqualTo("local"));                 // env
            Assert.That(Val(match, 2), Is.EqualTo("ladder"));               // source
            Assert.That(Val(match, 3), Is.EqualTo(ExpectedEndedAt));        // ended_at (UTC)
            Assert.That(Val(match, 4), Is.EqualTo("concede"));              // end_reason
            Assert.That(Val(match, 5), Is.EqualTo("p0"));                   // winner_seat
            Assert.That(Val(match, 6), Is.EqualTo(7));                      // turn_count

            var p0 = cmds[2];
            Assert.That(Sql(p0), Does.Contain("insert into match_seat "));
            Assert.That(Val(p0, 1), Is.EqualTo("p0"));
            Assert.That(Val(p0, 2), Is.EqualTo(AccountId.FromDeviceToken("dev-0").Value));
            Assert.That(Val(p0, 3), Is.EqualTo("EMBERWARD"));
            Assert.That(Val(p0, 4), Is.EqualTo("prebuilt-emberward"));
            Assert.That(Val(p0, 5), Is.EqualTo("hash0"));
            Assert.That(Val(p0, 6), Is.EqualTo("win"));
            Assert.That(Val(p0, 7), Is.EqualTo(new[]
            {
                FixtureCard.Id("card_a").ToString(), FixtureCard.Id("card_b").ToString(),
            }));

            Assert.That(Val(cmds[3], 1), Is.EqualTo("p1"));
            Assert.That(Val(cmds[3], 6), Is.EqualTo("loss"));
            Assert.That(Val(cmds[3], 7), Is.EqualTo(Array.Empty<string>()));
        }

        [Test]
        public void Void_Line_Maps_To_A_Void_Match_And_Account_Only_Seats()
        {
            var cmds = ParseFirstLineOf(sink => sink.MatchVoid(Void("m2")));

            Assert.That(cmds.Count, Is.EqualTo(4));
            AssertRawEvent(cmds[0], "match_void");

            var match = cmds[1];
            Assert.That(Sql(match), Does.Contain("'void'"), "status is a literal, not a parameter");
            Assert.That(Val(match, 0), Is.EqualTo("m2"));
            Assert.That(Val(match, 4), Is.EqualTo(4), "the void carries turn_count as its 5th parameter");

            // Void seats: account id only, no result/class/deck (a void has no per-seat outcome).
            var p0 = cmds[2];
            Assert.That(p0.Parameters.Count, Is.EqualTo(3));
            Assert.That(Val(p0, 1), Is.EqualTo("p0"));
            Assert.That(Val(p0, 2), Is.EqualTo(AccountId.FromDeviceToken("dev-0").Value));
            Assert.That(Val(cmds[3], 2), Is.EqualTo(AccountId.FromDeviceToken("dev-1").Value));
        }

        [Test]
        public void CardSeen_Line_Maps_To_Raw_Plus_One_Card_Row()
        {
            var cmds = ParseFirstLineOf(sink => sink.CardsSeen(Cards("m1",
                Row(FixtureCard.Id("card_a"), inDeck: true, drawn: true, kept: true, firstPlayedTurn: 3, won: true))));

            Assert.That(cmds.Count, Is.EqualTo(2));
            AssertRawEvent(cmds[0], "card_seen");

            var card = cmds[1];
            Assert.That(Sql(card), Does.Contain("insert into card_seen "));
            Assert.That(Val(card, 0), Is.EqualTo("m1"));
            Assert.That(Val(card, 1), Is.EqualTo("local"));                 // env
            Assert.That(Val(card, 2), Is.EqualTo("ladder"));               // source
            Assert.That(Val(card, 3), Is.EqualTo(ExpectedEndedAt));
            Assert.That(Val(card, 4), Is.EqualTo(AccountId.FromDeviceToken("dev-0").Value));
            Assert.That(Val(card, 5), Is.EqualTo("EMBERWARD"));
            Assert.That(Val(card, 6), Is.EqualTo(FixtureCard.Id("card_a").ToString()),
                "card_def_id is text, so a numeric card id is stored in its decimal form.");
            Assert.That(Val(card, 7), Is.True);                            // in_deck
            Assert.That(Val(card, 8), Is.True);                            // drawn_this_match
            Assert.That(Val(card, 9), Is.True);                            // match_won
            Assert.That(Val(card, 10), Is.True);                           // kept_in_mulligan
            Assert.That(Val(card, 11), Is.EqualTo(3));                     // first_played_turn
        }

        [Test]
        public void Never_Played_Maps_To_A_Null_First_Played_Turn()
        {
            // The sink writes an explicit JSON null; the drain must carry it as a SQL null, not 0 — a
            // 0 reads as "played on turn zero" and drags the average-turn-played metric down.
            var cmds = ParseFirstLineOf(sink => sink.CardsSeen(Cards("m1", Row(FixtureCard.Id("card_a"), firstPlayedTurn: null))));
            Assert.That(cmds[1].Parameters[11].Value, Is.EqualTo(DBNull.Value));
        }

        // ── Failure posture: a poison line is skipped, never fatal ───────────────

        [Test]
        public void A_Line_That_Will_Not_Parse_Is_Reported_As_Skipped_With_No_Commands()
        {
            var into = new List<NpgsqlBatchCommand>();
            Assert.That(TelemetryDrain.TryBuildLine("{ this is not json", "uid", into), Is.False);
            Assert.That(into, Is.Empty, "a poison line contributes nothing — it cannot half-write");
        }

        [Test]
        public void An_Unknown_Type_Still_Lands_In_Raw_Event_And_Nothing_Typed()
        {
            // Additive-schema forward-compat: a row type this drain has never heard of must still be
            // preserved verbatim in raw_event (the reprojection lever) rather than dropped.
            var into = new List<NpgsqlBatchCommand>();
            Assert.That(TelemetryDrain.TryBuildLine("{\"type\":\"future_event\",\"x\":1}", "uid", into), Is.True);
            Assert.That(into.Count, Is.EqualTo(1));
            AssertRawEvent(into[0], "future_event");
        }

        // ── Connection string normalization (Supabase URI → Npgsql key-value) ────

        [Test]
        public void A_Supabase_Pooler_Uri_Becomes_An_Npgsql_Key_Value_String_With_Ssl()
        {
            string outp = TelemetryDrain.NormalizeConnString(
                "postgresql://postgres.abcdef:sekret@aws-0-us-east-1.pooler.supabase.com:5432/postgres");
            var b = new NpgsqlConnectionStringBuilder(outp);

            Assert.That(b.Host, Is.EqualTo("aws-0-us-east-1.pooler.supabase.com"));
            Assert.That(b.Port, Is.EqualTo(5432));
            Assert.That(b.Username, Is.EqualTo("postgres.abcdef"));
            Assert.That(b.Password, Is.EqualTo("sekret"));
            Assert.That(b.Database, Is.EqualTo("postgres"));
            Assert.That(b.SslMode, Is.EqualTo(SslMode.Require), "Supabase requires SSL");
        }

        [Test]
        public void Percent_Encoded_Password_Characters_Are_Decoded()
        {
            string outp = TelemetryDrain.NormalizeConnString(
                "postgresql://user:p%40ss%3Aword%2Fx@host.example.com:5432/postgres");
            Assert.That(new NpgsqlConnectionStringBuilder(outp).Password, Is.EqualTo("p@ss:word/x"));
        }

        [Test]
        public void An_Unescaped_Colon_Or_At_In_The_Password_Does_Not_Break_Parsing()
        {
            // System.Uri throws "Invalid port specified" on these; generated DB passwords have them
            // constantly, so the drain parses the URI structurally instead of trusting Uri.
            string outp = TelemetryDrain.NormalizeConnString(
                "postgresql://postgres.abcdef:p@ss:w0rd@aws-0-us-east-1.pooler.supabase.com:5432/postgres");
            var b = new NpgsqlConnectionStringBuilder(outp);

            Assert.That(b.Host, Is.EqualTo("aws-0-us-east-1.pooler.supabase.com"));
            Assert.That(b.Port, Is.EqualTo(5432));
            Assert.That(b.Username, Is.EqualTo("postgres.abcdef"));
            Assert.That(b.Password, Is.EqualTo("p@ss:w0rd"), "everything after the first ':' before the host is the password");
            Assert.That(b.Database, Is.EqualTo("postgres"));
        }

        [Test]
        public void An_Already_KeyValue_String_Is_Passed_Through_Untouched()
        {
            const string kv = "Host=foo;Username=bar;Password=baz;Database=db";
            Assert.That(TelemetryDrain.NormalizeConnString(kv), Is.EqualTo(kv));
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // The instant the builders stamp on every event, as the drain will hand it to Postgres (UTC).
        private static readonly DateTime ExpectedEndedAt =
            DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000).UtcDateTime;

        /// <summary>Write via a real sink, then parse the first line back through the drain.</summary>
        private List<NpgsqlBatchCommand> ParseFirstLineOf(Action<JsonlTelemetrySink> emit)
        {
            var sink = new JsonlTelemetrySink(_dir, "local");
            emit(sink);
            string line = File.ReadAllLines(sink.Path).First(l => l.Length > 0);

            var into = new List<NpgsqlBatchCommand>();
            Assert.That(TelemetryDrain.TryBuildLine(line, "uid-" + Guid.NewGuid().ToString("N"), into), Is.True,
                "a line the sink just wrote must never read back as poison");
            return into;
        }

        private static void AssertRawEvent(NpgsqlBatchCommand cmd, string type)
        {
            Assert.That(Sql(cmd), Does.Contain("insert into raw_event "));
            Assert.That(cmd.Parameters[1].Value, Is.EqualTo(type));
            Assert.That(cmd.Parameters[2].NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Jsonb),
                "the verbatim line is stored as jsonb, the reprojection lever");
        }

        private static string Sql(NpgsqlBatchCommand cmd) => cmd.CommandText;
        private static object Val(NpgsqlBatchCommand cmd, int i) => cmd.Parameters[i].Value!;

        private static MatchCompleteEvent Complete(string id)
            => new MatchCompleteEvent(
                new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000),
                MatchEndReason.Concede, MatchOutcome.Player0Win, 7, TelemetrySource.Ladder,
                new SeatOutcome(AccountId.FromDeviceToken("dev-0"), CardClass.EMBERWARD,
                    "prebuilt-emberward", "hash0", SeatResult.Win, FixtureCard.Ids("card_a", "card_b")),
                new SeatOutcome(AccountId.FromDeviceToken("dev-1"), CardClass.TIDECALLER,
                    "prebuilt-tidecaller", "hash1", SeatResult.Loss, Array.Empty<int>()));

        private static CardSeenEvent Cards(string id, params CardSeenRow[] rows)
            => new CardSeenEvent(new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000), TelemetrySource.Ladder, rows);

        private static CardSeenRow Row(int defId, bool inDeck = true, bool drawn = false,
            bool kept = false, int? firstPlayedTurn = null, bool won = true)
            => new CardSeenRow(AccountId.FromDeviceToken("dev-0"), CardClass.EMBERWARD, won,
                defId, inDeck, drawn, kept, firstPlayedTurn);

        private static MatchVoidEvent Void(string id)
            => new MatchVoidEvent(new MatchId(id),
                DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000), 4, TelemetrySource.Ladder,
                AccountId.FromDeviceToken("dev-0"), AccountId.FromDeviceToken("dev-1"));
    }
}
