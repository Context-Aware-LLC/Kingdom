using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Emberfall.Presentation
{
    // Shipping IArtProvider (dev-docs/pipelines/art-and-rendering.md:13): resolves artId → Addressables key
    // "card_art/<flattened artId>" and streams the sprite on demand. Replaces
    // EditorArtProvider in player builds.
    //
    // Handles are cached for the lifetime of the provider (one match). Per-set
    // eviction (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §6 LOD/release strategy) is deferred until the mobile
    // memory ceiling makes it real work — desktop alpha holds every loaded sprite.
    public sealed class AddressableArtProvider : IArtProvider
    {
        private readonly Dictionary<string, AsyncOperationHandle<Sprite>> _handles = new();

        public void LoadArt(string artId, Action<Sprite> onLoaded)
        {
            string fileName = ArtIds.ToFileName(artId);
            if (fileName.Length == 0) { onLoaded?.Invoke(null); return; }

            string key = $"card_art/{fileName}";

            if (_handles.TryGetValue(key, out var cached))
            {
                if (cached.IsDone) { onLoaded?.Invoke(cached.Status == AsyncOperationStatus.Succeeded ? cached.Result : null); }
                else cached.Completed += h => onLoaded?.Invoke(h.Status == AsyncOperationStatus.Succeeded ? h.Result : null);
                return;
            }

            var handle = Addressables.LoadAssetAsync<Sprite>(key);
            _handles[key] = handle;
            handle.Completed += h =>
            {
                if (h.Status != AsyncOperationStatus.Succeeded)
                    Debug.LogWarning($"[AddressableArtProvider] no art at key '{key}' (artId '{artId}')");
                onLoaded?.Invoke(h.Status == AsyncOperationStatus.Succeeded ? h.Result : null);
            };
        }

        public void ReleaseAll()
        {
            foreach (var handle in _handles.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
        }
    }
}
