using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// Minimal in-memory <see cref="ICardDatabase"/> for L1 unit tests — returns a vanilla 1/1 minion
    /// for any requested id (the foundation turn-loop never reads card stats; it only moves instances).
    /// </summary>
    public sealed class FakeCardDatabase : ICardDatabase
    {
        private readonly Dictionary<string, CardDefinition> _cache = new();

        /// <summary>Register an explicit definition (with effects/keywords) so interpreter tests can author behavior.</summary>
        public FakeCardDatabase Register(CardDefinition def)
        {
            _cache[def.Id] = def;
            return this;
        }

        public bool TryGet(string cardId, out CardDefinition def)
        {
            def = Get(cardId);
            return true;
        }

        public CardDefinition Get(string cardId)
        {
            if (!_cache.TryGetValue(cardId, out var d))
            {
                d = new CardDefinition(
                    id: cardId, name: cardId, type: CardType.MINION, cost: 1,
                    attack: 1, health: 1, durability: null,
                    @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                    keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
                    cardText: "", flavorText: "", artId: "art/" + cardId, setId: SetId.CORE,
                    collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);
                _cache[cardId] = d;
            }
            return d;
        }

        public IReadOnlyCollection<CardDefinition> AllCollectible() => System.Array.Empty<CardDefinition>();

        /// <summary>A fixed slice deck of <paramref name="n"/> distinct card ids (deterministic, ordered).</summary>
        public static List<string> Deck(string prefix, int n)
        {
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add($"{prefix}_{i:00}");
            return list;
        }
    }
}
