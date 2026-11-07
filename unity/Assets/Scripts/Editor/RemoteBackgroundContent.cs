using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Records the exact remote output and prevents a player shipping unpublished URLs.</summary>
    public sealed class RemoteBackgroundContent : IPreprocessBuildWithReport
    {
        [Serializable]
        private sealed class Bundle
        {
            public string file;
            public long bytes;
            public string sha256;
        }

        [Serializable]
        private sealed class Manifest
        {
            public int version = 1;
            public string platform;
            public string baseUrl;
            public string bucket = "kobi-game-assets";
            public string keyPrefix;
            public Bundle[] bundles;
        }

        [Serializable]
        private sealed class Receipt { public string manifestSha256; }

        public int callbackOrder => 100;
        public void OnPreprocessBuild(BuildReport report) => RequirePublished();

        private static string OutputDirectory()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var group = settings.FindGroup(BackgroundVariantPolicy.Group(BackgroundTier.DesktopTablet));
            return Path.GetFullPath(group.GetSchema<BundledAssetGroupSchema>().BuildPath.GetValue(settings));
        }

        public static void WriteManifest(AddressablesPlayerBuildResult result)
        {
            string output = OutputDirectory();
            string groupName = BackgroundVariantPolicy.Group(BackgroundTier.DesktopTablet);
            var bundles = result.AssetBundleBuildResults.Where(b => b.SourceAssetGroup != null
                && b.SourceAssetGroup.Name == groupName).OrderBy(b => b.FilePath, StringComparer.Ordinal).ToArray();
            if (bundles.Length != BackgroundVariantPolicy.Keys.Length)
                throw new BuildFailedException($"Expected {BackgroundVariantPolicy.Keys.Length} remote background bundles, got {bundles.Length}.");
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var schema = settings.FindGroup(groupName).GetSchema<BundledAssetGroupSchema>();
            var manifest = new Manifest
            {
                platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                baseUrl = schema.LoadPath.GetValue(settings),
                keyPrefix = "backgrounds/v1/" + EditorUserBuildSettings.activeBuildTarget,
                bundles = bundles.Select(b =>
                {
                    string path = Path.GetFullPath(b.FilePath);
                    if (Path.GetDirectoryName(path) != output)
                        throw new BuildFailedException($"Large background leaked outside remote output: {path}");
                    return new Bundle { file = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Digest(path) };
                }).ToArray(),
            };
            // A prior local build can leave large/legacy bundles in the copy-to-player folder.
            // Remove only obsolete BACKGROUND build outputs, never source assets or other groups.
            var mobile = settings.FindGroup(BackgroundVariantPolicy.Group(BackgroundTier.Mobile)).GetSchema<BundledAssetGroupSchema>();
            string local = Path.GetFullPath(mobile.BuildPath.GetValue(settings));
            var current = result.AssetBundleBuildResults.Select(b => Path.GetFullPath(b.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(local))
                foreach (string stale in Directory.GetFiles(local, "backgrounds*.bundle", SearchOption.AllDirectories))
                    if (!current.Contains(Path.GetFullPath(stale))) File.Delete(stale);

            Directory.CreateDirectory(output);
            string pathToManifest = Path.Combine(output, "background-content.json");
            File.WriteAllText(pathToManifest, JsonUtility.ToJson(manifest, true) + "\n", new UTF8Encoding(false));
            Debug.Log($"[BackgroundContent] {bundles.Length} remote bundles; publish {pathToManifest}");
        }

        public static void RequirePublished()
        {
            string manifestPath = Path.Combine(OutputDirectory(), "background-content.json");
            string receiptPath = manifestPath + ".published.json";
            string remedy = "Run Kingdom/Build/Background Content (active platform), then "
                + $"python tooling/scripts/publish_background_content.py --manifest \"{manifestPath}\" --publish, then rebuild the player.";
            if (!File.Exists(manifestPath) || !File.Exists(receiptPath))
                throw new BuildFailedException("Remote backgrounds have not been published. " + remedy);
            var receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
            if (receipt.manifestSha256 != Digest(manifestPath))
                throw new BuildFailedException("Remote backgrounds changed since publication. " + remedy);
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest.platform != EditorUserBuildSettings.activeBuildTarget.ToString()
                || manifest.bundles.Length != BackgroundVariantPolicy.Keys.Length)
                throw new BuildFailedException("Remote background receipt belongs to a different platform or incomplete build. " + remedy);
            foreach (var bundle in manifest.bundles)
            {
                string path = Path.Combine(OutputDirectory(), bundle.file);
                if (!File.Exists(path) || Digest(path) != bundle.sha256)
                    throw new BuildFailedException("Remote background build output changed. " + remedy);
            }
        }

        private static string Digest(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
