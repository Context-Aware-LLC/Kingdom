using System;
using UnityEngine;

namespace Kingdom.Presentation
{
    // Art resolution seam (dev-docs/pipelines/art-and-rendering.md:13): CardView/presenter code asks
    // for art by artId and never knows where it comes from. The shipping implementation is
    // AddressableArtProvider (key "card_art/<artId>"); EditorArtProvider serves the vertical
    // slice from AssetDatabase until Addressables groups are configured.
    public interface IArtProvider
    {
        // Invokes onLoaded with the sprite, or with null if the art doesn't exist (yet).
        // May complete synchronously; callers must not assume a frame delay.
        void LoadArt(string artId, Action<Sprite> onLoaded);
    }

    public static class ArtIds
    {
        // An artId IS the asset filename (2026-08-06). Card data used to namespace them as
        // "art/<cardId>", flattened to "art_<cardId>" on disk; that namespace had one value
        // across every row and was dropped. This stays a Replace rather than an identity so a
        // future namespaced id (should one ever be introduced) still flattens correctly -
        // filenames never contain path separators.
        public static string ToFileName(string artId) =>
            string.IsNullOrEmpty(artId) ? "" : artId.Replace('/', '_');
    }
}
