using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// EF-031's DONE-WHEN, sitting in the gate that enforces it: every class can seat a legal
    /// 30-card starter deck (L1-L5) as produced by <see cref="DefaultDeckGenerator"/>.
    ///
    /// <para><b>Why a second fixture and not a reuse.</b> <see cref="DefaultDeckGeneratorTests"/>
    /// already proves every pairing generates an L1-L5 deck, but it is
    /// <c>[Category("Collection")]</c>, and <c>ci.yml</c> Stage 2 (the BLOCKING core-unit stage)
    /// runs only <c>Category=Unit</c> - it runs Unit, Contract, Firewall and GoldenReplay and
    /// nothing else. The entire Collection category, deck legality included, executes in no CI
    /// stage today. This fixture is <c>[Category("Unit")]</c> so the launch-gate guarantee EF-031
    /// asks for is actually gated, not merely present.</para>
    ///
    /// <para><b>Premise correction (recorded, not silently reconciled).</b> The EF-031 brief and
    /// LAUNCH-WORKPLAN.md both say "Nine classes." The <see cref="CardClass"/> enum says otherwise:
    /// four real classes plus NEUTRAL. Nine is a Hearthstone class count that Emberfall does not
    /// share. A class also never seats a deck alone - a deck is bound to a dual-class
    /// <see cref="Character"/> and draws from <c>Primary u Secondary u NEUTRAL</c> - so "every
    /// class" is satisfied by sweeping every valid pairing, which is how a class becomes deck-able.
    /// <see cref="There_Are_Four_Real_Classes_Not_Nine"/> pins the count so the day a fifth class is
    /// added this fails and the sweep is re-read rather than silently under-covering.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class EveryClassHasALegalDeckTests
    {
        /// <summary>The deck-able classes: the enum minus NEUTRAL (NEUTRAL is never a hero class).</summary>
        private static CardClass[] RealClasses() =>
            Enum.GetValues(typeof(CardClass)).Cast<CardClass>().Where(c => c != CardClass.NEUTRAL).ToArray();

        /// <summary>Every ordered pairing of two distinct real classes - the full space the generator serves.</summary>
        private static IEnumerable<Character> AllPairings()
        {
            var classes = RealClasses();
            foreach (var p in classes)
                foreach (var s in classes)
                    if (p != s)
                        yield return new Character($"chr-{p}-{s}", $"{p}/{s}", p, s);
        }

        [Test]
        public void Every_Class_Seats_A_Deck_That_Passes_L1_Through_L5()
        {
            var covered = new HashSet<CardClass>();

            foreach (var character in AllPairings())
            {
                // Fixed seed: this is a legality gate, not a distribution test - it must fail on a
                // pool shortfall, never flap on a lucky or unlucky draw.
                var deck = DefaultDeckGenerator.Generate(
                    character, LiveCardSet.Db, new DeterministicRandom(1), "deck-1");
                var result = DeckValidator.Validate(deck, character, LiveCardSet.Db);

                Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize),
                    $"L1 size - {character.Name}");
                Assert.That(result.IsValid, Is.True,
                    $"{character.Name} failed L1-L5: " +
                    string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
                Assert.That(deck.CharacterId, Is.EqualTo(character.Id),
                    $"L5 binding is set by the generator - {character.Name}");

                covered.Add(character.PrimaryClass);
                covered.Add(character.SecondaryClass);
            }

            // "Every class" made explicit: no real class slipped through the pairing sweep unseated.
            Assert.That(covered, Is.EquivalentTo(RealClasses()),
                "A real class seated no legal deck in any pairing - EF-031 is not satisfied.");
        }

        [Test]
        public void The_Real_Class_Set_Is_Exactly_These_Eight()
        {
            // The tripwire that keeps a stale count from silently under-covering: if the real
            // (non-NEUTRAL) CardClass set ever changes, this fires and the coverage sweep above
            // must be re-read rather than assumed still exhaustive.
            //
            // It has already earned its keep once. Authored against FOUR classes, it fired the
            // moment the commit was cherry-picked onto a master that had grown to EIGHT
            // (2026-08-01) - CLOCKWRIGHT, NIGHTVEIL, STONEWARDEN and TEMPEST had been added with
            // nothing to prompt a re-read of EF-031's premise. The sweep above was re-run against
            // all eight and still passes, so the ticket's substance holds; only its count was stale.
            //
            // Its original name was There_Are_Four_Real_Classes_Not_Nine, written to refute a brief
            // that claimed nine. Both numbers are now history; the set below is the assertion.
            Assert.That(RealClasses(), Is.EquivalentTo(new[]
            {
                CardClass.EMBERWARD, CardClass.TIDECALLER, CardClass.GRAVEKEEP, CardClass.VERDANT,
                CardClass.CLOCKWRIGHT, CardClass.NIGHTVEIL, CardClass.STONEWARDEN, CardClass.TEMPEST,
            }), "The real (non-NEUTRAL) CardClass set changed - re-read EF-031's coverage sweep, " +
                "then update this set in the SAME commit that adds or removes the class.");
        }
    }
}
