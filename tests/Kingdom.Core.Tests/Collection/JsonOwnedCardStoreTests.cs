using System;
using System.IO;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The file-backed <see cref="IOwnedCardStore"/> (NOR-238).
    ///
    /// <para>Its CRUD is the least interesting thing about it. What these tests pin is the one
    /// distinction the whole ownership model rests on: <b>an absent record is not an empty one</b>. Load
    /// returns null when the file has never been written, and every L6 consumer reads that null as
    /// "ownership unknown, do not constrain". Collapsing the two — the obvious tidy-up, and the same
    /// shape every sibling store in this assembly deliberately does use — would turn every deck on every
    /// machine into an unqueueable draft on the first launch after L6 landed.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class JsonOwnedCardStoreTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-owned-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir we could not clean is not a test failure */ }
        }

        private JsonOwnedCardStore Store(string key = "device-token")
            => JsonOwnedCardStore.InDirectory(_root, key);

        // ---- the null contract ----------------------------------------------------------

        [Test]
        public void No_File_Loads_As_Null_Not_As_An_Empty_Record()
        {
            Assert.That(Store().Load(), Is.Null,
                "Null means 'nobody has told this account what it owns'. An empty record means 'owns " +
                "nothing', which makes every deck a draft. They are not interchangeable.");
        }

        [Test]
        public void An_Explicitly_Empty_Record_Round_Trips_As_Empty_Not_Null()
        {
            Store().Save(new OwnedCardCounts());

            var loaded = Store().Load();

            Assert.That(loaded, Is.Not.Null, "Saving an empty record asserts 'owns nothing' and must survive.");
            Assert.That(loaded!.RecordedCards, Is.EqualTo(0));
        }

        // ---- round trip ------------------------------------------------------------------

        [Test]
        public void Counts_Round_Trip_Through_Disk()
        {
            var owned = new OwnedCardCounts();
            owned.Set(FixtureCard.Id("card-a"), 2);
            owned.Set(FixtureCard.Id("card-b"), 1);
            owned.Set(FixtureCard.Id("card-c"), 0);
            Store().Save(owned);

            // A second instance over the same path — the state must be the file, not the object.
            var loaded = Store().Load();

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.CopiesOwned(FixtureCard.Id("card-a")), Is.EqualTo(2));
            Assert.That(loaded.CopiesOwned(FixtureCard.Id("card-b")), Is.EqualTo(1));
            Assert.That(loaded.CopiesOwned(FixtureCard.Id("card-c")), Is.EqualTo(0));
            Assert.That(loaded.CopiesOwned(FixtureCard.Id("never-granted")), Is.EqualTo(0));
            Assert.That(loaded.RecordedCards, Is.EqualTo(3),
                "A card recorded at zero is still a recorded card — every copy dusted is a fact, not an absence.");
        }

        [Test]
        public void Save_Replaces_The_Record_Rather_Than_Merging_Into_It()
        {
            var first = new OwnedCardCounts();
            first.Set(FixtureCard.Id("card-a"), 2);
            first.Set(FixtureCard.Id("card-b"), 2);
            Store().Save(first);

            var second = new OwnedCardCounts();
            second.Set(FixtureCard.Id("card-a"), 2);
            Store().Save(second);

            var loaded = Store().Load();

            Assert.That(loaded!.RecordedCards, Is.EqualTo(1),
                "Ownership is a projection of a grant history the client does not hold. Merging would " +
                "keep rows the authority has revoked, so a dusted card would stay owned forever.");
            Assert.That(loaded.CopiesOwned(FixtureCard.Id("card-b")), Is.EqualTo(0));
        }

        [Test]
        public void The_File_Is_Byte_Stable_For_The_Same_Ownership()
        {
            var inOneOrder = new OwnedCardCounts();
            inOneOrder.Set(FixtureCard.Id("zeta"), 1);
            inOneOrder.Set(FixtureCard.Id("alpha"), 2);
            inOneOrder.Set(FixtureCard.Id("mid"), 1);

            var inAnother = new OwnedCardCounts();
            inAnother.Set(FixtureCard.Id("mid"), 1);
            inAnother.Set(FixtureCard.Id("alpha"), 2);
            inAnother.Set(FixtureCard.Id("zeta"), 1);

            Assert.That(CollectionJson.WriteOwnedCards(inAnother),
                        Is.EqualTo(CollectionJson.WriteOwnedCards(inOneOrder)),
                "Rows are sorted by card id, so a collection whose contents did not change does not " +
                "produce a different file because packs were opened in a different order.");
        }

        // ---- refusing to read a broken file ---------------------------------------------

        [Test]
        public void A_Corrupt_File_Throws_Rather_Than_Reading_As_Absent()
        {
            string path = CollectionPaths.OwnedCards(_root, "device-token");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");

            Assert.Throws<CollectionFormatException>(() => Store().Load(),
                "Absent unlocks the full pool. Silently unlocking it off the back of a corrupt file is " +
                "the entitlement bypass this store exists to prevent.");
        }

        [Test]
        public void A_Negative_Count_Clamps_To_Zero()
        {
            string path = CollectionPaths.OwnedCards(_root, "device-token");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"{{ \"schemaVersion\": 1, \"ownedCards\": [ {{ \"id\": {FixtureCard.Id("card-a")}, \"copies\": -3 }} ] }}");

            // Server-side ownership is the signed sum of an append-only ledger, so a correction can
            // legitimately land below zero. "Less than none" and "none" are the same refusal.
            Assert.That(Store().Load()!.CopiesOwned(FixtureCard.Id("card-a")), Is.EqualTo(0));
        }

        [Test]
        public void Two_Accounts_On_One_Device_Own_Different_Cards()
        {
            var a = new OwnedCardCounts();
            a.Set(FixtureCard.Id("card-a"), 2);
            Store("account-a").Save(a);

            Assert.That(Store("account-b").Load(), Is.Null,
                "Ownership is scoped by identity like every other collection file — a second account on " +
                "the same device starts with no record of its own.");
            Assert.That(Store("account-a").Load()!.CopiesOwned(FixtureCard.Id("card-a")), Is.EqualTo(2));
        }
    }
}
