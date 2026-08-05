using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="QueueEligibility"/> — the one named gate between a saved deck and the queue
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §1: legality gates queueing, not saving).
    ///
    /// <para>The property that matters most here is not any single verdict but that there is only
    /// <b>one</b> of them: the deck-pick chooser's greyed row and match-join's rejection must never be
    /// able to disagree, which is what an open-coded <c>Validate().IsValid</c> at each call site would
    /// eventually allow.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class QueueEligibilityTests
    {
        private static Character Rotbloom(string defaultDeckId = "") =>
            new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, defaultDeckId);

        private static DeckList Legal(Character c, string id = "deck-legal", ulong seed = 7) =>
            DefaultDeckGenerator.Generate(c, LiveCardSet.Db, new DeterministicRandom(seed), id);

        // ---- the gate ------------------------------------------------------------------

        [Test]
        public void A_Legal_Deck_Is_Eligible_And_Has_No_Reason()
        {
            var character = Rotbloom();
            var check = QueueEligibility.Check(Legal(character), character, LiveCardSet.Db);

            Assert.That(check.IsEligible, Is.True);
            Assert.That(check.IsDraft, Is.False);
            Assert.That(check.Reason, Is.Empty, "An eligible deck has nothing to say in the row's inline reason.");
        }

        [Test]
        public void A_Short_Deck_Is_A_Draft_And_Carries_The_Validators_Copy_Verbatim()
        {
            var character = Rotbloom();
            var draft = new DeckList { Id = "deck-1", CharacterId = character.Id, Name = "Half-built" };
            draft.Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP).Id, 2));

            var check = QueueEligibility.Check(draft, character, LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Reason, Is.EqualTo("Add 28 more cards."),
                "§1 owns the copy; the gate restates it nowhere.");
            Assert.That(check.Reason, Is.EqualTo(check.Validation.Violations[0].Message));
        }

        [Test]
        public void The_Gate_Is_Exactly_The_L1_To_L5_Predicate_Today()
        {
            // Not a tautology worth deleting: it pins the CURRENT equivalence, so the day the gate
            // grows an L6 (ownership >= copies used, M3) this test is the one that has to be
            // consciously changed rather than the two call sites quietly drifting apart.
            var character = Rotbloom();
            var decks = new List<DeckList> { Legal(character), new DeckList { Id = "d2", CharacterId = character.Id } };
            decks[1].Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.NEUTRAL).Id, 5));

            foreach (var deck in decks)
                Assert.That(QueueEligibility.IsEligible(deck, character, LiveCardSet.Db),
                    Is.EqualTo(DeckValidator.Validate(deck, character, LiveCardSet.Db).IsValid),
                    $"Deck '{deck.Id}' — the gate and the predicate must not be able to disagree.");
        }

        // ---- the roster backdraft (match-join's shape) -----------------------------------

        [Test]
        public void Resolving_Through_A_Roster_Gives_The_Same_Answer()
        {
            var character = Rotbloom();
            var roster = new[] { character };
            var deck = Legal(character);

            Assert.That(QueueEligibility.Check(deck, roster, LiveCardSet.Db).IsEligible,
                Is.EqualTo(QueueEligibility.Check(deck, character, LiveCardSet.Db).IsEligible));
        }

        [Test]
        public void An_Orphaned_Deck_Is_Denied_The_Queue_And_Says_L5()
        {
            // Deleting a character orphans its decks rather than cascading (§3). This is the moment
            // that ruling has a consequence: the deck survives, and the queue is what it loses.
            var character = Rotbloom();
            var deck = Legal(character);

            var check = QueueEligibility.Check(deck, Array.Empty<Character>(), LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Validation.Violations[0].Rule, Is.EqualTo(DeckRule.L5_CharacterBound));
            Assert.That(check.Reason, Is.Not.Empty, "The chooser row needs something to show as its reason.");
        }

        // ---- client-authored input (VULN-001 §4, "Client-supplied deck lists") ----------

        [Test]
        public void An_Unknown_Card_Id_Is_A_Rejection_Not_An_Exception()
        {
            // The submitted deck is client-authored. A card id that resolves to nothing must fall out
            // as a violation — DeckValidator uses ICardDatabase.TryGet, never Get — because an
            // exception on the match-join path is a denial-of-service, not a validation.
            var character = Rotbloom();
            var deck = new DeckList { Id = "deck-1", CharacterId = character.Id };
            deck.Entries.Add(new DeckEntry("card/does-not-exist", 30));

            QueueCheck check = default;
            Assert.DoesNotThrow(() => check = QueueEligibility.Check(deck, character, LiveCardSet.Db));
            Assert.That(check.IsDraft, Is.True);
        }

        [Test]
        public void A_Null_Deck_Is_A_Draft_Rather_Than_A_Throw()
        {
            var check = QueueEligibility.Check(null!, Rotbloom(), LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Reason, Is.Empty, "No deck, so no violation to name — but never eligible.");
        }

        [Test]
        public void A_Null_Roster_Resolves_To_No_Character_And_Fails_L5()
        {
            var check = QueueEligibility.Check(Legal(Rotbloom()), (IEnumerable<Character>)null!, LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Validation.Violations[0].Rule, Is.EqualTo(DeckRule.L5_CharacterBound));
        }
    }
}
