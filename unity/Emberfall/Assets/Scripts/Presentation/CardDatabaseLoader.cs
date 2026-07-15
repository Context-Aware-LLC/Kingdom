using Emberfall.Core.Contracts;
using Emberfall.Data;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Loads the canonical card database (Assets/CardData/M1.cards.json) as an
    /// <see cref="ICardDatabase"/> — the same path GameBootstrap uses. Tries the
    /// "cards/M1.cards" Addressables address first (the runtime path); in the Editor it falls
    /// back to a direct AssetDatabase load so tools like the deckbuilder spike work even before
    /// the Addressables content catalog has been built.
    /// </summary>
    public static class CardDatabaseLoader
    {
        private const string Address = "cards/M1.cards";
        private const string EditorPath = "Assets/CardData/M1.cards.json";

        public static ICardDatabase Load()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<TextAsset>(Address);
            TextAsset asset = handle.WaitForCompletion();
            if (asset != null)
                return new JsonCardDatabase(asset.text);

#if UNITY_EDITOR
            var direct = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(EditorPath);
            if (direct != null)
            {
                Debug.LogWarning($"[CardDatabaseLoader] Addressable '{Address}' not built; " +
                                 $"loaded {EditorPath} directly (Editor fallback).");
                return new JsonCardDatabase(direct.text);
            }
#endif
            throw new System.Exception(
                $"Card data not found — '{Address}' not in Addressables. " +
                "Run Emberfall/Addressables/Setup Groups + Migrate Card Data.");
        }
    }
}
