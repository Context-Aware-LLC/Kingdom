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

    /// <summary>One slot of a template: its kind, its id, whether the plural form is wanted, and the
    /// grammatical form a translation asks for.</summary>
    public readonly struct CardTextSlot
    {
        public readonly CardTextSlotKind Kind;
        public readonly string Id;
        public readonly bool Plural;

        /// <summary>
        /// A case or other inflection a TRANSLATION names after the slot — <c>dat</c> in German
        /// <c>{tribe:construct:plural:dat}</c>, for "allen befreundeten Konstrukten". English never
        /// writes one. The client reads the glossary row with the form appended and falls back to the
        /// plain row, and a form is not part of the slot's identity: <see cref="Bare"/> is what a
        /// translation must match.
        /// </summary>
        public readonly string Form;

        public CardTextSlot(CardTextSlotKind kind, string id, bool plural, string form = "")
        {
            Kind = kind;
            Id = id;
            Plural = plural;
            Form = form ?? "";
        }

        /// <summary>The damage amount a <see cref="CardTextSlotKind.Damage"/> slot was printed with.</summary>
        public int Amount => int.Parse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture);

        /// <summary>This slot without its <see cref="Form"/>.</summary>
        public CardTextSlot Bare => new CardTextSlot(Kind, Id, Plural);

        public override string ToString()
            => "{" + Prefix(Kind) + ":" + Id + (Plural ? ":plural" : "") + (Form.Length > 0 ? ":" + Form : "") + "}";

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

        /// <summary>
        /// The fingerprint of a card's English that a translation records as its <c>src</c>:
        /// name, template, flavor and tooltip together, so a change to any of them marks the whole
        /// translation stale and the card falls back to English rather than mixing the two.
        /// FNV-1a 64 over UTF-8, 16 hex digits — the same on the client, CardCi and any script.
        /// </summary>
        public static string SourceHash(CardDefinition card, string template)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            byte[] bytes = Encoding.UTF8.GetBytes(card.Name + "\u001f" + (template ?? "") + "\u001f" +
                card.FlavorText + "\u001f" + card.Tooltip);
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
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
            if (parts.Length < 2 || parts.Length > 4 || parts[1].Length == 0) return false;
            bool plural = parts.Length > 2 && parts[2] == "plural";
            int formAt = plural ? 3 : 2;
            if (parts.Length > formAt + 1) return false;
            string form = parts.Length > formAt ? parts[formAt] : "";
            if (parts.Length > formAt && !IsForm(form)) return false;

            CardTextSlotKind? kind = null;
            foreach (CardTextSlotKind k in (CardTextSlotKind[])System.Enum.GetValues(typeof(CardTextSlotKind)))
                if (CardTextSlot.Prefix(k) == parts[0]) kind = k;
            if (kind == null) return false;

            slot = new CardTextSlot(kind.Value, parts[1], plural, form);
            return true;
        }

        private static bool IsForm(string form)
        {
            if (form.Length == 0 || form == "plural") return false;
            foreach (char c in form)
                if (c < 'a' || c > 'z') return false;
            return true;
        }
    }
}
