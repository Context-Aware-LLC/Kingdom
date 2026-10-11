using System.Collections.Generic;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace Kingdom.Editor
{
    /// <summary>
    /// Real IAddressableArtCatalog (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.4) backed by the project's
    /// AddressableAssetSettings: an artId exists iff a "card_art/&lt;flattened artId&gt;"
    /// address is registered in any group. Replaces StubArtCatalog (which answered
    /// Exists=true for everything, leaving lint E1 toothless). Metadata-only — never
    /// loads a sprite.
    /// </summary>
    public sealed class AddressablesArtCatalog : IAddressableArtCatalog
    {
        private const string Prefix = "card_art/";
        private readonly Dictionary<string, ArtCatalogEntry> _byArtId = new();

        public AddressablesArtCatalog()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return; // no settings yet → empty catalog, E1 warns on every artId

            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            foreach (AddressableAssetEntry entry in entries)
            {
                if (entry.address == null || !entry.address.StartsWith(Prefix)) continue;
                // Address "card_art/neutral_emberling_pup" ↔ artId "neutral_emberling_pup".
                //
                // An artId is now the filename, with no un-flattening step. The old scheme put
                // every artId under an "art/" namespace and this line was its inverse, stripping
                // the leading "art_" back to "art/". That namespace had exactly one value across
                // all 260 rows, so it distinguished nothing, and it was dropped 2026-08-06.
                // Reinstating a prefix rule here would silently mangle any card whose name
                // happens to start with the same letters.
                string artId = entry.address.Substring(Prefix.Length);
                _byArtId[artId] = new ArtCatalogEntry(entry.address, entry.guid);
            }
        }

        public bool Exists(string artId) => _byArtId.ContainsKey(artId);

        public bool TryGetEntry(string artId, out ArtCatalogEntry entry) => _byArtId.TryGetValue(artId, out entry);

        public IReadOnlyCollection<string> AllArtIds() => _byArtId.Keys;
    }
}
