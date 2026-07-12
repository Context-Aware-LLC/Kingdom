#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Editor-only IUiSpriteProvider: resolves frame/gem/icon sprites straight from AssetDatabase
    // under Assets/Art/UI/. Serves the M1 vertical slice (in-editor play mode) until the UI Sprite
    // Atlas group exists — then an AtlasUiSpriteProvider replaces it in builds (pipeline.md §2).
    //
    // Results are cached: UI sprites are few, always-resident, and requested repeatedly (every card
    // build + every keyword-change rebuild), so a per-key lookup once is plenty.
    public sealed class EditorUiSpriteProvider : IUiSpriteProvider
    {
        private readonly Dictionary<string, Sprite> _cache = new();

        public Sprite Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out Sprite cached)) return cached;

            Sprite result = null;
            foreach (string guid in AssetDatabase.FindAssets($"t:Sprite {key}", new[] { "Assets/Art/UI" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets matches loosely; require the exact file name.
                if (!System.IO.Path.GetFileNameWithoutExtension(path)
                        .Equals(key, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                result = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                break;
            }

            _cache[key] = result; // cache misses too — a missing placeholder shouldn't re-scan every frame.
            return result;
        }
    }
}
#endif
