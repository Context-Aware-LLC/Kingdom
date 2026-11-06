using System;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="OwnershipRecord"/>, <see cref="OwnershipDigest"/> and <see cref="AtLimitOwnedCounts"/>.
    /// The digest is what the client and server compare instead of exchanging a collection, so the
    /// properties that matter are the ones where two honest sides could disagree: row order, zero rows,
    /// and a superuser record that must not depend on which cards exist today.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class OwnershipRecordTests
    {
        private static OwnedCardCounts Counts(params (int id, int copies)[] rows)
        {
            var c = new OwnedCardCounts();
            foreach (var (id, copies) in rows) c.Set(id, copies);
            return c;
        }

        private static CardDefinition Card(string mnemonic, Rarity rarity, bool collectible = true)
            => new CardDefinition(FixtureCard.Id(mnemonic), mnemonic, mnemonic, CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "", setId: SetId.CORE,
                collectible: collectible, tribe: null, factionId: "");

        [Test]
        public void Digest_IsStable_RegardlessOfInsertionOrder()
        {
            var a = OwnershipRecord.List(Counts((1, 2), (5, 1), (3, 2)));
            var b = OwnershipRecord.List(Counts((3, 2), (1, 2), (5, 1)));
            Assert.That(a.Digest, Is.EqualTo(b.Digest));
        }

        [Test]
        public void Digest_IgnoresRowsAtZeroOrBelow()
        {
            var withZero = OwnershipRecord.List(Counts((1, 2), (2, 0), (3, -1)));
            var without = OwnershipRecord.List(Counts((1, 2)));
            Assert.That(withZero.Digest, Is.EqualTo(without.Digest));
        }

        [Test]
        public void Digest_Differs_WhenOneCopyChanges()
        {
            Assert.That(OwnershipRecord.List(Counts((1, 2))).Digest,
                Is.Not.EqualTo(OwnershipRecord.List(Counts((1, 1))).Digest));
        }

        [Test]
        public void AllAtLimit_DigestIsConstant_AndDiffersFromEmptyList()
        {
            Assert.That(OwnershipDigest.Of(OwnershipRecord.AllAtLimit), Is.EqualTo(OwnershipRecord.AllAtLimit.Digest));
            Assert.That(OwnershipRecord.AllAtLimit.Digest, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(OwnershipRecord.AllAtLimit.Rule, Is.EqualTo(OwnershipRule.AllAtLimit));
            Assert.That(OwnershipRecord.AllAtLimit.Counts, Is.Null);
            Assert.That(OwnershipRecord.AllAtLimit.Digest,
                Is.Not.EqualTo(OwnershipRecord.List(new OwnedCardCounts()).Digest));
        }

        [Test]
        public void List_Resolve_ReturnsTheCounts()
        {
            var record = OwnershipRecord.List(Counts((7, 2)));
            Assert.That(record.Resolve(new CardNameIndex(Array.Empty<CardDefinition>())).CopiesOwned(7), Is.EqualTo(2));
        }

        [Test]
        public void AtLimit_OwnsTwoOfACommon_OneOfALegendary_NoneOfAToken()
        {
            var common = Card("olr_common", Rarity.COMMON);
            var legend = Card("olr_legend", Rarity.LEGENDARY);
            var token = Card("olr_token", Rarity.COMMON, collectible: false);
            var db = new CardNameIndex(new[] { common, legend, token });

            var owned = OwnershipRecord.AllAtLimit.Resolve(db);

            Assert.That(owned.CopiesOwned(common.Id), Is.EqualTo(2));
            Assert.That(owned.CopiesOwned(legend.Id), Is.EqualTo(1));
            Assert.That(owned.CopiesOwned(token.Id), Is.EqualTo(0));
            Assert.That(owned.CopiesOwned(FixtureCard.Id("olr_unknown")), Is.EqualTo(0));
        }

        [Test]
        public void AtLimit_CoversACardAddedLater_WithoutChangingTheDigest()
        {
            var early = Card("olr_early", Rarity.RARE);
            var late = Card("olr_late", Rarity.LEGENDARY);
            var before = new CardNameIndex(new[] { early });
            var after = new CardNameIndex(new[] { early, late });

            string digestBefore = OwnershipRecord.AllAtLimit.Digest;
            Assert.That(OwnershipRecord.AllAtLimit.Resolve(before).CopiesOwned(late.Id), Is.EqualTo(0));
            Assert.That(OwnershipRecord.AllAtLimit.Resolve(after).CopiesOwned(late.Id), Is.EqualTo(1));
            Assert.That(OwnershipRecord.AllAtLimit.Digest, Is.EqualTo(digestBefore));
        }

        [Test]
        public void IsWellFormed()
        {
            string good = OwnershipRecord.AllAtLimit.Digest;
            Assert.That(good.Length, Is.EqualTo(64));
            Assert.That(OwnershipDigest.IsWellFormed(good), Is.True);
            Assert.That(OwnershipDigest.IsWellFormed(null), Is.False);
            Assert.That(OwnershipDigest.IsWellFormed(good.Substring(1)), Is.False);
            Assert.That(OwnershipDigest.IsWellFormed(good.ToUpperInvariant() + "a"), Is.False);
            Assert.That(OwnershipDigest.IsWellFormed(new string('A', 64)), Is.False);
            Assert.That(OwnershipDigest.IsWellFormed(new string('a', 63) + "g"), Is.False);
        }
    }
}
