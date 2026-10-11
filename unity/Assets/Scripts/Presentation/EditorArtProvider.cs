#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Presentation
{
    // Editor-only IArtProvider: resolves art straight from AssetDatabase under Assets/Art/.
    // Serves the M1 vertical slice (in-editor play mode) until Addressables groups exist —
    // then AddressableArtProvider replaces it in builds (dev-docs/pipelines/art-and-rendering.md:13).
    public sealed class EditorArtProvider : IArtProvider
    {
        public void LoadArt(string artId, Action<Sprite> onLoaded)
        {
            string fileName = ArtIds.ToFileName(artId);
            if (fileName.Length == 0) { onLoaded?.Invoke(null); return; }

            foreach (string guid in AssetDatabase.FindAssets($"t:Sprite {fileName}", new[] { "Assets/Art" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets matches loosely; require the exact file name.
                if (!System.IO.Path.GetFileNameWithoutExtension(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                onLoaded?.Invoke(AssetDatabase.LoadAssetAtPath<Sprite>(path));
                return;
            }
            onLoaded?.Invoke(null);
        }
    }
}
#endif
