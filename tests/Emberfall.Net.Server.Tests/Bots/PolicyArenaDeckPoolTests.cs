using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emberfall.Core;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Tests.Support;
using Emberfall.Tools.NetSoak;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Unit")]
    public sealed class PolicyArenaDeckPoolTests
    {
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "policy-arena-decks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void Load_pins_each_ordered_pair_to_its_primary_class_seat()
        {
            FileCardDatabase db = SessionHarness.Db;
            int[] emberward = WriteDeck(db, CardClass.EMBERWARD, CardClass.TIDECALLER, seed: 11);
            int[] tidecaller = WriteDeck(db, CardClass.TIDECALLER, CardClass.EMBERWARD, seed: 22);

            PolicyArenaDeckPool pool = PolicyArenaDeckPool.Load(
                _directory,
                db,
                new[] { CardClass.TIDECALLER, CardClass.EMBERWARD });
            MatchSetup setup = pool.Resolve(CardClass.EMBERWARD, CardClass.TIDECALLER);

            Assert.Multiple(() =>
            {
                Assert.That(setup.P0Class, Is.EqualTo(CardClass.EMBERWARD));
                Assert.That(setup.P1Class, Is.EqualTo(CardClass.TIDECALLER));
                Assert.That(setup.P0Deck, Is.EqualTo(emberward));
                Assert.That(setup.P1Deck, Is.EqualTo(tidecaller));
                Assert.That(pool.SourceDirectory, Is.EqualTo(Path.GetFullPath(_directory)));
                Assert.That(pool.Sha256, Does.Match("^[0-9a-f]{64}$"));
            });
        }

        [Test]
        public void Load_refuses_a_pool_without_the_reciprocal_ordered_pair()
        {
            FileCardDatabase db = SessionHarness.Db;
            WriteDeck(db, CardClass.EMBERWARD, CardClass.TIDECALLER, seed: 11);

            Assert.That(
                () => PolicyArenaDeckPool.Load(
                    _directory,
                    db,
                    new[] { CardClass.EMBERWARD, CardClass.TIDECALLER }),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("TIDECALLER/EMBERWARD"));
        }

        private int[] WriteDeck(
            FileCardDatabase db,
            CardClass primary,
            CardClass secondary,
            ulong seed)
        {
            DeckList deck = DefaultDeckGenerator.Generate(
                primary,
                secondary,
                db,
                new DeterministicRandom(seed),
                $"test-{primary}-{secondary}");
            var payload = new
            {
                primaryClass = primary.ToString(),
                secondaryClass = secondary.ToString(),
                deckId = deck.Id,
                seed,
                entries = deck.Entries.Select(entry => new { id = entry.CardId, count = entry.Count }),
            };
            string path = Path.Combine(_directory, $"{primary}_{secondary}_{seed}.deck.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
            return deck.Entries.SelectMany(entry => Enumerable.Repeat(entry.CardId, entry.Count)).ToArray();
        }
    }
}
