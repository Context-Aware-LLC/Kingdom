using System;
using System.Collections.Generic;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
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
        private const CardClass Primary = CardClass.GRAVEKEEP;
        private const CardClass Secondary = CardClass.VERDANT;

        /// <summary>A generated, legal 30 on the Gravekeep/Verdant pairing. It took a
        /// <c>Character</c> carrying that pairing until ADR-0009; the pairing is an argument now, and
        /// the deck comes back holding it.</summary>
        private static DeckList Legal(string id = "deck-legal", ulong seed = 7) =>
            DefaultDeckGenerator.Generate(Primary, Secondary, LiveCardSet.Db, new DeterministicRandom(seed), id);

        private static DeckList Draft(string id = "deck-1", string name = "Half-built") =>
            new DeckList(Primary, Secondary) { Id = id, Name = name };

        // ---- the gate ------------------------------------------------------------------

        [Test]
        public void A_Legal_Deck_Is_Eligible_And_Has_No_Reason()
        {
            var check = QueueEligibility.Check(Legal(), LiveCardSet.Db);

            Assert.That(check.IsEligible, Is.True);
            Assert.That(check.IsDraft, Is.False);
            Assert.That(check.Reason, Is.Empty, "An eligible deck has nothing to say in the row's inline reason.");
        }

        [Test]
        public void A_Short_Deck_Is_A_Draft_And_Carries_The_Validators_Copy_Verbatim()
        {
            var draft = Draft();
            draft.Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP).Id, 2));

            var check = QueueEligibility.Check(draft, LiveCardSet.Db);

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
            var decks = new List<DeckList> { Legal(), Draft("d2") };
            decks[1].Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.NEUTRAL).Id, 5));

            foreach (var deck in decks)
                Assert.That(QueueEligibility.IsEligible(deck, LiveCardSet.Db),
                    Is.EqualTo(DeckValidator.Validate(deck, LiveCardSet.Db).IsValid),
                    $"Deck '{deck.Id}' — the gate and the predicate must not be able to disagree.");
        }

        // ---- L5, which now asks about the pairing ---------------------------------------
        //
        // THREE TESTS STOOD HERE AND ALL THREE LOST THEIR SUBJECT (ADR-0009). They covered the
        // roster overload — Check(deck, roster, db), which resolved the deck's CharacterId against a
        // list — and the two ways that resolution failed: an empty roster (the deck's character had
        // been deleted, so it was an ORPHAN, denied the queue and saying L5) and a null one. There is
        // no roster overload, no orphan, and no second object to resolve against. What survives is
        // the question L5 asks now: is the pairing on this deck two real, distinct classes?

        [Test]
        public void A_Malformed_Pairing_Is_Denied_The_Queue_And_Says_L5()
        {
            // NEUTRAL/NEUTRAL is what an empty SubmitDeckMsg deserializes to, so this is the
            // match-join shape rather than a state a screen could produce.
            var deck = new DeckList(CardClass.NEUTRAL, CardClass.NEUTRAL) { Id = "deck-1" };

            var check = QueueEligibility.Check(deck, LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Validation.Violations[0].Rule, Is.EqualTo(DeckRule.L5_PairingWellFormed));
            Assert.That(check.Reason, Is.Not.Empty, "The chooser row needs something to show as its reason.");
        }

        // ---- client-authored input (VULN-001 §4, "Client-supplied deck lists") ----------

        [Test]
        public void An_Unknown_Card_Id_Is_A_Rejection_Not_An_Exception()
        {
            // The submitted deck is client-authored. A card id that resolves to nothing must fall out
            // as a violation — DeckValidator uses ICardDatabase.TryGet, never Get — because an
            // exception on the match-join path is a denial-of-service, not a validation.
            var deck = Draft();
            deck.Entries.Add(new DeckEntry(FixtureCard.Id("card/does-not-exist"), 30));

            QueueCheck check = default;
            Assert.DoesNotThrow(() => check = QueueEligibility.Check(deck, LiveCardSet.Db));
            Assert.That(check.IsDraft, Is.True);
        }

        [Test]
        public void A_Null_Deck_Is_A_Draft_Rather_Than_A_Throw()
        {
            var check = QueueEligibility.Check(null!, LiveCardSet.Db);

            Assert.That(check.IsDraft, Is.True);
            Assert.That(check.Reason, Is.Empty, "No deck, so no violation to name — but never eligible.");
        }

    }
}
