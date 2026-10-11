#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace Kingdom.Presentation
{
    // Editor-only IUiTextProvider: resolves UI data files straight from AssetDatabase under
    // Assets/Art/UI/, the EditorUiSpriteProvider recipe with t:TextAsset in place of t:Sprite.
    // Searching the whole tree rather than a fixed path means a file can move between subfolders
    // without a code change, which is how the sprite side already behaves.
    //
    // Results are cached, misses included: geometry is read once per socket build but that is once
    // per hero per match, and a missing file shouldn't re-scan the tree each time.
    public sealed class EditorUiTextProvider : IUiTextProvider
    {
        private readonly Dictionary<string, string> _cache = new();

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out string cached)) return cached;

            string result = null;
            foreach (string guid in AssetDatabase.FindAssets($"t:TextAsset {key}", new[] { "Assets/Art/UI" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets matches loosely; require the exact file name.
                if (!System.IO.Path.GetFileNameWithoutExtension(path)
                        .Equals(key, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(path);
                if (asset == null) continue;
                result = asset.text;
                break;
            }

            _cache[key] = result;
            return result;
        }
    }
}
#endif
