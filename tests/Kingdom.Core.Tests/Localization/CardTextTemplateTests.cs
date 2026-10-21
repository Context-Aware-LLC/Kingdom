using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using Kingdom.Core.Tests.Targeting;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Localization
{
    /// <summary>
    /// The card-text template is the generated text with its values marked as slots, and nothing
    /// else. Filled back in English it must be the text byte for byte, on every shipped card and
    /// under every Arcane Surge bonus — that is what lets a translation replace the words around
    /// the slots and trust the slots to be the whole of what varies.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CardTextTemplateTests
    {
        [Test]
        public void Every_shipped_template_fills_back_to_the_authored_text()
        {
            List<CardDefinition> cards = CorpusBehaviourSweep.ShippedCards();
            var db = new JsonCardDatabase(cards);
            var wrong = new List<string>();
            int slots = 0;
            foreach (CardDefinition card in cards)
            {
                string template = CardTextGenerator.Template(card, db);
                slots += CardTextTemplate.Slots(template).Count;
                string filled = CardTextTemplate.FillEnglish(template, db);
                if (filled != card.Text) wrong.Add($"{card.Id}: \"{filled}\" != \"{card.Text}\"");
            }
            TestContext.Out.WriteLine($"{cards.Count} cards, {slots} slots");
            Assert.That(wrong, Is.Empty);
        }

        [Test]
        public void A_boosted_spell_fills_to_the_effective_text([Values(1, 2, 5)] int bonus)
        {
            List<CardDefinition> cards = CorpusBehaviourSweep.ShippedCards();
            var db = new JsonCardDatabase(cards);
            List<CardDefinition> spells = cards.Where(c => c.Type == CardType.SPELL &&
                c.Rules.Any(r => r.Action is DamageAction)).ToList();
            Assert.That(spells, Is.Not.Empty);

            var wrong = new List<string>();
            foreach (CardDefinition spell in spells)
            {
                string filled = CardTextTemplate.FillEnglish(CardTextGenerator.Template(spell, db), db, bonus);
                string effective = CardTextGenerator.GenerateEffective(spell, db, bonus);
                if (filled != effective) wrong.Add($"{spell.Id}: \"{filled}\" != \"{effective}\"");
            }
            TestContext.Out.WriteLine($"{spells.Count} damage spells at +{bonus}");
            Assert.That(wrong, Is.Empty);
        }

        // A bold word left in the template's prose is one a translation would have to spell itself,
        // and the client could no longer bold it by slot.
        [Test]
        public void No_game_vocabulary_is_left_outside_a_slot()
        {
            List<CardDefinition> cards = CorpusBehaviourSweep.ShippedCards();
            var db = new JsonCardDatabase(cards);
            var vocabulary = new Regex(@"\b(" + string.Join("|",
                CardTextGenerator.EmphasisVocabulary().Select(Regex.Escape)) + @")\b");
            var wrong = new List<string>();
            foreach (CardDefinition card in cards)
            {
                string prose = CardTextTemplate.Fill(CardTextGenerator.Template(card, db), _ => "#");
                Match m = vocabulary.Match(prose);
                if (m.Success) wrong.Add($"{card.Id}: \"{m.Value}\" in \"{prose}\"");
            }
            Assert.That(wrong, Is.Empty);
        }

        [Test]
        public void Writing_a_template_leaves_the_plain_render_alone()
        {
            List<CardDefinition> cards = CorpusBehaviourSweep.ShippedCards();
            var db = new JsonCardDatabase(cards);
            CardDefinition card = cards.First(c => c.Rules.Count > 0);

            CardTextGenerator.Template(card, db);

            Assert.That(CardTextGenerator.Generate(card, db), Does.Not.Contain("{"));
        }

        [Test]
        public void Slots_parse_and_stray_braces_print_as_written()
        {
            const string template = "Deal {dmg:3} damage to all {tribe:beast:plural}. {oops} {kw:} {dmg:1";

            IReadOnlyList<CardTextSlot> slots = CardTextTemplate.Slots(template);

            Assert.That(slots.Select(s => s.ToString()), Is.EqualTo(new[] { "{dmg:3}", "{tribe:beast:plural}" }));
            Assert.That(slots[1].Plural, Is.True);
            Assert.That(CardTextTemplate.Fill(template, s => s.Kind.ToString()),
                Is.EqualTo("Deal Damage damage to all Tribe. {oops} {kw:} {dmg:1"));
        }

        [Test]
        public void English_fills_each_slot_kind()
        {
            var db = new JsonCardDatabase(Array.Empty<CardDefinition>());

            Assert.That(CardTextTemplate.FillEnglish("{dmg:3}", db, 2), Is.EqualTo("5"));
            Assert.That(CardTextTemplate.FillEnglish("{kw:frost}", db), Is.EqualTo("Frost"));
            Assert.That(CardTextTemplate.FillEnglish("{label:turn_end}", db), Is.EqualTo("Turn End"));
            Assert.That(CardTextTemplate.FillEnglish("{label:toll:plural}", db), Is.EqualTo("Tolls"));
            Assert.That(CardTextTemplate.FillEnglish("{tribe:undead:plural}", db), Is.EqualTo("Undead"));
            Assert.That(CardTextTemplate.FillEnglish("{tribe:beast:plural}", db), Is.EqualTo("Beasts"));
        }
    }
}
