using System.Globalization;
using System.Text;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// A card's name, rules text, flavor and tooltip in the player's language (2026-10-20). Reads
    /// <c>Resources/Localization/values-&lt;lang&gt;/cards.json</c> (<see cref="CardTranslations"/>)
    /// and falls back to the card's English, whole, when the language has no current translation of
    /// it — a stale one included.
    ///
    /// <para><b>Display only.</b> The English <see cref="CardDefinition.Name"/> stays the key for
    /// everything that is not a player reading it: <see cref="MinionSummonArchetypes"/> picks an
    /// animation from it, sorts break ties on it, logs name cards by it.</para>
    ///
    /// <para>Translated text is a template: <c>{dmg}</c> takes the live Spell Damage on a spell,
    /// <c>{kw}</c> and <c>{label}</c> are printed bold from the glossary, <c>{tribe}</c> from the
    /// glossary in the form the slot names (<c>tribe_construct_plural_dat</c>, else the plain row),
    /// <c>{card}</c> as that card's translated name, with its stats when it is a minion. See
    /// <c>docs/dev-docs/specs/localization.md</c>.</para>
    /// </summary>
    public static class CardLoc
    {
        private static string _language;
        private static CardTranslations _table = CardTranslations.Empty;

        /// <summary>The pool a caller with none of its own resolves named cards from; the last one
        /// <see cref="CardDatabaseLoader"/> loaded.</summary>
        internal static ICardDatabase DefaultPool { get; set; }

        private static CardTranslations Table
        {
            get
            {
                string language = Loc.Language;
                if (language == _language) return _table;
                _language = language;
                _table = CardTranslations.Empty;
                if (language == Loc.Source) return _table;
                var asset = Resources.Load<TextAsset>("Localization/values-" + language + "/cards");
                if (asset == null) return _table;
                try { _table = CardTranslations.Parse(asset.text); }
                catch (System.Exception e) { Debug.LogError($"[CardLoc] values-{language}/cards.json: {e.Message}"); }
                return _table;
            }
        }

        /// <summary>The current translation of <paramref name="card"/>, or null to show English.</summary>
        public static CardTranslation Current(CardDefinition card, ICardDatabase pool = null)
        {
            if (card == null || Loc.IsSource) return null;
            return Table.Current(card, Pool(pool));
        }

        public static string Name(CardDefinition card, ICardDatabase pool = null)
            => card == null ? "" : Current(card, pool)?.Name ?? card.Name;

        public static string Flavor(CardDefinition card, ICardDatabase pool = null)
            => card == null ? "" : Current(card, pool)?.Flavor ?? card.FlavorText;

        public static string Tooltip(CardDefinition card, ICardDatabase pool = null)
            => card == null ? "" : Current(card, pool)?.Tooltip ?? card.Tooltip;

        /// <summary>The rules text as the card face prints it — rich text, the game's vocabulary bold —
        /// with <paramref name="spellDamageBonus"/> applied when the card is a spell.</summary>
        public static string RulesText(CardDefinition card, ICardDatabase pool = null, int spellDamageBonus = 0)
        {
            if (card == null) return "";
            ICardDatabase resolved = Pool(pool);
            CardTranslation translation = Current(card, resolved);
            if (translation == null)
                return CardTextStyling.Style(CardTextGenerator.GenerateEffective(card, resolved, spellDamageBonus));

            int bonus = card.Type == CardType.SPELL ? spellDamageBonus : 0;
            return CardTextTemplate.Fill(translation.Text, slot => Resolve(slot, resolved, bonus));
        }

        /// <summary>The rules text with no markup and no Spell Damage, for a one-line summary.</summary>
        public static string PlainText(CardDefinition card, ICardDatabase pool = null)
        {
            if (card == null) return "";
            ICardDatabase resolved = Pool(pool);
            CardTranslation translation = Current(card, resolved);
            return translation == null
                ? card.Text
                : CardTextTemplate.Fill(translation.Text, slot => Resolve(slot, resolved, 0, bold: false));
        }

        /// <summary>Name, text and tooltip in the player's language, one per line, for search.</summary>
        public static string SearchText(CardDefinition card)
        {
            CardTranslation t = Current(card);
            if (t == null) return "";
            var sb = new StringBuilder();
            sb.Append(t.Name).Append('\n');
            sb.Append(CardTextTemplate.Fill(t.Text, slot => Resolve(slot, Pool(null), 0, bold: false))).Append('\n');
            foreach (Keyword kw in card.Keywords) sb.Append(GameTerms.Keyword(kw)).Append('\n');
            if (card.Tribe.HasValue && card.Tribe.Value != Tribe.NONE) sb.Append(GameTerms.Tribe(card.Tribe.Value)).Append('\n');
            sb.Append(GameTerms.Type(card.Type));
            return sb.ToString();
        }

        private static ICardDatabase Pool(ICardDatabase pool)
            => pool ?? DefaultPool ?? CardTextGenerator.NoPool.Instance;

        private static string Resolve(CardTextSlot slot, ICardDatabase pool, int bonus, bool bold = true)
        {
            switch (slot.Kind)
            {
                case CardTextSlotKind.Damage:
                    return (slot.Amount + bonus).ToString(CultureInfo.InvariantCulture);
                case CardTextSlotKind.Keyword:
                    return Emphasis(Term("keyword_" + slot.Id, false, slot.Form), bold);
                case CardTextSlotKind.Label:
                    return Emphasis(Term("label_" + slot.Id, slot.Plural, slot.Form), bold);
                case CardTextSlotKind.Tribe:
                    return Term("tribe_" + slot.Id, slot.Plural, slot.Form);
                case CardTextSlotKind.Card:
                    return NamedCard(slot, pool);
                default:
                    return slot.ToString();
            }
        }

        /// <summary>The glossary row for <paramref name="key"/>, plural and form appended where
        /// the language has them: <c>_plural_dat</c>, then <c>_plural</c>, then the bare row.</summary>
        private static string Term(string key, bool plural, string form)
        {
            string stem = plural ? key + "_plural" : key;
            return (form.Length > 0 ? Loc.Find(stem + "_" + form) : null)
                ?? Loc.Find(stem)
                ?? Loc.Get(key);
        }

        private static string NamedCard(CardTextSlot slot, ICardDatabase pool)
        {
            if (!int.TryParse(slot.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || !pool.TryGet(id, out CardDefinition named))
                return slot.ToString();
            CardTranslation t = Table.Current(named, pool);
            if (t == null) return CardTextTemplate.English(slot.Bare, pool);
            string name = slot.Plural && t.Plural.Length > 0 ? t.Plural : t.Name;
            return named.Type == CardType.MINION && named.Attack.HasValue && named.Health.HasValue
                ? named.Attack.Value.ToString(CultureInfo.InvariantCulture) + "/" +
                  named.Health.Value.ToString(CultureInfo.InvariantCulture) + " " + name
                : name;
        }

        private static string Emphasis(string word, bool bold) => bold ? CardTextStyling.Emphasise(word) : word;
    }
}
