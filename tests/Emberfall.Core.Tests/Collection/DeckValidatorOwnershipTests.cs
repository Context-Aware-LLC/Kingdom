using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="DeckValidator"/> L6 — "you own at least the copies used" (NOR-238) — against the live
    /// card set, the same choice <see cref="DeckValidatorDualClassTests"/> makes.
    ///
    /// <para>Two properties matter more here than the rule itself, and each has a test that fails loudly
    /// rather than a comment:</para>
    ///
    /// <list type="number">
    /// <item><b>A null ownership source does not constrain.</b> Every deck on every machine today is
    /// validated with no ownership record, and if L6 read that as "owns nothing" the whole game would
    /// become undeckable on the commit that landed it.</item>
    /// <item><b>An unowned deck stays savable and only loses the queue.</b> Legality gates
    /// queue-eligibility, never Save (§1, Sam 2026-07-26) — a card dusted out from under a saved deck
    /// must leave the deck intact.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckValidatorOwnershipTests
    {
        private static Character Gravedant() =>
            new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

        /// <summary>A legal 30 for a character, at max copies per card — the shape L6 is applied on top of.</summary>
        private static DeckList LegalDeck(Character c, string id = "deck-1")
        {
            var deck = new DeckList { Id = id, CharacterId = c.Id, Name = "Test Deck" };
            foreach (var def in LiveCardSet.Cards.Where(d => d.Collectible && c.AllowsClass(d.Class)))
            {
                if (deck.TotalCount >= DeckValidator.DeckSize) break;
                int want = System.Math.Min(DeckValidator.MaxCopies(def.Rarity),
                                           DeckValidator.DeckSize - deck.TotalCount);
                deck.Entries.Add(new DeckEntry(def.Id, want));
            }

            Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize),
                $"Live pool cannot fill a legal 30 for {c.PrimaryClass}/{c.SecondaryClass} — content bug, not a test bug.");
            return deck;
        }

        /// <summary>An ownership record that exactly covers a deck — every card at the copies it uses.</summary>
        private static OwnedCardCounts Exactly(DeckList deck)
        {
            var owned = new OwnedCardCounts();
            foreach (var e in deck.Entries) owned.Set(e.CardId, e.Count);
            return owned;
        }

        private static IReadOnlyList<DeckRule> RulesIn(DeckValidation v) => v.Violations.Select(x => x.Rule).ToList();

        private static CardDefinition AnyCollectibleFor(Character c) =>
            LiveCardSet.Cards.First(d => d.Collectible && c.AllowsClass(d.Class));

        // ---- the null contract: no record means no constraint ---------------------------

        [Test]
        public void No_Ownership_Record_Does_Not_Constrain_Anything()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);

            // Both spellings of "no record": the parameter omitted, and passed explicitly as null.
            Assert.That(DeckValidator.Validate(deck, c, LiveCardSet.Db).IsValid, Is.True);
            Assert.That(DeckValidator.Validate(deck, c, LiveCardSet.Db, null).IsValid, Is.True,
                "A null ownership source must mean 'ownership unknown, do not constrain'. Reading it as " +
                "'owns nothing' would make every deck on every machine illegal the day L6 landed.");
        }

        [Test]
        public void An_Empty_Record_Is_Not_The_Same_As_No_Record()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);

            var withNone = DeckValidator.Validate(deck, c, LiveCardSet.Db, new OwnedCardCounts());

            Assert.That(withNone.IsValid, Is.False,
                "An empty-but-present record asserts the player owns nothing, which is a real state and " +
                "not the same as having no record at all.");
            Assert.That(RulesIn(withNone), Has.All.EqualTo(DeckRule.L6_Ownership));
        }

        // ---- the rule ------------------------------------------------------------------

        [Test]
        public void Owning_Exactly_The_Copies_Used_Is_Legal()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);

            var result = DeckValidator.Validate(deck, c, LiveCardSet.Db, Exactly(deck));

            Assert.That(result.IsValid, Is.True,
                "Unexpected violations: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
        }

        [Test]
        public void Owning_More_Than_The_Copies_Used_Is_Legal()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var owned = Exactly(deck);
            foreach (var e in deck.Entries) owned.Set(e.CardId, e.Count + 5);

            Assert.That(DeckValidator.Validate(deck, c, LiveCardSet.Db, owned).IsValid, Is.True);
        }

        [Test]
        public void One_Copy_Short_Reports_L6_And_Says_How_Many_You_Have()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var owned = Exactly(deck);

            // Pick an entry using two copies so "you own 1 of X" is the message, not "you don't own X".
            var twoCopies = deck.Entries.First(e => e.Count >= 2);
            owned.Set(twoCopies.CardId, twoCopies.Count - 1);
            LiveCardSet.Db.TryGet(twoCopies.CardId, out var def);

            var result = DeckValidator.Validate(deck, c, LiveCardSet.Db, owned);

            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L6_Ownership }));
            Assert.That(result.Violations[0].Message, Is.EqualTo($"You own {twoCopies.Count - 1} of {def.Name}."));
        }

        [Test]
        public void Owning_None_Says_So_Rather_Than_Reporting_Zero()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var owned = Exactly(deck);
            var first = deck.Entries[0];
            owned.Set(first.CardId, 0);
            LiveCardSet.Db.TryGet(first.CardId, out var def);

            var result = DeckValidator.Validate(deck, c, LiveCardSet.Db, owned);

            Assert.That(result.Violations[0].Message, Is.EqualTo($"You don't own {def.Name}."),
                "'You own 0 of X' is a sentence no human writes.");
        }

        [Test]
        public void L6_And_The_Copy_Cap_Are_Reported_Independently()
        {
            var c = Gravedant();
            var deck = new DeckList { Id = "deck-1", CharacterId = c.Id, Name = "Over" };
            var def = AnyCollectibleFor(c);
            var owned = new OwnedCardCounts();
            owned.Set(def.Id, 1);

            // Three copies of a card you own one of: over the format cap AND past what you hold.
            deck.Entries.Add(new DeckEntry(def.Id, 3));

            var rules = RulesIn(DeckValidator.Validate(deck, c, LiveCardSet.Db, owned));

            Assert.That(rules, Does.Contain(DeckRule.L6_Ownership));
            Assert.That(rules, Does.Contain(def.Rarity == Rarity.LEGENDARY
                ? DeckRule.L3_LegendaryCap
                : DeckRule.L2_CopyCap),
                "Both statements are true and each is the reason for a different fix. Folding them " +
                "together prints one number that is right for one reason and wrong for the other.");
        }

        [Test]
        public void L6_Never_Consults_Anything_But_The_Copy_Count()
        {
            // Rotation is not ownership (card-id permanence, github issue #86). Every collectible card
            // in the live set, owned at one copy, in a one-card deck: not one of them may be refused for
            // any reason to do with its set, its rarity or its age. If a format axis ever lands, it
            // lands as a rule of its own and this test is what stops it landing inside L6.
            var c = Gravedant();
            foreach (var def in LiveCardSet.Cards.Where(d => d.Collectible && c.AllowsClass(d.Class)))
            {
                var deck = new DeckList { Id = "deck-1", CharacterId = c.Id, Name = "One card" };
                deck.Entries.Add(new DeckEntry(def.Id, 1));
                var owned = new OwnedCardCounts();
                owned.Set(def.Id, 1);

                var l6 = DeckValidator.Validate(deck, c, LiveCardSet.Db, owned)
                    .Violations.Where(v => v.Rule == DeckRule.L6_Ownership).ToList();

                Assert.That(l6, Is.Empty, $"{def.Id} ({def.Name}) is owned at 1 copy and used once, yet L6 refused it.");
            }
        }

        // ---- CanAdd ---------------------------------------------------------------------

        [Test]
        public void CanAdd_Refuses_A_Copy_You_Cannot_Back_With_A_Card()
        {
            var c = Gravedant();
            var deck = new DeckList { Id = "deck-1", CharacterId = c.Id, Name = "Empty" };
            var def = AnyCollectibleFor(c);

            var ownNone = new OwnedCardCounts();
            var ownOne = new OwnedCardCounts();
            ownOne.Set(def.Id, 1);

            Assert.That(DeckValidator.CanAdd(deck, c, def), Is.True, "no record: unconstrained");
            Assert.That(DeckValidator.CanAdd(deck, c, def, ownNone), Is.False, "owns none");
            Assert.That(DeckValidator.CanAdd(deck, c, def, ownOne), Is.True, "owns one, using none");

            deck.Entries.Add(new DeckEntry(def.Id, 1));
            Assert.That(DeckValidator.CanAdd(deck, c, def, ownOne), Is.False, "owns one, already using it");
        }

        // ---- CopyLimit: what the deckbuilder's fade reads --------------------------------

        [Test]
        public void CopyLimit_Is_The_Format_Cap_When_Ownership_Is_Unknown()
        {
            foreach (var r in new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                Assert.That(DeckValidator.CopyLimit(r, null, "any-card"), Is.EqualTo(DeckValidator.MaxCopies(r)));
        }

        [Test]
        public void CopyLimit_Is_The_Lower_Of_The_Format_Cap_And_What_You_Own()
        {
            var owned = new OwnedCardCounts();
            owned.Set("none", 0);
            owned.Set("one", 1);
            owned.Set("plenty", 9);

            Assert.That(DeckValidator.CopyLimit(Rarity.COMMON, owned, "none"), Is.EqualTo(0));
            Assert.That(DeckValidator.CopyLimit(Rarity.COMMON, owned, "one"), Is.EqualTo(1));
            Assert.That(DeckValidator.CopyLimit(Rarity.COMMON, owned, "plenty"), Is.EqualTo(2),
                "owning nine does not raise the format cap");
            Assert.That(DeckValidator.CopyLimit(Rarity.LEGENDARY, owned, "plenty"), Is.EqualTo(1));
            Assert.That(DeckValidator.CopyLimit(Rarity.COMMON, owned, "never-seen"), Is.EqualTo(0),
                "a card absent from a present record is owned at zero");
        }

        // ---- the two things NOR-238 said not to break ------------------------------------

        [Test]
        public void A_Deck_That_Becomes_Unowned_Stops_Being_Queue_Eligible()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var owned = Exactly(deck);

            Assert.That(QueueEligibility.IsEligible(deck, c, LiveCardSet.Db, owned), Is.True);

            // ... and then a copy is dusted.
            var first = deck.Entries[0];
            owned.Set(first.CardId, first.Count - 1);

            var check = QueueEligibility.Check(deck, c, LiveCardSet.Db, owned);
            Assert.That(check.IsEligible, Is.False);
            Assert.That(check.IsDraft, Is.True, "It is a draft — a legitimate saved state, denied only the queue.");
            Assert.That(check.Reason, Is.Not.Empty, "The row has to be able to say why.");
        }

        [Test]
        public void An_Unownable_Deck_Is_Still_A_Savable_Draft()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            int before = deck.TotalCount;
            var entriesBefore = deck.Entries.Select(e => (e.CardId, e.Count)).ToList();

            // Validation is the only thing that judges a deck, and it must not repair one.
            DeckValidator.Validate(deck, c, LiveCardSet.Db, new OwnedCardCounts());
            QueueEligibility.Check(deck, c, LiveCardSet.Db, new OwnedCardCounts());

            Assert.That(deck.TotalCount, Is.EqualTo(before));
            Assert.That(deck.Entries.Select(e => (e.CardId, e.Count)).ToList(), Is.EqualTo(entriesBefore),
                "Legality gates the queue, never the deck (collection-deckbuilder-spec §1, Sam 2026-07-26). " +
                "A deck that stops being ownable must survive intact and become eligible again when the " +
                "copies come back.");
        }

        // ---- AllCardsOwnedCounts: the superuser source, not the null contract -----------

        [Test]
        public void AllCardsOwnedCounts_Never_Reports_L6_For_Any_Live_Card()
        {
            // Distinct from "no record" — this IS a supplied source, just one that always answers
            // "plenty". Every collectible card, at both copy caps, must never trip L6 under it.
            foreach (var def in LiveCardSet.Cards.Where(d => d.Collectible))
            {
                int max = DeckValidator.MaxCopies(def.Rarity);
                Assert.That(DeckValidator.CopyLimit(def.Rarity, AllCardsOwnedCounts.Instance, def.Id),
                    Is.EqualTo(max), $"{def.Id} should still be capped by the format, not by ownership.");
            }
        }

        [Test]
        public void AllCardsOwnedCounts_Passes_L6_On_A_Full_Legal_Deck()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);

            var result = DeckValidator.Validate(deck, c, LiveCardSet.Db, AllCardsOwnedCounts.Instance);

            Assert.That(result.IsValid, Is.True,
                "Unexpected violations: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
        }
    }
}
