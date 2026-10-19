using System;
using System.IO;
using NUnit.Framework;
using Kingdom.Core.Collection;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The display-name half of guest identity (accounts posture decision 6a).
    ///
    /// <para>The behaviours worth pinning are the ones a caller would otherwise re-derive: first run
    /// is an unnamed profile and not an error, a corrupt file is an error and not an unnamed profile,
    /// the stored form is the normalized one, and the character classes a nameplate must never be
    /// handed are refused on the way in.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class PlayerProfileTests
    {
        private string _root = null!;
        private const string Key = "device-token-abc";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "kingdom-profile-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir we could not remove fails nothing */ }
        }

        private JsonProfileStore Store() => JsonProfileStore.InDirectory(_root, Key);

        // ---- name rules ----------------------------------------------------------------

        [TestCase("Sam")]
        [TestCase("Sam Nordaware")]
        [TestCase("x_y-z'w.")]
        [TestCase("Ember1")]
        [TestCase("SixteenCharsXYZW")]   // exactly MaxLength = 16
        public void Check_accepts_a_reasonable_name(string raw)
        {
            var check = PlayerName.Check(raw);
            Assert.That(check.IsValid, Is.True, check.Reason);
            Assert.That(check.Normalized, Is.EqualTo(raw));
        }

        [Test]
        public void Check_trims_and_collapses_whitespace_rather_than_refusing()
        {
            var check = PlayerName.Check("   Sam    the   Ember  ");
            Assert.That(check.IsValid, Is.True, check.Reason);
            Assert.That(check.Normalized, Is.EqualTo("Sam the Ember"));
        }

        [Test]
        public void Check_measures_length_after_normalizing()
        {
            // Seven characters typed, three stored — the player is judged on what gets stored.
            Assert.That(PlayerName.IsValid("  Sam  "), Is.True);
            // And the cap is on the stored value too: 17 real characters is still too long.
            Assert.That(PlayerName.IsValid(" ThrivingGravekeeper "), Is.False);
        }

        [TestCase("", "empty")]
        [TestCase("   ", "whitespace only")]
        [TestCase("ab", "too short")]
        [TestCase("SeventeenCharsXYZ", "too long")]
        [TestCase("...", "punctuation only — no letter or digit")]
        [TestCase("Sam<script>", "disallowed punctuation")]
        [TestCase("Sam\u0007bell", "control character")]
        [TestCase("Sam\u202Eeht", "bidi override — invisible, and a spoofing primitive")]
        [TestCase("Sam\u200Db", "zero-width joiner")]
        [TestCase("Sam\u00ADdash", "soft hyphen — a format char that renders as nothing")]
        public void Check_refuses(string raw, string why)
        {
            var check = PlayerName.Check(raw);
            Assert.That(check.IsValid, Is.False, $"expected refusal ({why})");
            Assert.That(check.Reason, Is.Not.Empty, "a refusal with no reason has nothing to render");
            Assert.That(check.Normalized, Is.Empty, "an invalid name must not hand back a value to store");
        }

        [Test]
        public void Check_never_throws_on_null()
        {
            Assert.That(PlayerName.Check(null!).IsValid, Is.False);
        }

        [TestCase("You")]
        [TestCase("Opponent")]
        [TestCase("AI Opponent")]
        [TestCase("Unnamed player")]
        [TestCase("Kingdom")]
        [TestCase("SYSTEM")]
        [TestCase("system")]              // case is not an escape hatch
        [TestCase("  OPPONENT  ")]        // nor is whitespace the normalizer would strip anyway
        [TestCase("unnamed   player")]    // nor is a whitespace run that collapses onto a reserved name
        public void Check_refuses_a_name_our_own_chrome_authors(string raw)
        {
            // VULN-002 §4.3. Every character in these is legal, so no character-class rule above can
            // reach them — a nameplate reading "SYSTEM" is an impersonation primitive made entirely
            // of allowed letters. This is also the one refusal that is not about the name being
            // unusable, which is why it carries its own message.
            var check = PlayerName.Check(raw);
            Assert.That(check.IsValid, Is.False, $"'{raw}' impersonates our own UI");
            Assert.That(check.Reason, Does.Contain("reserved"));
            Assert.That(check.Normalized, Is.Empty);
        }

        [TestCase("King", "Tuesday")]
        [TestCase("herald", "mispronounce")]
        [TestCase("JESTER", "not amused")]
        [TestCase("Queen", "Pick another name")]
        [TestCase("Council", "Pick another name")]
        public void A_court_name_is_reserved_and_says_why(string raw, string because)
        {
            var check = PlayerName.Check(raw);
            Assert.That(check.IsValid, Is.False);
            Assert.That(check.Reason, Does.Contain("reserved").And.Contain(because));
        }

        [Test]
        public void A_reserved_word_INSIDE_a_name_is_still_allowed()
        {
            // The rule is "could this be mistaken for the game talking", not "does it contain a
            // banned substring". "Kingdom Sam" is nobody's chrome, and a substring ban would be
            // both wider than the threat and trivially worked around.
            Assert.That(PlayerName.IsValid("Kingdom Sam"), Is.True);
            Assert.That(PlayerName.IsValid("NotYou"), Is.True);
            Assert.That(PlayerName.IsReserved("Kingdom Sam"), Is.False);
            Assert.That(PlayerName.IsReserved("SYSTEM"), Is.True);
        }

        [Test]
        public void Check_allows_non_latin_scripts()
        {
            // Letters are letters. Impersonation via confusables is explicitly NOT this validator's
            // job (there is no uniqueness namespace to impersonate within yet) — see PlayerName.
            Assert.That(PlayerName.IsValid("Дмитрий"), Is.True);
            Assert.That(PlayerName.IsValid("さくら"), Is.True);
            Assert.That(PlayerName.IsValid("José"), Is.True);
        }

        // ---- store ---------------------------------------------------------------------

        [Test]
        public void Save_overwrites_rather_than_accumulating()
        {
            var store = Store();
            store.Save(new PlayerProfile("deck-1"));
            store.Save(new PlayerProfile("deck-2"));
            Assert.That(store.Load().LastPlayedDeckId, Is.EqualTo("deck-2"));
        }

        [Test]
        public void Load_on_a_corrupt_file_throws_rather_than_reporting_unnamed()
        {
            // Reporting an empty profile would let the next write overwrite a recoverable one.
            var store = Store();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{ this is not json");
            Assert.Throws<CollectionFormatException>(() => store.Load());
        }

        [Test]
        public void Load_refuses_a_newer_schema_version()
        {
            var store = Store();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath,
                $"{{ \"schemaVersion\": {CollectionJson.SchemaVersion + 1}, \"profile\": {{ \"displayName\": \"Sam\" }} }}");
            Assert.Throws<CollectionFormatException>(() => store.Load());
        }

        [Test]
        public void ReadProfile_ignores_a_display_name_written_before_it_moved_to_the_identity()
        {
            var profile = CollectionJson.ReadProfile(
                $"{{ \"schemaVersion\": {CollectionJson.SchemaVersion}, \"profile\": {{ \"displayName\": \"Sam\", \"lastPlayedDeckId\": \"deck-1\" }} }}");
            Assert.That(profile.LastPlayedDeckId, Is.EqualTo("deck-1"));
            Assert.That(CollectionJson.WriteProfile(profile), Does.Not.Contain("displayName"),
                "the next write drops it");
        }

        [Test]
        public void ReadProfile_refuses_a_file_with_no_profile_object()
        {
            Assert.Throws<CollectionFormatException>(() => CollectionJson.ReadProfile(
                $"{{ \"schemaVersion\": {CollectionJson.SchemaVersion} }}"));
        }

        // ---- scoping -------------------------------------------------------------------

        [Test]
        public void Profile_lives_beside_the_roster_in_the_same_identity_scope()
        {
            string profile = CollectionPaths.Profile(_root, Key);
            string characters = CollectionPaths.Characters(_root, Key);
            Assert.That(Path.GetDirectoryName(profile), Is.EqualTo(Path.GetDirectoryName(characters)));
            Assert.That(Path.GetFileName(profile), Is.EqualTo(CollectionPaths.ProfileFileName));
        }

        [Test]
        public void Two_identities_hold_two_profiles()
        {
            JsonProfileStore.InDirectory(_root, "device-a").Save(new PlayerProfile("deck-a"));
            JsonProfileStore.InDirectory(_root, "device-b").Save(new PlayerProfile("deck-b"));

            Assert.That(JsonProfileStore.InDirectory(_root, "device-a").Load().LastPlayedDeckId, Is.EqualTo("deck-a"));
            Assert.That(JsonProfileStore.InDirectory(_root, "device-b").Load().LastPlayedDeckId, Is.EqualTo("deck-b"));
        }

        // ---- last-played pointer (Sam, 2026-08-09: Find Game pre-arms its pick from this) ----
        //
        // IT WAS A PAIR OF IDS UNTIL ADR-0009 -- lastPlayedCharacterId beside lastPlayedDeckId --
        // and the character half went with the type. The deck carries the pairing now, so the deck
        // id is the whole of the selection and there is no second field that could disagree with it.

        [Test]
        public void Load_on_first_run_has_no_last_played_pointer()
        {
            var profile = Store().Load();
            Assert.That(profile.LastPlayedDeckId, Is.Empty);
        }

        [Test]
        public void Save_then_load_round_trips_the_last_played_pointer()
        {
            Store().Save(new PlayerProfile("deck-1"));

            var loaded = Store().Load();
            Assert.That(loaded.LastPlayedDeckId, Is.EqualTo("deck-1"));
        }

        [Test]
        public void ReadProfile_treats_a_missing_last_played_pointer_as_never_played()
        {
            var profile = CollectionJson.ReadProfile(
                $"{{ \"schemaVersion\": {CollectionJson.SchemaVersion}, \"profile\": {{ \"displayName\": \"Sam\" }} }}");
            Assert.That(profile.LastPlayedDeckId, Is.Empty);
        }

        [Test]
        public void A_Profile_Written_Before_ADR_0009_Loads_And_Drops_The_Character_Half()
        {
            // Every profile.json on every device carries lastPlayedCharacterId. An unknown field must
            // not be a read error -- unlike a deck's missing pairing, nothing here is unsafe to
            // ignore: the deck id beside it is still the whole answer, and the worst case is a pick
            // that is not pre-armed once.
            var profile = CollectionJson.ReadProfile(
                $"{{ \"schemaVersion\": {CollectionJson.SchemaVersion}, \"profile\": " +
                "{ \"displayName\": \"Sam\", \"lastPlayedCharacterId\": \"chr-1\", " +
                "\"lastPlayedDeckId\": \"deck-1\" } }");

            Assert.That(profile.LastPlayedDeckId, Is.EqualTo("deck-1"));
        }
    }
}
