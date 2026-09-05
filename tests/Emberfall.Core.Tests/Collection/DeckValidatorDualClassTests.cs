using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="DeckValidator"/> L1-L5 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §1, LOCKED) against the
    /// <b>live</b> M1 card set rather than synthetic rows — the same choice
    /// <c>CardValidatorTests</c> makes, and for the same reason: a rule that only holds against
    /// fixtures does not gate a Save button.
    ///
    /// <para>The dual-class rules are the point of this fixture. L4 became
    /// <c>NEUTRAL ∪ Primary ∪ Secondary</c> when the pairing ruling landed (Sam, 2026-07-26), and L5
    /// changed from "a hero class" to "exactly one Character" — the two rules that had no test
    /// because the predicate they replaced was only ever called by an editor spike.</para>
    ///
    /// <para><b>L5 changed AGAIN on 2026-09-04 (ADR-0009).</b> The pairing moved onto the deck, so
    /// "bound to exactly one Character" cannot fail — a deck cannot fail to be bound to itself. The
    /// slot is <c>L5_PairingWellFormed</c> now and asks the question the container used to answer for
    /// free: are these two classes real and distinct? The wire can supply NEUTRAL/NEUTRAL, so it is a
    /// live question and not a formality.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckValidatorDualClassTests
    {
        /// <summary>The pairing every deck below is built on. It was a <c>Character</c> holding
        /// these two classes until ADR-0009; the deck holds them itself now, so the helper returns a
        /// deck rather than something to bind one to.</summary>
        private static DeckList Gravedant(string id = "deck-1") =>
            new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = id, Name = "Test Deck" };

        /// <summary>
        /// A legal 30-card deck for a character, filled from the live pool at the maximum copies each
        /// card allows. Built rather than hardcoded so a balance patch cannot make it stale, and so a
        /// class that drops below a buildable 30 fails loudly here.
        /// </summary>
        private static DeckList LegalDeck(DeckList c, string id = "deck-1")
        {
            var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = id, Name = "Test Deck" };
            foreach (var def in LiveCardSet.Cards.Where(d => d.Collectible && deck.AllowsClass(d.Class)))
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

        private static IReadOnlyList<DeckRule> RulesIn(DeckValidation v) => v.Violations.Select(x => x.Rule).ToList();

        // ---- the happy path ------------------------------------------------------------

        [Test]
        public void A_Filled_Dual_Class_Deck_Is_Valid()
        {
            var c = Gravedant();
            var result = DeckValidator.Validate(LegalDeck(c), LiveCardSet.Db);

            Assert.That(result.IsValid, Is.True,
                "Unexpected violations: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
        }

        // ---- L1 deck size --------------------------------------------------------------

        [Test]
        public void L1_Flags_A_Deck_Under_Thirty_And_Says_How_Many_Are_Missing()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var last = deck.Entries[deck.Entries.Count - 1];
            deck.Entries[deck.Entries.Count - 1] = last.With(last.Count - 1);

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(result.IsValid, Is.False);
            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L1_Size }));
            Assert.That(result.Violations[0].Message, Does.Contain("1"), "Copy tells the player the exact shortfall.");
        }

        [Test]
        public void L1_Flags_A_Deck_Over_Thirty()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var extra = LiveCardSet.Collectible(CardClass.NEUTRAL).First(d => deck.CountOf(d.Id) == 0);
            deck.Entries.Add(new DeckEntry(extra.Id, 1));

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L1_Size }));
        }

        [Test]
        public void L1_Ceiling_Refuses_The_Thirty_First_Add()
        {
            var c = Gravedant();
            var deck = LegalDeck(c);
            var unused = LiveCardSet.Collectible(CardClass.NEUTRAL).First(d => deck.CountOf(d.Id) == 0);

            Assert.That(DeckValidator.CanAdd(deck, unused), Is.False,
                "30 is a hard ceiling — §1 prevents the 31st add rather than reporting it.");
        }

        // ---- L2 / L3 copy caps ---------------------------------------------------------

        [Test]
        public void L2_Caps_Non_Legendaries_At_Two()
        {
            var c = Gravedant();
            var common = LiveCardSet.Cards.First(d => d.Collectible && d.Class == CardClass.GRAVEKEEP && d.Rarity != Rarity.LEGENDARY);
            var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
            deck.Entries.Add(new DeckEntry(common.Id, 3));

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Does.Contain(DeckRule.L2_CopyCap));
            Assert.That(RulesIn(result), Does.Not.Contain(DeckRule.L3_LegendaryCap));

            var atCap = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
            atCap.Entries.Add(new DeckEntry(common.Id, 2));
            Assert.That(DeckValidator.CanAdd(atCap, common), Is.False, "The +copy control disables at the cap.");
        }

        [Test]
        public void L3_Caps_Legendaries_At_One()
        {
            var c = Gravedant();
            var legendary = LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP, Rarity.LEGENDARY);
            var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
            deck.Entries.Add(new DeckEntry(legendary.Id, 2));

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Does.Contain(DeckRule.L3_LegendaryCap));
            Assert.That(RulesIn(result), Does.Not.Contain(DeckRule.L2_CopyCap),
                "A legendary over-copy is L3, not L2 — the inline copy differs (\"Only 1\" vs \"Only 2\").");
        }

        // ---- L4 class membership: the dual-class rule ----------------------------------

        [Test]
        public void L4_Accepts_Neutral_And_Both_Halves_Of_The_Pairing()
        {
            var c = Gravedant();

            foreach (var cls in new[] { CardClass.NEUTRAL, CardClass.GRAVEKEEP, CardClass.VERDANT })
            {
                var card = LiveCardSet.AnyCollectible(cls);
                var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
                deck.Entries.Add(new DeckEntry(card.Id, 1));

                var result = DeckValidator.Validate(deck, LiveCardSet.Db);
                Assert.That(RulesIn(result), Does.Not.Contain(DeckRule.L4_ClassMembership),
                    $"{cls} is inside NEUTRAL ∪ {c.PrimaryClass} ∪ {c.SecondaryClass}.");
                Assert.That(DeckValidator.CanAdd(new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" }, card), Is.True);
            }
        }

        [Test]
        public void L4_Accepts_A_Deck_With_No_Card_From_The_PRIMARY_Class()
        {
            // The regression this fixture exists for. The single-class predicate this one replaced
            // compared every card against one hero class, so a deck carrying nothing but secondary
            // and neutral cards is exactly what it would have rejected — and is legal by ruling.
            //
            // Secondary-only cannot reach 30 on today's pool (VERDANT has 15 collectible rows = 29
            // copies), so neutrals fill the tail. That is the honest shape of the deck anyway.
            var c = Gravedant();
            var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
            var pool = LiveCardSet.Collectible(CardClass.VERDANT).Concat(LiveCardSet.Collectible(CardClass.NEUTRAL));
            foreach (var def in pool)
            {
                if (deck.TotalCount >= DeckValidator.DeckSize) break;
                int want = System.Math.Min(DeckValidator.MaxCopies(def.Rarity), DeckValidator.DeckSize - deck.TotalCount);
                deck.Entries.Add(new DeckEntry(def.Id, want));
            }

            Assert.That(deck.Entries.Any(e => LiveCardSet.Db.Get(e.CardId).Class == c.SecondaryClass), Is.True,
                "The deck must actually contain secondary-class cards for this to test anything.");
            Assert.That(deck.Entries.Any(e => LiveCardSet.Db.Get(e.CardId).Class == c.PrimaryClass), Is.False);

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);
            Assert.That(result.IsValid, Is.True,
                "Unexpected violations: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
        }

        [Test]
        public void L4_Rejects_A_Class_Outside_The_Pairing()
        {
            var c = Gravedant();
            var offClass = LiveCardSet.AnyCollectible(CardClass.EMBERWARD);
            var deck = LegalDeck(c);

            // Swap, not append, so the size stays legal and L4 is the only rule under test.
            var replaced = deck.Entries[0];
            deck.Entries[0] = new DeckEntry(offClass.Id, replaced.Count);

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L4_ClassMembership }));
            Assert.That(result.Violations[0].Message, Does.Contain(offClass.Name));
            Assert.That(DeckValidator.CanAdd(new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" }, offClass), Is.False,
                "§1: off-class cards are not shown as addable.");
        }

        [Test]
        public void L4_Surfaces_A_Card_That_Vanished_From_The_Pool()
        {
            // §3: a deck that became illegal after a balance patch surfaces on open. The unknown id
            // must NOT be silently dropped, and must NOT throw — DeckValidator resolves through
            // ICardDatabase.TryGet, never Get (VULN-001 §4 rule 1, the same discipline).
            var c = Gravedant();
            var deck = LegalDeck(c);
            var replaced = deck.Entries[0];
            deck.Entries[0] = new DeckEntry(FixtureCard.Id("card_deleted_by_a_balance_patch"), replaced.Count);

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(result.IsValid, Is.False);
            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L4_ClassMembership }));
            Assert.That(result.Violations[0].Message,
                Does.Contain(FixtureCard.Id("card_deleted_by_a_balance_patch").ToString()));
            Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize), "Validation never mutates the deck.");
        }

        // ---- L5 pairing well-formedness -------------------------------------------------
        //
        // This slot was L5_CharacterBound until ADR-0009 — two tests, one for a deck bound to
        // nothing and one for a deck whose character had been deleted. Neither state exists: a deck
        // carries its own pairing, so it cannot fail to be bound to itself and there is no second
        // object to go missing. What replaced them is the question the container used to answer for
        // free, and it is a live one because the WIRE can supply a pairing this type's constructor
        // would never be given by a screen.

        [Test]
        public void L5_Fires_On_A_Pairing_That_Is_Not_Two_Real_Classes()
        {
            // NEUTRAL/NEUTRAL is what an empty SubmitDeckMsg deserializes to. It is not a pairing.
            var deck = new DeckList(CardClass.NEUTRAL, CardClass.NEUTRAL) { Id = "deck-1" };

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(result.IsValid, Is.False);
            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L5_PairingWellFormed }),
                "A malformed pairing is checked FIRST and returns: every L4 message is phrased in " +
                "terms of the pairing, so reporting L4 against nonsense produces nonsense.");
        }

        [Test]
        public void L5_Fires_On_A_Class_Paired_With_Itself()
        {
            var deck = new DeckList(CardClass.GRAVEKEEP, CardClass.GRAVEKEEP) { Id = "deck-1" };

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Is.EqualTo(new[] { DeckRule.L5_PairingWellFormed }),
                "Gravekeep+Gravekeep is one class twice, not a pairing.");
        }

        [Test]
        public void A_Well_Formed_Pairing_Never_Reports_L5()
        {
            var result = DeckValidator.Validate(LegalDeck(Gravedant()), LiveCardSet.Db);

            Assert.That(RulesIn(result), Does.Not.Contain(DeckRule.L5_PairingWellFormed));
        }

        // ---- composition ---------------------------------------------------------------

        [Test]
        public void Multiple_Violations_Are_All_Reported_In_Deck_Order()
        {
            var c = Gravedant();
            var legendary = LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP, Rarity.LEGENDARY);
            var offClass = LiveCardSet.AnyCollectible(CardClass.TIDECALLER);

            var deck = new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" };
            deck.Entries.Add(new DeckEntry(legendary.Id, 2));   // L3
            deck.Entries.Add(new DeckEntry(offClass.Id, 1));    // L4

            var result = DeckValidator.Validate(deck, LiveCardSet.Db);

            Assert.That(RulesIn(result), Is.EqualTo(new[]
            {
                DeckRule.L1_Size,            // 3 cards, not 30 — reported first
                DeckRule.L3_LegendaryCap,
                DeckRule.L4_ClassMembership,
            }), "Violations are order-stable so the UI list does not jump between keystrokes.");
        }

        [Test]
        public void CanAdd_Refuses_Everything_It_Cannot_Reason_About()
        {
            var c = Gravedant();
            var card = LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP);

            // Two arguments, not three: the Character that used to sit between them is gone, and
            // with it the "no character to judge against" case. What is left is the deck and the
            // card, and a null of either is unreasonable-about.
            Assert.That(DeckValidator.CanAdd(null!, card), Is.False);
            Assert.That(DeckValidator.CanAdd(new DeckList(c.PrimaryClass, c.SecondaryClass) { Id = "d" }, null!), Is.False);
        }
    }
}
