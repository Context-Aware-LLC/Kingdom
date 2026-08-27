using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// One-shot (idempotent) Addressables bootstrap per hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §8:
    ///   UI_Core           — Assets/Art/UI/** at address "ui/&lt;fileName&gt;" (always loaded)
    ///   CardArt_&lt;setId&gt;   — one group per subfolder of Assets/Art/Cards/, at "card_art/&lt;fileName&gt;".
    ///                       CORE additionally carries Assets/Art/Hero/**.
    ///   CardData          — card set JSON at "cards/&lt;setId&gt;.cards"
    ///   PackArt_&lt;setId&gt;   — one group per subfolder of Assets/Art/Shop/Packs/, sprites at
    ///                       "pack_art/&lt;fileName&gt;" and that set's packart.json at
    ///                       "pack_art/&lt;setId&gt;.manifest".
    /// A new card set needs NO edit here — drop Assets/Art/Cards/&lt;setId&gt;/ and re-run. Re-running
    /// the menu item after an art drop re-syncs entries without duplicating them.
    /// Also migrates Assets/Resources/cards/*.cards.json out of Resources/ (M2 D1).
    /// </summary>
    public static class AddressablesSetup
    {
        private const string CardDataFolder = "Assets/CardData";
        private const string CardArtRoot = "Assets/Art/Cards";
        private const string PackArtRoot = "Assets/Art/Shop/Packs";
        public const string SoundEffectsFolder = "Assets/Sound/SFX";

        /// <summary>Every group name this run touched, in the order it touched them, so the
        /// closing log reports the run rather than a list someone has to remember to edit.</summary>
        private static readonly List<string> SyncedGroups = new List<string>();

        [MenuItem("Emberfall/Addressables/Setup Groups + Migrate Card Data")]
        public static void Run()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            SyncedGroups.Clear();

            MigrateCardDataOutOfResources();

            var uiCore = GetGroup(settings, "UI_Core");
            AddFolderSprites(settings, uiCore, "Assets/Art/UI", "ui");
            AddFolderAssets(settings, uiCore, "Assets/Materials", "materials", "t:Material");
            // One CardArt_<setId> group per SET FOLDER FOUND ON DISK, not a hardcoded list.
            // REKINDLING shipped card rows and art with no group because the previous three lines
            // named CORE and EMBERFALL literally: EditorArtProvider resolves by AssetDatabase and
            // finds art whether or not it is addressable, so an unregistered set looks correct in
            // the Editor and renders BLANK in a player build, silently. Enumerating the folder
            // means a new set cannot be forgotten — it only has to exist.
            foreach (string setFolder in Directory.GetDirectories(CardArtRoot))
            {
                string setId = Path.GetFileName(setFolder);
                var group = GetGroup(settings, "CardArt_" + setId);
                AddFolderSprites(settings, group, $"{CardArtRoot}/{setId}", "card_art");

                // Hero portraits ride along with CORE — they are set-agnostic and always loaded.
                if (setId == "CORE") AddFolderSprites(settings, group, "Assets/Art/Hero", "card_art");
            }

            // One PackArt_<setId> group per pack-art folder found on disk — same enumerate-the-disk
            // rule as CardArt above, for the same reason: an expansion's packaging must not be
            // forgettable. Sprites address flat as pack_art/<fileName> (so filenames stay unique
            // across sets, exactly as card art already requires); the manifest cannot, because
            // every set's is called packart.json, so it is addressed from its FOLDER name.
            if (Directory.Exists(PackArtRoot))
            {
                foreach (string setFolder in Directory.GetDirectories(PackArtRoot))
                {
                    string setId = Path.GetFileName(setFolder);
                    var packGroup = GetGroup(settings, "PackArt_" + setId);
                    AddFolderSprites(settings, packGroup, $"{PackArtRoot}/{setId}", "pack_art");

                    string manifestPath = $"{PackArtRoot}/{setId}/packart.json";
                    string manifestGuid = AssetDatabase.AssetPathToGUID(manifestPath);
                    if (string.IsNullOrEmpty(manifestGuid))
                    {
                        // Art with no manifest resolves to nothing at runtime: PackArtProvider reads
                        // the manifest first and never scans the folder in a build. Say so here,
                        // because the Editor's AssetDatabase fallback hides it during authoring.
                        Debug.LogWarning($"[AddressablesSetup] {manifestPath} is missing — " +
                                         $"PackArt_{setId} will ship art that nothing can look up.");
                    }
                    else
                    {
                        settings.CreateOrMoveEntry(manifestGuid, packGroup)
                                .SetAddress($"pack_art/{setId}.manifest");
                    }
                }
            }

            var dataGroup = GetGroup(settings, "CardData");
            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { CardDataFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cards.json")) continue;
                string setFile = Path.GetFileName(path);                       // "Emberfall.cards.json"
                string address = "cards/" + setFile.Substring(0, setFile.Length - ".json".Length); // "cards/Emberfall.cards"
                var entry = settings.CreateOrMoveEntry(guid, dataGroup);
                entry.SetAddress(address);
            }

            var bgGroup = GetGroup(settings, "Backgrounds");
            AddFolderAssets(settings, bgGroup, "Assets/Art/Backgrounds", "backgrounds", "t:Texture2D");
            // Assets/Art/Loading was a staging folder: of its ten textures exactly three were
            // ever loaded (the orbs), and the other seven only looked live because this scan
            // registered the folder wholesale. Orbs now have their own root; the rest went to
            // art-pipeline/junk/. Backgrounds is scanned recursively and addresses by FILE NAME,
            // so Backgrounds/Loading/ and Backgrounds/Versus/ need no entry of their own and
            // keep the addresses they already had.
            var orbsGroup = GetGroup(settings, "Orbs");
            AddFolderAssets(settings, orbsGroup, "Assets/Art/Orbs", "orbs", "t:Texture2D");
            var fontsGroup = GetGroup(settings, "Fonts");
            AddFolderAssets(settings, fontsGroup, "Assets/Art/Fonts", "fonts", "t:TMP_FontAsset");
            var soundEffectsGroup = GetGroup(settings, "SoundEffects");
            AddFolderAssets(settings, soundEffectsGroup, SoundEffectsFolder, "sfx", "t:AudioClip");

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            AssetDatabase.SaveAssets();
            // Report what this run actually touched, never a written-down list. The literal one
            // here named seven groups and was already wrong the moment a set folder appeared that
            // it did not mention — which is what a per-set loop guarantees will happen. A summary
            // you have to remember to update is a summary that lies about the run you just did.
            Debug.Log("[AddressablesSetup] groups synced: " + string.Join(", ", SyncedGroups));
        }

        /// <summary>
        /// Registers one clip selected by the card-authoring window immediately, so attaching a
        /// sound is a complete operation and does not depend on remembering to run the bulk setup
        /// menu afterwards.
        /// </summary>
        public static bool TryRegisterSoundEffect(AudioClip clip, out string soundId, out string error)
        {
            soundId = "";
            error = "";
            if (clip == null) return true;

            string path = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
            string prefix = SoundEffectsFolder + "/";
            if (!path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                error = $"Sound effects must live under {SoundEffectsFolder}. Move '{path}' there and attach it again.";
                return false;
            }

            soundId = Path.GetFileNameWithoutExtension(path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                error = $"Unity has no asset GUID for '{path}'.";
                soundId = "";
                return false;
            }

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            string address = $"sfx/{soundId}";
            foreach (AddressableAssetEntry existing in entries)
                if (existing.address == address && existing.guid != guid)
                {
                    error = $"Another AudioClip already owns Addressables key '{address}'. Sound filenames must be unique.";
                    soundId = "";
                    return false;
                }

            AddressableAssetGroup group = GetGroup(settings, "SoundEffects");
            settings.CreateOrMoveEntry(guid, group).SetAddress(address);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, null, true, true);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static void MigrateCardDataOutOfResources()
        {
            const string oldFolder = "Assets/Resources/cards";
            if (!AssetDatabase.IsValidFolder(oldFolder)) return;
            if (!AssetDatabase.IsValidFolder(CardDataFolder))
                AssetDatabase.CreateFolder("Assets", "CardData");

            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { oldFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string dest = $"{CardDataFolder}/{Path.GetFileName(path)}";
                string err = AssetDatabase.MoveAsset(path, dest);
                if (!string.IsNullOrEmpty(err)) Debug.LogError($"[AddressablesSetup] move failed: {err}");
                else Debug.Log($"[AddressablesSetup] migrated {path} → {dest}");
            }
        }

        private static AddressableAssetGroup GetGroup(AddressableAssetSettings settings, string name)
        {
            if (!SyncedGroups.Contains(name)) SyncedGroups.Add(name);
            AddressableAssetGroup group = settings.FindGroup(name);
            if (group == null)
            {
                group = settings.CreateGroup(name, false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                // Local packed groups only for M2 (decision 10 pending → no remote catalog).
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            }
            return group;
        }

        private static void AddFolderSprites(AddressableAssetSettings settings, AddressableAssetGroup group,
                                             string folder, string addressPrefix)
            => AddFolderAssets(settings, group, folder, addressPrefix, "t:Sprite");

        private static void AddFolderAssets(AddressableAssetSettings settings, AddressableAssetGroup group,
                                            string folder, string addressPrefix, string filter)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[AddressablesSetup] folder missing, skipped: {folder}");
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets(filter, new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group);
                entry.SetAddress($"{addressPrefix}/{Path.GetFileNameWithoutExtension(path)}");
            }
        }
    }
}
