using Kingdom.Core.Cards;

namespace Kingdom.Core.Tests.Support
{
    /// <summary>Card-shaped helpers for hand-authored fixtures.</summary>
    internal static class TestCards
    {
        /// <summary>
        /// Gives a synthetic row the card text an authored row would carry, so it is legal under
        /// C26 without the fixture spelling out a sentence that is not what the test is about.
        /// </summary>
        /// <remarks>
        /// This is the "use what the rules read as" button an author presses in the authoring window,
        /// and it is legitimate here for the same reason: a fixture exercising C14 or C21 needs a
        /// well-formed card, and since 2026-08-16 well-formed includes saying what it does.
        ///
        /// <para><b>Never reach for this in a fixture whose subject IS the text.</b> Seeding from the
        /// generator makes the check vacuous by construction, so a C26 test written this way would
        /// pass no matter what C26 did. Those fixtures author their sentence by hand — see
        /// CardTextCheckerTests.</para>
        ///
        /// <para>A DELIBERATELY malformed fixture — an out-of-range TurnOwner, a zero-use FINITE
        /// lifetime — has no renderable sentence, and going through Check rather than Generate is
        /// what stops this helper throwing on the way into a test about some other rejection. Such a
        /// card keeps an empty text and collects a C26-TEXT-UNRENDERABLE alongside the rejection it
        /// was written to prove, which is what those tests assert on.</para>
        /// </remarks>
        public static CardDefinition Authored(CardDefinition def)
        {
            // NoPool: a synthetic fixture names no other card, and saying so keeps a fixture that
            // starts to name one unrenderable rather than quietly resolving it.
            CardTextGenerator.Check(def, CardTextGenerator.NoPool.Instance, out string expected);
            return Authored(def, expected);
        }

        /// <summary>Gives a synthetic row an explicit sentence, for a fixture that cares which one.</summary>
        public static CardDefinition Authored(CardDefinition def, string text)
            => new CardDefinition(
                id: def.Id, handle: def.Handle, name: def.Name, type: def.Type, cost: def.Cost,
                attack: def.Attack, health: def.Health, durability: def.Durability,
                @class: def.Class, rarity: def.Rarity,
                keywords: def.Keywords, rules: def.Rules,
                flavorText: def.FlavorText, artId: def.ArtId, setId: def.SetId,
                collectible: def.Collectible, tribe: def.Tribe, factionId: def.FactionId,
                tooltip: def.Tooltip, text: text);
    }
}
