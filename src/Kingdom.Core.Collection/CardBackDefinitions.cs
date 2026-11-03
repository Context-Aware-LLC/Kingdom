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
        // Never renumber or reuse an ID. Zero means an unset deck selection. A new back takes the
        // next free ID. 2-9 were assigned together (#421, Sam 2026-11-03): the three place backs
        // in place order, then the pool in the order Sam listed it. Only ID 1 is granted at sign-up.
        public const int DefaultId = 1;
        public static IReadOnlyList<CardBackDefinition> All { get; } = Array.AsReadOnly(new[]
        {
            new CardBackDefinition(DefaultId, "gravekeep_allover", "Gravekeep", "card_back"),
            new CardBackDefinition(2, "moonlit_orrery", "Moonlit Orrery", "card_back_moonlit_orrery"),
            new CardBackDefinition(3, "tideglass", "Tideglass", "card_back_tideglass"),
            new CardBackDefinition(4, "verdant_reliquary", "Verdant Reliquary", "card_back_verdant_reliquary"),
            new CardBackDefinition(5, "gravekeep_bell", "Gravekeep Bell", "card_back_gravekeep_bell"),
            new CardBackDefinition(6, "verdant_acorn", "Verdant Acorn", "card_back_verdant_acorn"),
            new CardBackDefinition(7, "emberweave", "Emberweave", "card_back_emberweave"),
            new CardBackDefinition(8, "rekindled_seal", "Rekindled Seal", "card_back_rekindled_seal"),
            new CardBackDefinition(9, "wayfarers_knot", "Wayfarer's Knot", "card_back_wayfarers_knot"),
        });

        /// <summary>The King's Favor place backs, 1st, 2nd and 3rd (#421): exactly three ids, each a
        /// row of <see cref="All"/>, none of them in <see cref="PoolIds"/>. Emptying this makes the
        /// Tuesday sweep pay gold only and hold every top-three back; emptying the pool while this
        /// is filled settles every repeat holder with PoolEmptyGold, permanently.</summary>
        public static IReadOnlyList<int> PlaceBackIds { get; } = Array.AsReadOnly(new[] { 2, 3, 4 });

        /// <summary>The backs a player picks from on taking a place they have held before.</summary>
        public static IReadOnlyList<int> PoolIds { get; } = Array.AsReadOnly(new[] { 5, 6, 7, 8, 9 });

        public static bool TryGet(int id, out CardBackDefinition definition)
        {
            foreach (var back in All)
                if (back.Id == id) { definition = back; return true; }
            definition = default;
            return false;
        }
    }
}
