using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="CharacterQuery"/> — the read side the S6a character-select panel is built on
    /// (ui/collection-deckbuilder.md §2: "list existing Characters whose Primary or Secondary is
    /// this class"), plus <see cref="CollectionPaths"/>, the storage-key convention it is scoped by.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CharacterQueryTests
    {
        private static readonly Character Ashtide = new("chr-1", "Ashtide", CardClass.EMBERWARD, CardClass.TIDECALLER);
        private static readonly Character Emberoot = new("chr-2", "Emberoot", CardClass.VERDANT, CardClass.EMBERWARD);
        private static readonly Character Rotbloom = new("chr-3", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

        private static Character[] Roster() => new[] { Ashtide, Emberoot, Rotbloom };

        [Test]
        public void ForClass_Matches_Primary_Or_Secondary()
        {
            // EMBERWARD is Ashtide's primary and Emberoot's secondary — the S6a panel shows both.
            Assert.That(CharacterQuery.ForClass(Roster(), CardClass.EMBERWARD).Select(c => c.Id),
                Is.EqualTo(new[] { "chr-1", "chr-2" }));

            Assert.That(CharacterQuery.ForClass(Roster(), CardClass.VERDANT).Select(c => c.Id),
                Is.EqualTo(new[] { "chr-2", "chr-3" }));

            Assert.That(CharacterQuery.ForClass(Roster(), CardClass.TIDECALLER).Select(c => c.Id),
                Is.EqualTo(new[] { "chr-1" }));
        }

        [Test]
        public void ForClass_Preserves_Roster_Order()
        {
            var reversed = Roster().Reverse().ToArray();
            Assert.That(CharacterQuery.ForClass(reversed, CardClass.EMBERWARD).Select(c => c.Id),
                Is.EqualTo(new[] { "chr-2", "chr-1" }),
                "The panel shows characters in stored order, not an order the filter invents.");
        }

        [Test]
        public void ForClass_Is_Empty_For_Neutral_And_For_An_Unpaired_Class()
        {
            Assert.That(CharacterQuery.ForClass(Roster(), CardClass.NEUTRAL), Is.Empty,
                "No valid pairing contains NEUTRAL, so the S6 Neutral tab has no character list.");

            var noGravekeep = new[] { Ashtide, Emberoot };
            Assert.That(CharacterQuery.ForClass(noGravekeep, CardClass.GRAVEKEEP), Is.Empty,
                "An empty result is the S6a 'Create a Character' state, not an error.");
        }

        [Test]
        public void ForPrimary_Is_Narrower_Because_Pairs_Are_Ordered()
        {
            // The distinction that matters: the primary is the one that supplies the hero power.
            Assert.That(CharacterQuery.ForPrimary(Roster(), CardClass.EMBERWARD).Select(c => c.Id),
                Is.EqualTo(new[] { "chr-1" }));
            Assert.That(CharacterQuery.ForClass(Roster(), CardClass.EMBERWARD).Count, Is.EqualTo(2));

            Assert.That(CharacterQuery.ForPrimary(Roster(), CardClass.TIDECALLER), Is.Empty,
                "Ashtide holds TIDECALLER as its SECONDARY, so it does not cast the Tidecaller power.");
        }

        [Test]
        public void ById_Resolves_The_Deck_Binding_Or_Returns_Null()
        {
            Assert.That(CharacterQuery.ById(Roster(), "chr-2")!.Name, Is.EqualTo("Emberoot"));
            Assert.That(CharacterQuery.ById(Roster(), "chr-missing"), Is.Null);
            Assert.That(CharacterQuery.ById(Roster(), ""), Is.Null);
            Assert.That(CharacterQuery.ById(Array.Empty<Character>(), "chr-1"), Is.Null);
        }

        [Test]
        public void Queries_Tolerate_A_Null_Roster()
        {
            Assert.That(CharacterQuery.ForClass(null!, CardClass.EMBERWARD), Is.Empty);
            Assert.That(CharacterQuery.ForPrimary(null!, CardClass.EMBERWARD), Is.Empty);
            Assert.That(CharacterQuery.ById(null!, "chr-1"), Is.Null);
        }

        // ---- storage-key convention ----------------------------------------------------

        [Test]
        public void SanitizeKey_Reduces_A_Key_To_One_Safe_Path_Segment()
        {
            Assert.That(CollectionPaths.SanitizeKey("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
                Is.EqualTo("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), "A UUID passes through untouched.");

            foreach (var hostile in new[] { "../../etc", "..\\..\\windows", "a/b", "a:b", "a\0b" })
            {
                string safe = CollectionPaths.SanitizeKey(hostile);
                Assert.That(safe, Does.Not.Contain(".."), $"'{hostile}' must not survive as path structure.");
                // NUL is checked by character, not by Does.Not.Contain — NUnit's substring match is
                // culture-sensitive and culture-sensitive IndexOf treats "\0" as ignorable, so every
                // string "contains" it.
                Assert.That(safe.IndexOf('\0'), Is.EqualTo(-1), $"'{hostile}' -> '{safe}'");
                foreach (char forbidden in new[] { '/', '\\', ':', '.' })
                    Assert.That(safe, Does.Not.Contain(forbidden.ToString()), $"'{hostile}' -> '{safe}'");
            }
        }

        [Test]
        public void A_Hostile_Key_Cannot_Escape_The_Collection_Root()
        {
            string root = Path.Combine(Path.GetTempPath(), "emberfall-paths-test");
            string escaped = Path.GetFullPath(CollectionPaths.Characters(root, "../../../pwned"));

            Assert.That(escaped, Does.StartWith(Path.GetFullPath(root)),
                "Today's key is a locally-minted UUID; the guard is for the day it is server-supplied.");
        }

        [Test]
        public void SanitizeKey_Rejects_An_Empty_Identity()
        {
            Assert.Throws<ArgumentException>(() => CollectionPaths.SanitizeKey(""));
            Assert.Throws<ArgumentException>(() => CollectionPaths.SanitizeKey("   "));
            Assert.Throws<ArgumentException>(() => CollectionPaths.SanitizeKey(null!));
        }

        [Test]
        public void Characters_And_Decks_Are_Separate_Files_In_One_Identity_Folder()
        {
            string root = Path.Combine(Path.GetTempPath(), "emberfall-paths-test");
            string characters = CollectionPaths.Characters(root, "token");
            string decks = CollectionPaths.Decks(root, "token");

            Assert.That(Path.GetDirectoryName(characters), Is.EqualTo(Path.GetDirectoryName(decks)));
            Assert.That(Path.GetFileName(characters), Is.EqualTo(CollectionPaths.CharactersFileName));
            Assert.That(Path.GetFileName(decks), Is.EqualTo(CollectionPaths.DecksFileName));
        }
    }
}
