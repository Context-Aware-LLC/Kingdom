using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>A pairing's deck name (whimsy pass, Sam 2026-10-17).</summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckNamesTests
    {
        [Test]
        public void Every_Pairing_Has_Its_Own_Name_Either_Way_Round()
        {
            var classes = ((CardClass[])Enum.GetValues(typeof(CardClass)))
                .Where(c => c != CardClass.NEUTRAL).ToArray();
            var names = new List<string>();
            foreach (CardClass a in classes)
                foreach (CardClass b in classes)
                {
                    if (a >= b) continue;
                    string name = DeckNames.ForPairing(a, b);
                    Assert.That(name, Is.Not.EqualTo("New Deck"), $"{a}/{b} has no name.");
                    Assert.That(DeckNames.ForPairing(b, a), Is.EqualTo(name));
                    names.Add(name);
                }
            Assert.That(names, Is.Unique);
        }

        [Test]
        public void A_Taken_Name_Takes_The_Next_Regnal_Number()
        {
            var g = CardClass.GRAVEKEEP;
            var v = CardClass.VERDANT;
            Assert.That(DeckNames.Unused(v, g, new string[0]), Is.EqualTo("Compost"));
            Assert.That(DeckNames.Unused(v, g, new[] { "compost" }), Is.EqualTo("Compost II"));
            Assert.That(DeckNames.Unused(g, v, new[] { "Compost", "Compost II", "Compost III" }),
                Is.EqualTo("Compost IV"));
        }
    }
}
