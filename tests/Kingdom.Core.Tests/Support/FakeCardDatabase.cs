using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;

namespace Kingdom.Core.Tests.Support
{
    /// <summary>
    /// Minimal in-memory <see cref="ICardDatabase"/> for L1 unit tests — returns a vanilla 1/1 minion
    /// for any requested id (the foundation turn-loop never reads card stats; it only moves instances).
    /// </summary>
    public sealed class FakeCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _cache = new();

        /// <summary>
        /// Declare every equipped weapon on a board as a WEAPON row.
        ///
        /// <para><b>Call this in any fixture whose assertions can see a card's TYPE.</b>
        /// <see cref="Get"/> below fabricates a <c>CardType.MINION</c> row for any id it was not
        /// handed, and <see cref="TryGet"/> returns true unconditionally — so an unregistered weapon
        /// silently reads as a minion and nothing fails. That was invisible while the resolver keyed
        /// on the fused <c>TargetCandidateKind</c>, which took the weapon's identity from its ZONE
        /// and never asked the database at all. The type axis is real now and it asks.</para>
        ///
        /// <para>Weapons are the only case that bites today, because the weapon slot is the one
        /// public zone whose occupant is not a minion. A fixture that seeds spells in hand and
        /// asserts on their type needs the same treatment, by hand.</para>
        /// </summary>
        public FakeCardDatabase DeclareWeapons(GameState state)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                CardInstance? weapon = state.Players[seat].Weapon;
                if (weapon == null) continue;
                Register(new CardDefinition(
                    id: weapon.DefinitionId, handle: $"w{weapon.DefinitionId}",
                    name: $"w{weapon.DefinitionId}", type: CardType.WEAPON, cost: 1,
                    attack: 1, health: null, durability: weapon.CurrentDurability ?? 1,
                    @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                    keywords: System.Array.Empty<Keyword>(),
                    rules: System.Array.Empty<RuleDefinition>(),
                    flavorText: "", artId: $"w{weapon.DefinitionId}", setId: SetId.CORE,
                    collectible: true, tribe: null, factionId: ""));
            }
            return this;
        }

        /// <summary>Register an explicit definition (with effects/keywords) so interpreter tests can author behavior.</summary>
        public FakeCardDatabase Register(CardDefinition def)
        {
            _cache[def.Id] = def;
            return this;
        }

        public bool TryGet(int cardId, out CardDefinition def)
        {
            def = Get(cardId);
            return true;
        }

        public CardDefinition Get(int cardId)
        {
            if (!_cache.TryGetValue(cardId, out var d))
            {
                d = new CardDefinition(
                    id: cardId, handle: $"c{cardId}", name: $"c{cardId}", type: CardType.MINION, cost: 1,
                    attack: 1, health: 1, durability: null,
                    @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                    keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
                    flavorText: "", artId: $"art/c{cardId}", setId: SetId.CORE,
                    collectible: true, tribe: null, factionId: "");
                _cache[cardId] = d;
            }
            return d;
        }

        public IReadOnlyCollection<CardDefinition> AllCollectible() => System.Array.Empty<CardDefinition>();

        /// <summary>
        /// A fixed slice deck of <paramref name="n"/> distinct card ids (deterministic, ordered),
        /// counting up from <paramref name="first"/>. Callers pass an id in the MINION band (1001+)
        /// unless the fixture is about some other type.
        /// </summary>
        public static List<int> Deck(int first, int n)
        {
            var list = new List<int>(n);
            for (int i = 0; i < n; i++) list.Add(first + i);
            return list;
        }
    }
}
