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

        public static bool TryGet(int id, out CardBackDefinition definition)
        {
            foreach (var back in All)
                if (back.Id == id) { definition = back; return true; }
            definition = default;
            return false;
        }
    }
}
