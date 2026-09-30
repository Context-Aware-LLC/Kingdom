using System;
using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Reads <c>Assets/Art/Shop/Packs/&lt;setId&gt;/</c> — the manifest, then the sprites it names.
    /// <see cref="PackArtSet"/> offers the candidate folders most specific first, and THE FIRST ONE
    /// WITH A MANIFEST WINS: <c>pack.emberfall.starter</c> takes <c>STARTER</c> when that folder has
    /// been drawn and falls through to <c>EMBERFALL</c> when it has not.
    /// Addressables in a build, AssetDatabase in the Editor, the same two-path shape
    /// <see cref="CardDatabaseLoader"/> uses so tools work before a catalog has been built.
    ///
    /// <para><b>The fall-through is silent, and in a player it can hide a packaging step.</b> A set
    /// folder that exists on disk but was never registered as Addressable has no manifest AT
    /// RUNTIME, so a build quietly wears the expansion's art while the Editor wears the SKU's. That
    /// is the same divergence <c>AddressablesSetup</c> exists to close — re-run
    /// <c>Emberfall/Addressables/Setup Groups + Migrate Card Data</c> after adding a folder — but
    /// with a fallback in front of it there is now no missing picture to notice.</para>
    ///
    /// <para><b>Adding an expansion's art is a content drop, not a code change.</b> Drop the folder,
    /// write its manifest, re-run <c>Emberfall/Addressables/Setup Groups + Migrate Card Data</c>.
    /// Nothing here and nothing in the transport layer names a set.</para>
    ///
    /// <para><b>Two silences are deliberate, and they are not the same silence.</b> A set with no
    /// manifest logs nothing — art that has not been drawn yet is the normal state of an
    /// expansion, not a fault. A manifest entry whose file will not resolve DOES warn: something
    /// declared that piece, so its absence is a mistake rather than an absence.</para>
    ///
    /// <para>Results are cached per PACK ID, misses included — a pack tile re-renders on every wallet
    /// refresh and on every busy-state flip, and re-hitting Addressables for each is wasted work.
    /// The cache is per instance and per session; there is no invalidation, because art does not
    /// change under a running player. In the Editor it means a newly imported file needs a domain
    /// reload to appear, which is the same rule the UI sprite providers already follow.</para>
    /// </summary>
    public sealed class PackArtProvider : IPackArtProvider
    {
        private const string ArtRoot = "Assets/Art/Shop/Packs";
        private const string ManifestFileName = "packart.json";

        private readonly Dictionary<string, IReadOnlyList<PackArtPiece>> _cache =
            new Dictionary<string, IReadOnlyList<PackArtPiece>>();

        public void LoadPackArt(string packId, Action<IReadOnlyList<PackArtPiece>> onLoaded)
        {
            if (onLoaded == null) return;

            if (!_cache.TryGetValue(packId ?? "", out IReadOnlyList<PackArtPiece> pieces))
            {
                pieces = Resolve(PackArtSet.SetIdsFor(packId));
                _cache[packId ?? ""] = pieces;
            }

            // Synchronous by contract, like IBoosterPackagingArtProvider before it: both paths
            // resolve inline. The callback shape is kept for a future streamed loader.
            onLoaded(pieces);
        }

        /// <summary>Take the first candidate folder that has a manifest. A manifest present but
        /// EMPTY still wins — the folder has spoken, and falling through would paint another set's
        /// art over a deliberate blank.</summary>
        private static IReadOnlyList<PackArtPiece> Resolve(IReadOnlyList<string> setIds)
        {
            for (int i = 0; i < setIds.Count; i++)
            {
                PackArtManifest manifest = LoadManifest(setIds[i]);
                if (manifest != null) return Build(setIds[i], manifest);
            }
            return Array.Empty<PackArtPiece>();
        }

        private static IReadOnlyList<PackArtPiece> Build(string setId, PackArtManifest manifest)
        {
            if (manifest.pieces == null || manifest.pieces.Count == 0)
                return Array.Empty<PackArtPiece>();

            var pieces = new List<PackArtPiece>(manifest.pieces.Count);
            for (int i = 0; i < manifest.pieces.Count; i++)
            {
                PackArtManifestEntry entry = manifest.pieces[i];
                if (entry == null || string.IsNullOrEmpty(entry.id) || string.IsNullOrEmpty(entry.file))
                {
                    Debug.LogWarning($"[PackArt] {setId}/{ManifestFileName} entry {i} is missing an id or file; skipped.");
                    continue;
                }

                Sprite sprite = LoadSprite(setId, entry.file);
                if (sprite == null)
                {
                    Debug.LogWarning($"[PackArt] {setId}/{ManifestFileName} declares '{entry.id}' at file " +
                                     $"'{entry.file}', which did not resolve. Is it in the folder, and have " +
                                     "Addressables groups been re-run since it was added?");
                    continue;
                }

                pieces.Add(new PackArtPiece(entry.id, string.IsNullOrEmpty(entry.title) ? entry.id : entry.title, sprite));
            }
            return pieces;
        }

        private static PackArtManifest LoadManifest(string setId)
        {
            // ASK BEFORE FETCHING. LoadAssetAsync on an absent key throws InvalidKeyException —
            // and Addressables LOGS it as an error first, so a catch cannot keep it out of the
            // console. In the Editor the miss is the NORMAL path (AssetDatabase serves authoring
            // before any content build), which turned an expected fallback into a red error every
            // session. Checking locations first is the only way to make the miss silent.
            TextAsset asset = LoadAddressable<TextAsset>($"pack_art/{setId}.manifest");

#if UNITY_EDITOR
            if (asset == null)
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>($"{ArtRoot}/{setId}/{ManifestFileName}");
#endif
            if (asset == null) return null;

            try
            {
                return JsonUtility.FromJson<PackArtManifest>(asset.text);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackArt] {setId}/{ManifestFileName} did not parse: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Load an Addressable, or null if the key is not in the catalog — without the
        /// console error a missing key otherwise produces. See LoadManifest for why that matters.</summary>
        private static T LoadAddressable<T>(string key) where T : UnityEngine.Object
        {
            try
            {
                var locations = UnityEngine.AddressableAssets.Addressables
                    .LoadResourceLocationsAsync(key, typeof(T)).WaitForCompletion();
                if (locations == null || locations.Count == 0) return null;
                return UnityEngine.AddressableAssets.Addressables
                    .LoadAssetAsync<T>(locations[0]).WaitForCompletion();
            }
            catch (Exception)
            {
                // No catalog at all (Editor before a build). Callers fall back to AssetDatabase.
                return null;
            }
        }

        private static Sprite LoadSprite(string setId, string fileName)
        {
            Sprite loaded = LoadAddressable<Sprite>($"pack_art/{fileName}");
            if (loaded != null) return loaded;

#if UNITY_EDITOR
            // FindAssets matches loosely, so require the exact file name — the same guard
            // EditorUiSpriteProvider carries, for the same reason.
            string folder = $"{ArtRoot}/{setId}";
            if (UnityEditor.AssetDatabase.IsValidFolder(folder))
            {
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"t:Sprite {fileName}", new[] { folder }))
                {
                    string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    if (System.IO.Path.GetFileNameWithoutExtension(path) != fileName) continue;
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
                }
            }
#endif
            return null;
        }
    }
}
