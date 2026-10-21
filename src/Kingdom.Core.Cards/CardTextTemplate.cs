using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>What a slot in a card-text template stands for.</summary>
    public enum CardTextSlotKind
    {
        /// <summary><c>{dmg:3}</c> — a damage amount, which Arcane Surge raises at display time.</summary>
        Damage,

        /// <summary><c>{kw:frost}</c> — a keyword name, printed bold.</summary>
        Keyword,

        /// <summary><c>{label:toll}</c> — a timing label or action verb the game prints bold
        /// (Toll, Aura, Unearth, Freeze). The id is the glossary's <c>label_*</c> suffix.</summary>
        Label,

        /// <summary><c>{tribe:undead}</c> — a tribe name.</summary>
        Tribe,

        /// <summary><c>{card:1042}</c> — another card, named by id; a summoned minion carries its stats.</summary>
        Card,
    }

    /// <summary>One slot of a template: its kind, its id, and whether the plural form is wanted.</summary>
    public readonly struct CardTextSlot
    {
        public readonly CardTextSlotKind Kind;
        public readonly string Id;
        public readonly bool Plural;

        public CardTextSlot(CardTextSlotKind kind, string id, bool plural)
        {
            Kind = kind;
            Id = id;
            Plural = plural;
        }

        /// <summary>The damage amount a <see cref="CardTextSlotKind.Damage"/> slot was printed with.</summary>
        public int Amount => int.Parse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture);

        public override string ToString()
            => "{" + Prefix(Kind) + ":" + Id + (Plural ? ":plural" : "") + "}";

        internal static string Prefix(CardTextSlotKind kind) => kind switch
        {
            CardTextSlotKind.Damage => "dmg",
            CardTextSlotKind.Keyword => "kw",
            CardTextSlotKind.Label => "label",
            CardTextSlotKind.Tribe => "tribe",
            CardTextSlotKind.Card => "card",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// A card's text with every value a translation has to supply marked as a slot (2026-10-20):
    /// <c>Deal {dmg:3} damage to a {tribe:beast}.</c> <see cref="CardTextGenerator.Template"/>
    /// writes one; <see cref="Fill"/> turns one back into text in any language.
    ///
    /// <para><b>A translation is a template in another language, and nothing else.</b> The words
    /// around the slots are the translator's; the slots are filled at display time, so a boosted
    /// spell shows its live damage and a summoned card shows its translated name without the card
    /// being re-rendered in that language. English needs no translation: <see cref="FillEnglish"/>
    /// over the template is byte for byte the generated text, which the tests hold for every card.</para>
    ///
    /// <para>Slot ids are the lowercased enum or glossary ids (<c>keyword_frost</c>,
    /// <c>tribe_undead</c>, <c>label_toll</c>), so a client fills them from its glossary table
    /// directly. A brace that does not open a well-formed slot is printed as written.</para>
    ///
    /// <para>See <c>docs/dev-docs/specs/localization.md</c>, "Card text: proposed mechanism".</para>
    /// </summary>
    public static class CardTextTemplate
    {
        /// <summary>Replaces every slot in <paramref name="template"/> with what
        /// <paramref name="resolve"/> returns for it.</summary>
        public static string Fill(string template, Func<CardTextSlot, string> resolve)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            if (template.IndexOf('{') < 0) return template;

            var text = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '{' && TryRead(template, i, out CardTextSlot slot, out int end))
                {
                    text.Append(resolve(slot));
                    i = end;
                }
                else
                {
                    text.Append(template[i++]);
                }
            }
            return text.ToString();
        }

        /// <summary>Every slot in <paramref name="template"/>, in order — what a translation of it
        /// must also contain.</summary>
        public static IReadOnlyList<CardTextSlot> Slots(string template)
        {
            var slots = new List<CardTextSlot>();
            Fill(template, slot => { slots.Add(slot); return ""; });
            return slots;
        }

        /// <summary>
        /// The English the template stands for, with <paramref name="spellDamageBonus"/> added to
        /// every damage slot. Pass the bonus only for a spell: that is the one card type Arcane
        /// Surge reaches, the same rule <see cref="CardTextGenerator.GenerateEffective"/> applies.
        /// </summary>
        public static string FillEnglish(string template, ICardDatabase pool, int spellDamageBonus = 0)
        {
            if (spellDamageBonus < 0) throw new ArgumentOutOfRangeException(nameof(spellDamageBonus));
            return Fill(template, slot => English(slot, pool, spellDamageBonus));
        }

        /// <summary>One slot in English.</summary>
        public static string English(CardTextSlot slot, ICardDatabase pool, int spellDamageBonus = 0)
        {
            switch (slot.Kind)
            {
                case CardTextSlotKind.Damage:
                    return (slot.Amount + spellDamageBonus).ToString(CultureInfo.InvariantCulture);
                case CardTextSlotKind.Keyword:
                    return CardTextGenerator.DescribeKeyword(Enum<Keyword>(slot.Id));
                case CardTextSlotKind.Label:
                    string label = CardTextGenerator.LabelFor(slot.Id);
                    return slot.Plural ? label + "s" : label;
                case CardTextSlotKind.Tribe:
                    string tribe = CardTextGenerator.DescribeTribe(Enum<Tribe>(slot.Id));
                    return slot.Plural ? CardTextGenerator.DescribePlural(tribe) : tribe;
                case CardTextSlotKind.Card:
                    int id = int.Parse(slot.Id, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (pool == null || !pool.TryGet(id, out CardDefinition named))
                        throw new KeyNotFoundException($"no card '{id}' in the pool the template was filled from.");
                    string name = CardTextGenerator.DescribeCardName(named);
                    return slot.Plural ? CardTextGenerator.DescribePlural(name) : name;
                default:
                    throw new ArgumentOutOfRangeException(nameof(slot), slot.Kind, null);
            }
        }

        private static T Enum<T>(string id) where T : struct
            => (T)System.Enum.Parse(typeof(T), id.ToUpperInvariant());

        private static bool TryRead(string template, int start, out CardTextSlot slot, out int end)
        {
            slot = default;
            end = template.IndexOf('}', start);
            if (end < 0) return false;
            string[] parts = template.Substring(start + 1, end - start - 1).Split(':');
            end++;
            if (parts.Length < 2 || parts.Length > 3 || parts[1].Length == 0) return false;
            if (parts.Length == 3 && parts[2] != "plural") return false;

            CardTextSlotKind? kind = null;
            foreach (CardTextSlotKind k in (CardTextSlotKind[])System.Enum.GetValues(typeof(CardTextSlotKind)))
                if (CardTextSlot.Prefix(k) == parts[0]) kind = k;
            if (kind == null) return false;

            slot = new CardTextSlot(kind.Value, parts[1], parts.Length == 3);
            return true;
        }
    }
}
