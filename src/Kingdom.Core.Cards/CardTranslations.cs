using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>One card in another language: its name (and the plural, for a card another card
    /// summons in twos), its text as a <see cref="CardTextTemplate"/>, its flavor and tooltip, and
    /// <see cref="Src"/>, the <see cref="CardTextTemplate.SourceHash"/> of the English it was
    /// translated from.</summary>
    public sealed class CardTranslation
    {
        public string Name { get; }
        public string Plural { get; }
        public string Text { get; }
        public string Flavor { get; }
        public string Tooltip { get; }
        public string Src { get; }

        public CardTranslation(string name, string plural, string text, string flavor, string tooltip, string src)
        {
            Name = name ?? "";
            Plural = plural ?? "";
            Text = text ?? "";
            Flavor = flavor ?? "";
            Tooltip = tooltip ?? "";
            Src = src ?? "";
        }
    }

    /// <summary>
    /// A language's card file, <c>values-&lt;lang&gt;/cards.json</c>: card id to
    /// <see cref="CardTranslation"/> (2026-10-20). Client-only by design — the card rows never carry
    /// a translation, because <c>CorpusDigest</c> hashes their bytes and a text fix would force a
    /// server redeploy.
    ///
    /// <para><b>A translation whose <c>src</c> no longer matches the card's English is stale, and a
    /// stale card shows in English, whole.</b> <see cref="Current"/> is the only read a display path
    /// should make. <see cref="Problems"/> is what CardCi reports.</para>
    ///
    /// <para>The same format, written from the English by <see cref="WriteSource"/>, is the file a
    /// translator starts from. See <c>docs/dev-docs/specs/localization.md</c>.</para>
    /// </summary>
    public sealed class CardTranslations
    {
        public static readonly CardTranslations Empty = new CardTranslations(new Dictionary<int, CardTranslation>());

        private readonly Dictionary<int, CardTranslation> _rows;
        private readonly Dictionary<int, CardTranslation?> _current = new Dictionary<int, CardTranslation?>();

        private CardTranslations(Dictionary<int, CardTranslation> rows) => _rows = rows;

        public IReadOnlyDictionary<int, CardTranslation> Rows => _rows;

        public static CardTranslations Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var rows = new Dictionary<int, CardTranslation>();
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("a card translation file is a JSON object keyed by card id.");
            foreach (JsonProperty row in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(row.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    throw new FormatException($"'{row.Name}' is not a card id.");
                rows[id] = new CardTranslation(Read(row.Value, "name"), Read(row.Value, "plural"), Read(row.Value, "text"),
                    Read(row.Value, "flavor"), Read(row.Value, "tooltip"), Read(row.Value, "src"));
            }
            return new CardTranslations(rows);
        }

        /// <summary>
        /// The translation of <paramref name="card"/>, or null when there is none, it is stale, or its
        /// English cannot be written from <paramref name="pool"/> (a card naming one the pool lacks).
        /// Checked once per card id and remembered.
        /// </summary>
        public CardTranslation? Current(CardDefinition card, ICardDatabase pool)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (!_rows.TryGetValue(card.Id, out CardTranslation row)) return null;
            lock (_current)
            {
                if (_current.TryGetValue(card.Id, out CardTranslation? known)) return known;
                string template;
                // Not remembered: a caller with a fuller pool may still be able to write it.
                try { template = CardTextGenerator.Template(card, pool); }
                catch (Exception) { return null; }
                CardTranslation? result =
                    row.Src == CardTextTemplate.SourceHash(card, template) && SameSlots(template, row.Text) ? row : null;
                _current[card.Id] = result;
                return result;
            }
        }

        /// <summary>What is wrong with <paramref name="row"/> as a translation of
        /// <paramref name="card"/> whose English template is <paramref name="template"/>; empty when
        /// nothing is. A stale <c>src</c> is reported separately, by <see cref="IsStale"/>.</summary>
        public static IReadOnlyList<string> Problems(CardDefinition card, string template, CardTranslation row)
        {
            var problems = new List<string>();
            if (row.Name.Trim().Length == 0) problems.Add("no name");
            if (!SameSlots(template, row.Text))
                problems.Add($"slots {Describe(row.Text)} where the English has {Describe(template)}");
            if (template.Length > 0 && row.Text.Trim().Length == 0) problems.Add("no text");
            if (card.FlavorText.Length > 0 && row.Flavor.Trim().Length == 0) problems.Add("no flavor");
            if (card.FlavorText.Length == 0 && row.Flavor.Length > 0) problems.Add("flavor the English does not have");
            if (card.Tooltip.Length > 0 && row.Tooltip.Trim().Length == 0) problems.Add("no tooltip");
            if (row.Text.IndexOf('[') >= 0 || row.Text.IndexOf('<') >= 0) problems.Add("markup in the text");
            return problems;
        }

        public static bool IsStale(CardDefinition card, string template, CardTranslation row)
            => row.Src != CardTextTemplate.SourceHash(card, template);

        /// <summary>True when both templates carry the same slots, in any order and any number of
        /// times each — a translation may move a slot, never add, drop or change one.</summary>
        public static bool SameSlots(string english, string translated)
            => Multiset(english).SequenceEqual(Multiset(translated));

        /// <summary>The English source file for <paramref name="cards"/>: the format a translation
        /// takes, with <c>text</c> as the English template and <c>src</c> filled in.</summary>
        public static string WriteSource(IEnumerable<CardDefinition> cards, ICardDatabase pool)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                List<CardDefinition> ordered = cards.OrderBy(c => c.Id).ToList();
                var templates = ordered.ToDictionary(c => c.Id, c => CardTextGenerator.Template(c, pool));
                HashSet<int> namedPlural = NamedInPlural(templates.Values);
                w.WriteStartObject();
                foreach (CardDefinition card in ordered)
                {
                    string template = templates[card.Id];
                    w.WriteStartObject(card.Id.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("name", card.Name);
                    if (namedPlural.Contains(card.Id)) w.WriteString("plural", CardTextGenerator.DescribePlural(card.Name));
                    w.WriteString("text", template);
                    if (card.FlavorText.Length > 0) w.WriteString("flavor", card.FlavorText);
                    if (card.Tooltip.Length > 0) w.WriteString("tooltip", card.Tooltip);
                    w.WriteString("src", CardTextTemplate.SourceHash(card, template));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }

        /// <summary>Every card id a template names in the plural (<c>{card:1191:plural}</c>): the
        /// cards whose translation must carry a <see cref="CardTranslation.Plural"/>.</summary>
        public static HashSet<int> NamedInPlural(IEnumerable<string> templates)
        {
            var ids = new HashSet<int>();
            foreach (string template in templates)
                foreach (CardTextSlot slot in CardTextTemplate.Slots(template))
                    if (slot.Kind == CardTextSlotKind.Card && slot.Plural
                        && int.TryParse(slot.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                        ids.Add(id);
            return ids;
        }

        private static List<string> Multiset(string template)
            => CardTextTemplate.Slots(template).Select(s => s.Bare.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();

        private static string Describe(string template)
        {
            List<string> slots = Multiset(template);
            return slots.Count == 0 ? "(none)" : string.Join(" ", slots);
        }

        private static string Read(JsonElement row, string field)
            => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out JsonElement v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
