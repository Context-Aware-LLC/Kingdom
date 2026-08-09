using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Loads all card sets (Emberfall, Rekindling) as one merged <see cref="ICardDatabase"/>.
    /// Tries each Addressables address first; in the Editor falls back to direct AssetDatabase
    /// loads so tools work before the Addressables catalog has been built.
    /// </summary>
    public static class CardDatabaseLoader
    {
        // All registered card-set Addressables addresses and their Editor fallback paths.
        private static readonly (string address, string editorPath)[] CardSets =
        {
            ("cards/Emberfall.cards", "Assets/CardData/Emberfall.cards.json"),
            ("cards/Rekindling.cards", "Assets/CardData/Rekindling.cards.json"),
        };

        public static ICardDatabase Load()
        {
            var payloads = new List<string>();
            foreach (var (address, editorPath) in CardSets)
            {
                var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<TextAsset>(address);
                TextAsset asset = handle.WaitForCompletion();
                if (asset != null)
                {
                    payloads.Add(asset.text);
                    continue;
                }

#if UNITY_EDITOR
                var direct = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(editorPath);
                if (direct != null)
                {
                    Debug.LogWarning($"[CardDatabaseLoader] Addressable '{address}' not built; " +
                                     $"loaded {editorPath} directly (Editor fallback).");
                    payloads.Add(direct.text);
                    continue;
                }
#endif
                throw new System.Exception(
                    $"Card data not found — '{address}' not in Addressables. " +
                    "Run Emberfall/Addressables/Setup Groups + Migrate Card Data.");
            }

            return new JsonCardDatabase(payloads);
        }
    }
}
