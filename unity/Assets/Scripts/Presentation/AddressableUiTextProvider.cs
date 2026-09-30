using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Emberfall.Presentation
{
    // Shipping IUiTextProvider: resolves UI data-file keys from the UI_Core Addressables group at
    // address "ui/<key>", the same address shape AddressableUiSpriteProvider uses, because these
    // files sit in the same group beside the chrome they describe. Get is a synchronous contract
    // and the payload is under a kilobyte, so the first request per key uses WaitForCompletion —
    // a boot-time cost, not a per-frame one, with results (misses included) cached.
    public sealed class AddressableUiTextProvider : IUiTextProvider
    {
        private readonly Dictionary<string, string> _cache = new();

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out string cached)) return cached;

            var handle = Addressables.LoadAssetAsync<TextAsset>($"ui/{key}");
            TextAsset asset = handle.WaitForCompletion();
            string result = null;
            if (handle.Status == AsyncOperationStatus.Succeeded && asset != null)
                result = asset.text;
            else
                Debug.LogWarning($"[AddressableUiTextProvider] no UI text asset at key 'ui/{key}'");

            _cache[key] = result;
            return result;
        }
    }
}
