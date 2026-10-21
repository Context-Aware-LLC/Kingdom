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
        public void A_form_parses_and_is_not_part_of_the_slot()
        {
            IReadOnlyList<CardTextSlot> slots = CardTextTemplate.Slots(
                "Gib allen {tribe:construct:plural:dat} und einer {tribe:beast:dat} {dmg:2}. {tribe:beast:Dat}");

            Assert.That(slots.Select(s => s.ToString()),
                Is.EqualTo(new[] { "{tribe:construct:plural:dat}", "{tribe:beast:dat}", "{dmg:2}" }));
            Assert.That(slots[0].Plural, Is.True);
            Assert.That(slots[1].Plural, Is.False);
            Assert.That(slots[1].Form, Is.EqualTo("dat"));
            Assert.That(CardTranslations.SameSlots("Give all {tribe:construct:plural} {dmg:2} and a {tribe:beast}.",
                "Gib {dmg:2} allen {tribe:construct:plural:dat} und einer {tribe:beast:dat}."), Is.True);
            Assert.That(CardTranslations.SameSlots("Deal {dmg:2} damage.", "Verursache {dmg:3} Schaden."), Is.False);
        }

        [Test]
        public void The_source_file_parses_back_current_and_an_edit_makes_it_stale()
        {
            List<CardDefinition> cards = CorpusBehaviourSweep.ShippedCards();
            var db = new JsonCardDatabase(cards);

            CardTranslations source = CardTranslations.Parse(CardTranslations.WriteSource(cards, db));

            Assert.That(source.Rows.Count, Is.EqualTo(cards.Count));
            var notCurrent = cards.Where(c => source.Current(c, db) == null).Select(c => c.Id).ToList();
            Assert.That(notCurrent, Is.Empty);
            foreach (int named in CardTranslations.NamedInPlural(cards.Select(c => CardTextGenerator.Template(c, db))))
                Assert.That(source.Rows[named].Plural, Is.Not.Empty, $"card {named} is named in the plural");

            CardDefinition card = cards.First(c => c.Rules.Count > 0);
            CardTranslation row = source.Rows[card.Id];
            var edited = new CardTranslation(row.Name, row.Plural, row.Text, row.Flavor, row.Tooltip, "0000000000000000");
            Assert.That(CardTranslations.IsStale(card, CardTextGenerator.Template(card, db), edited), Is.True);
            Assert.That(CardTranslations.Problems(card, CardTextGenerator.Template(card, db), row), Is.Empty);
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
