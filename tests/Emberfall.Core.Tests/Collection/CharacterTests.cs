using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="Character"/> — the permanent class pairing (Sam, 2026-07-26; gbrain
    /// <c>decisions/characters-dual-class-pairing</c>; ui/collection-deckbuilder.md §5 O-1).
    ///
    /// <para>Two properties carry the whole ruling and are worth pinning explicitly, because both
    /// are the kind of thing a later refactor "simplifies" away: pairs are <b>ordered</b> (the hero
    /// power comes from the primary, so Pyra/Maren is a different character from Maren/Pyra), and
    /// <c>NEUTRAL</c> is <b>not a class you can pair with</b> (it is the shared pool, not a half).</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CharacterTests
    {
        private static readonly CardClass[] RealClasses =
            Enum.GetValues(typeof(CardClass)).Cast<CardClass>().Where(c => c != CardClass.NEUTRAL).ToArray();

        [Test]
        public void Two_Distinct_Real_Classes_Is_The_Only_Valid_Pairing()
        {
            foreach (var primary in RealClasses)
            foreach (var secondary in RealClasses)
            {
                bool expected = primary != secondary;
                Assert.That(Character.IsValidPairing(primary, secondary), Is.EqualTo(expected),
                    $"{primary}/{secondary}");
            }
        }

        [Test]
        public void Neutral_Is_Never_Half_Of_A_Pairing()
        {
            // NEUTRAL is the shared pool every character already gets (L4), not a class to pair with.
            // Allowing it would make "Emberward/Neutral" a character whose pool equals plain Emberward.
            foreach (var other in RealClasses)
            {
                Assert.That(Character.IsValidPairing(CardClass.NEUTRAL, other), Is.False, $"NEUTRAL/{other}");
                Assert.That(Character.IsValidPairing(other, CardClass.NEUTRAL), Is.False, $"{other}/NEUTRAL");
            }
            Assert.That(Character.IsValidPairing(CardClass.NEUTRAL, CardClass.NEUTRAL), Is.False);
        }

        [Test]
        public void Same_Class_Twice_Is_Not_A_Pairing()
        {
            foreach (var c in RealClasses)
                Assert.That(Character.IsValidPairing(c, c), Is.False, $"{c}/{c}");
        }

        [Test]
        public void Pairing_Validity_Is_Symmetric_Even_Though_Characters_Are_Not()
        {
            // IsValidPairing asks "are these two classes pairable", which is order-free. The ORDER
            // still matters to the character — same pool, different hero power — so a test that
            // conflated the two would be reading the ruling backwards.
            foreach (var a in RealClasses)
            foreach (var b in RealClasses)
                Assert.That(Character.IsValidPairing(a, b), Is.EqualTo(Character.IsValidPairing(b, a)));

            var pyraMaren = new Character("c1", "Ashtide", CardClass.EMBERWARD, CardClass.TIDECALLER);
            var marenPyra = new Character("c2", "Tideash", CardClass.TIDECALLER, CardClass.EMBERWARD);

            Assert.That(pyraMaren.PrimaryClass, Is.Not.EqualTo(marenPyra.PrimaryClass),
                "Ordered pairs: the primary is what picks the single hero power.");
            foreach (var cls in Enum.GetValues(typeof(CardClass)).Cast<CardClass>())
                Assert.That(pyraMaren.AllowsClass(cls), Is.EqualTo(marenPyra.AllowsClass(cls)),
                    $"Reversed pairs share an identical card pool ({cls}).");
        }

        [Test]
        public void AllowsClass_Is_Neutral_Union_Primary_Union_Secondary()
        {
            var c = new Character("c1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

            Assert.That(c.AllowsClass(CardClass.NEUTRAL), Is.True, "Neutral is in every character's pool.");
            Assert.That(c.AllowsClass(CardClass.GRAVEKEEP), Is.True);
            Assert.That(c.AllowsClass(CardClass.VERDANT), Is.True);
            Assert.That(c.AllowsClass(CardClass.EMBERWARD), Is.False);
            Assert.That(c.AllowsClass(CardClass.TIDECALLER), Is.False);
        }

        [Test]
        public void An_Invalid_Pairing_Can_Still_Be_Constructed()
        {
            // Documented contract: IsValidPairing is a helper, not a constructor guard, so a loaded
            // row is reported as stored. The rejection lives at the write boundary instead — see
            // JsonCharacterStore.Save.
            var degenerate = new Character("c1", "Broken", CardClass.EMBERWARD, CardClass.EMBERWARD);
            Assert.That(Character.IsValidPairing(degenerate.PrimaryClass, degenerate.SecondaryClass), Is.False);
            Assert.That(degenerate.AllowsClass(CardClass.EMBERWARD), Is.True);
        }
    }
}
