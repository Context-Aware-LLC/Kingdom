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
    /// A new card set needs NO edit here — drop Assets/Art/Cards/&lt;setId&gt;/ and re-run. Re-running
    /// the menu item after an art drop re-syncs entries without duplicating them.
    /// Also migrates Assets/Resources/cards/*.cards.json out of Resources/ (M2 D1).
    /// </summary>
    public static class AddressablesSetup
    {
        private const string CardDataFolder = "Assets/CardData";
        private const string CardArtRoot = "Assets/Art/Cards";

        [MenuItem("Emberfall/Addressables/Setup Groups + Migrate Card Data")]
        public static void Run()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);

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

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[AddressablesSetup] groups synced: UI_Core, CardArt_CORE, CardArt_EMBERFALL, CardData, Backgrounds, Orbs, Fonts");
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
