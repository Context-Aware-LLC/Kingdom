using System;
using System.Collections.Generic;

namespace Kingdom.Core.Collection
{
    /// <summary>A cosmetic identity shared by the client and server. Handles are descriptive;
    /// persistence and entitlements always use the positive numeric ID.</summary>
    public readonly struct CardBackDefinition
    {
        public int Id { get; }
        public string Handle { get; }
        public string Name { get; }
        public string SpriteKey { get; }

        public CardBackDefinition(int id, string handle, string name, string spriteKey)
        {
            Id = id;
            Handle = handle;
            Name = name;
            SpriteKey = spriteKey;
        }
    }

    public static class CardBackDefinitions
    {
        // Never renumber or reuse an ID. Zero means an unset deck selection.
        public const int DefaultId = 1;
        public static IReadOnlyList<CardBackDefinition> All { get; } = Array.AsReadOnly(new[]
        {
            new CardBackDefinition(DefaultId, "gravekeep_allover", "Gravekeep", "card_back"),
        });

        /// <summary>The King's Favor place backs, 1st, 2nd and 3rd (#421): exactly three ids, or
        /// none. With none the Tuesday sweep pays gold and holds every top-three back until the
        /// first sweep after they land here. Ids here must also be rows of <see cref="All"/>.</summary>
        // RETIREMENT TRIGGER: empty only because the art does not exist yet (plan Task 10). Filling
        // these is the whole of wiring them: KingPayoutSweep already reads both lists.
        public static IReadOnlyList<int> PlaceBackIds { get; } = Array.AsReadOnly(new int[0]);

        /// <summary>The backs a player picks from on taking a place they have held before.</summary>
        public static IReadOnlyList<int> PoolIds { get; } = Array.AsReadOnly(new int[0]);

        public static bool TryGet(int id, out CardBackDefinition definition)
        {
            foreach (var back in All)
                if (back.Id == id) { definition = back; return true; }
            definition = default;
            return false;
        }
    }
}
