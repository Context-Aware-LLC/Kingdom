using System;
using UnityEngine;

namespace Emberfall.Presentation
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
        // Card data uses "art/<cardId>" ids; asset filenames use the flat "art_<cardId>"
        // form (dev-docs/specs/card-generation-prompt.md:18 — filenames never contain path separators).
        public static string ToFileName(string artId) =>
            string.IsNullOrEmpty(artId) ? "" : artId.Replace('/', '_');
    }
}
