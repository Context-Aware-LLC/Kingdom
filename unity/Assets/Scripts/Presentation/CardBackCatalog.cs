using System.Collections.Generic;
using Kingdom.Core.Collection;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>Unity art resolution for the shared numeric card-back catalog. The picker filters
    /// these definitions through the server's ownership response. Match backs still use card_back;
    /// per-seat cosmetic rendering must be wired before adding a second live back.</summary>
    public static class CardBackCatalog
    {
        public const string ArtFolder = "Assets/Art/Cards/Backs";
        public static IReadOnlyList<CardBackDefinition> All => CardBackDefinitions.All;
        public static CardBackDefinition Default => CardBackDefinitions.All[0];
        public static bool HasChoice => All.Count > 1;

        public static CardBackDefinition Resolve(int id) =>
            CardBackDefinitions.TryGet(id, out var back) ? back : Default;

        public static Sprite SpriteFor(int id, IUiSpriteProvider ui)
            => ui?.Get(Resolve(id).SpriteKey);

        public static IUiSpriteProvider NewProvider()
        {
#if UNITY_EDITOR
            return new EditorUiSpriteProvider();
#else
            return new AddressableUiSpriteProvider();
#endif
        }
    }
}
