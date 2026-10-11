using System;
using System.IO;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Unit")]
    public sealed class BotRosterTests
    {
        private static string WriteTemp(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "kingdom-bot-roster-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>A legal 30-card EMBERWARD/STONEWARDEN deck — 15 distinct ids at the COMMON/RARE
        /// copy cap. Numeric because a card resolves by its number now; the handles, for a reader:
        /// ember_recruit, ember_zealot, ember_flame_bolt, ember_scorch, ember_cinder_drake,
        /// ember_overheat_golem, ember_cinder_lance, neutral_stone_sentry, neutral_emberling_pup,
        /// neutral_stoneback_sentry, neutral_hollow_acolyte, neutral_aegis_templar,
        /// ember_stoke_the_coals, neutral_wandering_scout, neutral_lantern_bearer.
        /// copy cap (2 each), matching the shape DeckValidator's L1-L3 require. Kept small and
        /// hand-verified rather than reusing the real <c>tooling/data/bot-roster.json</c>, so this
        /// test file has no dependency on that file's contents.</summary>
        private const string LegalCardsJson = @"
            1001, 1001,
            1002, 1002,
            151, 151,
            153, 153,
            1003, 1003,
            1019, 1019,
            51, 51,
            1007, 1007,
            1009, 1009,
            1010, 1010,
            1011, 1011,
            1013, 1013,
            167, 167,
            1008, 1008,
            1012, 1012";

        [Test]
        public void Loads_A_Well_Formed_Roster()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""ember_knight"", ""displayName"": ""Emberkin"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var roster = BotRoster.LoadFromFile(path, db);
                Assert.That(roster, Has.Count.EqualTo(1));
                Assert.That(roster[0].AccountUsername, Is.EqualTo("ember_knight"));
                Assert.That(roster[0].DisplayName, Is.EqualTo("Emberkin"));
                Assert.That(roster[0].PrimaryClass, Is.EqualTo(CardClass.EMBERWARD));
                Assert.That(roster[0].SecondaryClass, Is.EqualTo(CardClass.STONEWARDEN));
                Assert.That(roster[0].Cards, Has.Count.EqualTo(30));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_An_Unknown_Card_Id()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""bad_bot"", ""displayName"": ""Badbot"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [7777777] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("7777777"));
                Assert.That(ex.Message, Does.Contain("bad_bot"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_An_Invalid_Class_Pairing()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""bad_bot"", ""displayName"": ""Badbot"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""EMBERWARD"",
                  ""cards"": [1001] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("bad_bot"));
                Assert.That(ex.Message, Does.Contain("pairing"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_A_Duplicate_Account_Username()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""dupe"", ""displayName"": ""Emberkin"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"", ""cards"": [" + LegalCardsJson + @"] },
                { ""accountUsername"": ""dupe"", ""displayName"": ""Saltmourn"", ""primaryClass"": ""TIDECALLER"", ""secondaryClass"": ""GRAVEKEEP"", ""cards"": [1004] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("dupe"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_A_Deck_That_Is_Not_30_Cards()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""short_deck"", ""displayName"": ""Shortdeck"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [1001, 1001, 151] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("short_deck"));
                Assert.That(ex.Message, Does.Contain("illegal deck"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_A_Null_Cards_List()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""null_cards"", ""displayName"": ""Nullcards"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": null }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("null_cards"));
                Assert.That(ex.Message, Does.Contain("no cards"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_A_Display_Name_That_Fails_PlayerName_Check()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            // Below PlayerName.MinLength (3).
            string path = WriteTemp(@"[
                { ""accountUsername"": ""ember_knight"", ""displayName"": ""ab"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("ab"));
            }
            finally { File.Delete(path); }
        }

        /// <summary>VULN-002 §4.3 reaches the roster too. A bot is the one seat that can wear a name
        /// nobody typed, so the reserved list has to be enforced on the way in — nothing downstream
        /// re-checks it, because MatchGate only inspects names that arrived from a peer.</summary>
        [Test]
        public void Rejects_A_Reserved_Display_Name()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""ember_knight"", ""displayName"": ""Opponent"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("Opponent"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Rejects_An_Entry_With_No_Display_Name()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""ember_knight"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("no displayName"));
            }
            finally { File.Delete(path); }
        }

        /// <summary>Two bots differing only in case are indistinguishable on a nameplate, which is
        /// the one surface this field exists for.</summary>
        [Test]
        public void Rejects_A_Duplicate_Display_Name_Case_Insensitively()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""a_bot"", ""displayName"": ""Emberkin"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"", ""cards"": [" + LegalCardsJson + @"] },
                { ""accountUsername"": ""b_bot"", ""displayName"": ""emberkin"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"", ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => BotRoster.LoadFromFile(path, db));
                Assert.That(ex!.Message, Does.Contain("duplicate displayName"));
            }
            finally { File.Delete(path); }
        }

        /// <summary>The whole point of the split: the username stays a key, so it is free to look
        /// like one. Only the display name has to read as a name.</summary>
        [Test]
        public void Accepts_A_Key_Shaped_Username_When_The_Display_Name_Is_A_Name()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = WriteTemp(@"[
                { ""accountUsername"": ""ember_knight_00"", ""displayName"": ""Emberkin"", ""primaryClass"": ""EMBERWARD"", ""secondaryClass"": ""STONEWARDEN"",
                  ""cards"": [" + LegalCardsJson + @"] }
            ]");
            try
            {
                var roster = BotRoster.LoadFromFile(path, db);
                Assert.That(roster[0].AccountUsername, Is.EqualTo("ember_knight_00"));
                Assert.That(roster[0].DisplayName, Is.EqualTo("Emberkin"));
            }
            finally { File.Delete(path); }
        }

        /// <summary>
        /// The SHIPPED roster — <c>tooling/data/bot-roster.json</c>, the file Dockerfile.server copies
        /// to <c>/app/bot-roster.json</c> — loads and every entry passes L1-L5.
        ///
        /// <para><b>Why this is worth a test when every other case above uses a temp file.</b> Those
        /// pin the LOADER's rules and must not depend on the roster's contents. Nothing pinned the
        /// roster itself, so its only validation was <c>ServerApp</c> at boot: an illegal decklist
        /// edited into that file is green through the whole suite, green through CI, and takes the
        /// match server's startup down on deploy. This asserts the file loads and nothing about what
        /// is in it beyond legality, so adding, retiring or rebalancing a bot never touches it.</para>
        /// </summary>
        [Test]
        public void The_Shipped_Roster_Loads_And_Every_Deck_Is_Legal()
        {
            var db = Data.FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            string path = ShippedRosterPath();

            var roster = BotRoster.LoadFromFile(path, db);   // throws on any L1-L5 or identity failure

            Assert.That(roster, Is.Not.Empty, path + " is empty.");
            foreach (var entry in roster)
                Assert.That(entry.Cards, Has.Count.EqualTo(30),
                    $"{entry.AccountUsername} holds {entry.Cards.Count} cards.");
        }

        /// <summary>Walks up from the test binary to the repo root, the same shape as
        /// <see cref="SessionHarness.CardDataDir"/> and for the same reason.</summary>
        private static string ShippedRosterPath()
        {
            string? path = NUnit.Framework.TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "tooling", "data", "bot-roster.json");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Could not locate tooling/data/bot-roster.json from the test directory.");
        }
    }
}
