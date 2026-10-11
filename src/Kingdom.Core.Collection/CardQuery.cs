using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>Primary sort key for a card-pool query (ui/collection-deckbuilder.md §2 grid).</summary>
    public enum CardSortKey { Cost, Name, Rarity, Class, Type, Attack, Health }

    /// <summary>
    /// A total order over the pool. The primary <see cref="Key"/> may run descending; ties always break
    /// by Name then Id ascending, so the grid is <b>deterministic</b> regardless of the enumeration
    /// order <see cref="ICardDatabase.AllCollectible"/> hands back (a dictionary makes no such promise).
    /// </summary>
    public readonly struct CardSort
    {
        public readonly CardSortKey Key;
        public readonly bool Descending;
        public CardSort(CardSortKey key, bool descending = false) { Key = key; Descending = descending; }

        /// <summary>The grid's opening order: cheapest first (ui/collection-deckbuilder.md §2).</summary>
        public static readonly CardSort Default = new CardSort(CardSortKey.Cost, descending: false);
    }

    /// <summary>
    /// A card-pool query. Every facet is optional: a null/empty facet places no constraint, so the
    /// empty filter matches the whole pool. Facets combine as <b>AND across facets, OR within a
    /// facet</b> — the standard deckbuilder mental model (any of the ticked rarities, and any of the
    /// ticked classes, and …). Set-valued facets are membership tests; <see cref="Text"/> is the
    /// free-text search described on <see cref="CardQuery"/>.
    /// </summary>
    public sealed class CardFilter
    {
        /// <summary>Free-text query. Whitespace-tokenised; see <see cref="CardQuery"/> for the field union.</summary>
        public string? Text { get; set; }

        public IReadOnlyCollection<CardClass>? Classes { get; set; }
        public IReadOnlyCollection<Rarity>? Rarities { get; set; }
        public IReadOnlyCollection<CardType>? Types { get; set; }

        /// <summary>Tribe membership. A card with no tribe is treated as <see cref="Tribe.NONE"/>.</summary>
        public IReadOnlyCollection<Tribe>? Tribes { get; set; }

        /// <summary>Keyword facet: a card matches if it carries <b>any</b> of the selected keywords.</summary>
        public IReadOnlyCollection<Keyword>? Keywords { get; set; }

        /// <summary>Inclusive lower bound on <see cref="CardDefinition.Cost"/>. Null = no floor. (Cost 7+ = MinCost 7.)</summary>
        public int? MinCost { get; set; }

        /// <summary>Inclusive upper bound on <see cref="CardDefinition.Cost"/>. Null = no ceiling.</summary>
        public int? MaxCost { get; set; }
    }

    /// <summary>
    /// Read-side search over the card pool for the deckbuilder collection grid
    /// (ui/collection-deckbuilder.md §2). Pure and UnityEngine-free — the same predicate the grid runs
    /// live is directly unit-testable, and nothing here allocates a clock or a float (ADR-0003).
    ///
    /// <para><b>Free-text is a union across five fields</b> (the spec's phrasing): a token matches when
    /// it is a case-insensitive substring of the card's <see cref="CardDefinition.Name"/>, generated
    /// rule text, any <see cref="CardDefinition.Keywords"/> name,
    /// its <see cref="CardDefinition.Type"/> name, or its <see cref="CardDefinition.Tribe"/> name. A
    /// multi-token query is an <b>AND of tokens</b> — every token must land in some field — so "beast
    /// bulwark" finds a Bulwark whose rules read "your Beasts", not only cards literally named that.
    /// Flavour text and id are deliberately excluded: neither is what a builder is searching by.</para>
    /// </summary>
    public static class CardQuery
    {
        /// <summary>Search the collectible pool of a database (the deckbuilder's source).</summary>
        public static IReadOnlyList<CardDefinition> Search(ICardDatabase db, CardFilter? filter = null, CardSort? sort = null)
            => Search(db != null ? db.AllCollectible() : Array.Empty<CardDefinition>(), filter, sort);

        /// <summary>Filter then sort an explicit pool. Never mutates the input; returns a fresh list.</summary>
        public static IReadOnlyList<CardDefinition> Search(IEnumerable<CardDefinition> pool, CardFilter? filter = null, CardSort? sort = null)
        {
            var result = new List<CardDefinition>();
            if (pool == null) return result;

            foreach (var card in pool)
                if (card != null && Matches(card, filter)) result.Add(card);

            var order = sort ?? CardSort.Default;
            result.Sort((a, b) => Compare(a, b, order));
            return result;
        }

        /// <summary>
        /// Does one card satisfy the filter? Exposed so the grid can decide a single tile's visibility
        /// without re-running the whole search — the counterpart to <see cref="DeckValidator.CanAdd"/>.
        /// A null filter matches everything.
        /// </summary>
        public static bool Matches(CardDefinition card, CardFilter? filter)
        {
            if (card == null) return false;
            if (filter == null) return true;

            if (filter.MinCost.HasValue && card.Cost < filter.MinCost.Value) return false;
            if (filter.MaxCost.HasValue && card.Cost > filter.MaxCost.Value) return false;

            if (HasAny(filter.Classes) && !Contains(filter.Classes!, card.Class)) return false;
            if (HasAny(filter.Rarities) && !Contains(filter.Rarities!, card.Rarity)) return false;
            if (HasAny(filter.Types) && !Contains(filter.Types!, card.Type)) return false;
            if (HasAny(filter.Tribes) && !Contains(filter.Tribes!, card.Tribe ?? Tribe.NONE)) return false;
            if (HasAny(filter.Keywords) && !CarriesAny(card, filter.Keywords!)) return false;

            if (!string.IsNullOrWhiteSpace(filter.Text) && !MatchesText(card, filter.Text!)) return false;

            return true;
        }

        // ── free text ──────────────────────────────────────────────────────────────

        private static bool MatchesText(CardDefinition card, string query)
        {
            string haystack = Haystack(card);
            foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;   // AND of tokens: one miss disqualifies the card.
            return true;
        }

        /// <summary>
        /// The five searchable fields joined with '\n'. The separator is a whitespace char, so a
        /// whitespace-split token can never contain it — which means a token can only match inside one
        /// field, never across a boundary (no "fire" + "ball" → "fireball" false hit).
        /// </summary>
        private static string Haystack(CardDefinition card)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(card.Name).Append('\n');
            sb.Append(card.Text).Append('\n');
            foreach (var kw in card.Keywords) sb.Append(kw).Append('\n');
            sb.Append(card.Type).Append('\n');
            sb.Append(card.Tribe ?? Tribe.NONE);
            return sb.ToString();
        }

        // ── sort ─────────────────────────────────────────────────────────────────────

        private static int Compare(CardDefinition a, CardDefinition b, CardSort sort)
        {
            int primary = KeyCompare(a, b, sort.Key);
            if (sort.Descending) primary = -primary;
            if (primary != 0) return primary;

            // Total order: equal primaries break by Name then Id, always ascending, so the grid is
            // byte-stable across runs no matter how the pool was enumerated.
            int byName = string.CompareOrdinal(a.Name, b.Name);
            if (byName != 0) return byName;
            return a.Id.CompareTo(b.Id);
        }

        private static int KeyCompare(CardDefinition a, CardDefinition b, CardSortKey key) => key switch
        {
            CardSortKey.Cost => a.Cost.CompareTo(b.Cost),
            CardSortKey.Name => string.CompareOrdinal(a.Name, b.Name),
            CardSortKey.Rarity => ((int)a.Rarity).CompareTo((int)b.Rarity),
            CardSortKey.Class => ((int)a.Class).CompareTo((int)b.Class),
            CardSortKey.Type => ((int)a.Type).CompareTo((int)b.Type),
            // Spells/weapons carry no attack/health; a missing stat sorts before 0 so the statless
            // cards group at the ascending end rather than scattering through the curve.
            CardSortKey.Attack => Nullable(a.Attack).CompareTo(Nullable(b.Attack)),
            CardSortKey.Health => Nullable(a.Health).CompareTo(Nullable(b.Health)),
            _ => 0,
        };

        private static int Nullable(int? stat) => stat ?? int.MinValue;

        // ── facet helpers ─────────────────────────────────────────────────────────────

        private static bool HasAny<T>(IReadOnlyCollection<T>? set) => set != null && set.Count > 0;

        private static bool Contains<T>(IReadOnlyCollection<T> set, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            foreach (var item in set)
                if (cmp.Equals(item, value)) return true;
            return false;
        }

        private static bool CarriesAny(CardDefinition card, IReadOnlyCollection<Keyword> wanted)
        {
            foreach (var kw in card.Keywords)
                if (Contains(wanted, kw)) return true;
            return false;
        }
    }
}
