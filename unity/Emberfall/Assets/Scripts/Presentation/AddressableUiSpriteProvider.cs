using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Emberfall.Presentation
{
    // Shipping IUiSpriteProvider (pipeline.md §2): resolves frame/gem/icon keys from the
    // UI_Core Addressables group at address "ui/<key>". UI chrome is small and always
    // resident, and IUiSpriteProvider.Get is a synchronous contract, so the first request
    // per key uses WaitForCompletion — a boot-time cost, not a per-frame one, because
    // results (including misses) are cached exactly like EditorUiSpriteProvider.
    public sealed class AddressableUiSpriteProvider : IUiSpriteProvider
    {
        private readonly Dictionary<string, Sprite> _cache = new();

        public Sprite Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out Sprite cached)) return cached;

            Sprite result = null;
            var handle = Addressables.LoadAssetAsync<Sprite>($"ui/{key}");
            result = handle.WaitForCompletion();
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogWarning($"[AddressableUiSpriteProvider] no UI sprite at key 'ui/{key}'");
                result = null;
            }

            _cache[key] = result; // cache misses too — a missing placeholder shouldn't re-hit Addressables.
            return result;
        }
    }
}
