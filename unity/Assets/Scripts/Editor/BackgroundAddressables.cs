using System;
using System.IO;
using System.Linq;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Mobile ships locally; desktop/tablet backgrounds are cached on demand.</summary>
    public static class BackgroundAddressables
    {
        [MenuItem("Kingdom/Regenerate/Background Variant Groups")]
        public static void Run()
        {
            Synchronize(AddressableAssetSettingsDefaultObject.GetSettings(true));
            AssetDatabase.SaveAssets();
            Debug.Log("[BackgroundAddressables] DesktopTablet and Mobile background groups synchronized.");
        }

        public static void Synchronize(AddressableAssetSettings settings)
        {
            // Check everything first: an incomplete export must fail the build, not silently ship
            // blank screens or a mixture of old and new compositions.
            foreach (BackgroundTier tier in Enum.GetValues(typeof(BackgroundTier)))
            foreach (string key in BackgroundVariantPolicy.Keys)
            {
                string path = BackgroundVariantPolicy.AssetPath(tier, key);
                if (!File.Exists(path) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    throw new InvalidOperationException($"Missing background export '{path}'. Run tooling/scripts/export_background_variants.py and let Unity import it.");
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) throw new InvalidOperationException($"Not a texture: {path}");
                importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                if (width != BackgroundVariantPolicy.Width(tier) || height != BackgroundVariantPolicy.Height(tier))
                    throw new InvalidOperationException($"Wrong background dimensions at '{path}': {width}x{height}.");
                int required = BackgroundVariantPolicy.Width(tier);
                foreach (string platform in new[] { "Standalone", "Android", "iPhone" })
                {
                    var platformSettings = importer.GetPlatformTextureSettings(platform);
                    if (!platformSettings.overridden || platformSettings.maxTextureSize < required)
                        throw new InvalidOperationException($"'{path}' is capped below {required} on {platform}. Reimport the BackgroundVariants folder.");
                }
            }

            foreach (string profile in settings.profileSettings.GetAllProfileNames())
            {
                string id = settings.profileSettings.GetProfileId(profile);
                settings.profileSettings.SetValue(id, AddressableAssetSettings.kRemoteBuildPath, "ServerData/Backgrounds/v1/[BuildTarget]");
                settings.profileSettings.SetValue(id, AddressableAssetSettings.kRemoteLoadPath,
                    "https://kingdomofbadideas.com/game-assets/backgrounds/v1/[BuildTarget]");
            }
            // Embed the catalog so startup is offline and older app versions keep their exact
            // hashed bundle URLs. Art updates ship with an app update; remote catalogs are not used.
            settings.BuildRemoteCatalog = false;

            foreach (BackgroundTier tier in Enum.GetValues(typeof(BackgroundTier)))
            {
                string groupName = BackgroundVariantPolicy.Group(tier);
                var group = settings.FindGroup(groupName) ?? settings.CreateGroup(groupName, false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                group.IncludeInBuild = true;
                bool remote = tier == BackgroundTier.DesktopTablet;
                schema.BundleMode = remote ? BundledAssetGroupSchema.BundlePackingMode.PackSeparately
                    : BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                schema.BuildPath.SetVariableByName(settings, remote ? AddressableAssetSettings.kRemoteBuildPath : AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, remote ? AddressableAssetSettings.kRemoteLoadPath : AddressableAssetSettings.kLocalLoadPath);
                schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
                schema.UseAssetBundleCache = true;
                schema.UseAssetBundleCrc = true;
                schema.Timeout = remote ? 20 : 0;
                foreach (var stale in group.entries.ToArray()) settings.RemoveAssetEntry(stale.guid);
                foreach (string key in BackgroundVariantPolicy.Keys)
                {
                    string guid = AssetDatabase.AssetPathToGUID(BackgroundVariantPolicy.AssetPath(tier, key));
                    settings.CreateOrMoveEntry(guid, group).SetAddress(BackgroundVariantPolicy.Address(tier, key));
                }
            }

            // Canonical originals remain available for authoring, but have no shipping entry.
            // USS/prefab references have also been removed, so they cannot enter a built-in bundle.
            var originals = settings.FindGroup("Backgrounds");
            if (originals != null)
            {
                foreach (var entry in originals.entries.ToArray()) settings.RemoveAssetEntry(entry.guid);
                originals.IncludeInBuild = false;
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        }

        [MenuItem("Kingdom/Preview/Background Tier/Desktop and Tablet (next Play)")]
        public static void PreviewDesktopTablet() => EditorPrefs.SetString("Kingdom.BackgroundArt.PreviewTier", "DesktopTablet");

        [MenuItem("Kingdom/Preview/Background Tier/Mobile (next Play)")]
        public static void PreviewMobile() => EditorPrefs.SetString("Kingdom.BackgroundArt.PreviewTier", "Mobile");
    }
}
