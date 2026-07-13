using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// One-shot (idempotent) Addressables bootstrap per pipeline.md §8:
    ///   UI_Core           — Assets/Art/UI/** at address "ui/&lt;fileName&gt;" (always loaded)
    ///   CardArt_CORE      — Assets/Art/Cards/CORE/** + Assets/Art/Hero/** at "card_art/&lt;fileName&gt;"
    ///   CardArt_EMBERFALL — Assets/Art/Cards/EMBERFALL/** at "card_art/&lt;fileName&gt;"
    ///   CardData          — card set JSON at "cards/&lt;setId&gt;.cards"
    /// Each new card set = one new CardArt_&lt;setId&gt; group + JSON rows; re-running the menu
    /// item after an art drop re-syncs entries without duplicating them.
    /// Also migrates Assets/Resources/cards/*.cards.json out of Resources/ (M2 D1).
    /// </summary>
    public static class AddressablesSetup
    {
        private const string CardDataFolder = "Assets/CardData";

        [MenuItem("Emberfall/Addressables/Setup Groups + Migrate Card Data")]
        public static void Run()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);

            MigrateCardDataOutOfResources();

            AddFolderSprites(settings, GetGroup(settings, "UI_Core"), "Assets/Art/UI", "ui");
            var core = GetGroup(settings, "CardArt_CORE");
            AddFolderSprites(settings, core, "Assets/Art/Cards/CORE", "card_art");
            AddFolderSprites(settings, core, "Assets/Art/Hero", "card_art");
            AddFolderSprites(settings, GetGroup(settings, "CardArt_EMBERFALL"), "Assets/Art/Cards/EMBERFALL", "card_art");

            var dataGroup = GetGroup(settings, "CardData");
            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { CardDataFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cards.json")) continue;
                string setFile = Path.GetFileName(path);                       // "M1.cards.json"
                string address = "cards/" + setFile.Substring(0, setFile.Length - ".json".Length); // "cards/M1.cards"
                var entry = settings.CreateOrMoveEntry(guid, dataGroup);
                entry.SetAddress(address);
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[AddressablesSetup] groups synced: UI_Core, CardArt_CORE, CardArt_EMBERFALL, CardData");
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
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[AddressablesSetup] folder missing, skipped: {folder}");
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Sprite", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group);
                entry.SetAddress($"{addressPrefix}/{Path.GetFileNameWithoutExtension(path)}");
            }
        }
    }
}
