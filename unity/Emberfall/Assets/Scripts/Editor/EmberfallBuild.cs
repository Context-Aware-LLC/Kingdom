using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// M2 D1 player-build entry point: Addressables content build + Windows standalone player
    /// in one step (content must be rebuilt whenever addressable assets change, or the player
    /// ships a stale catalog). Output: unity/Emberfall/Builds/Windows/Emberfall.exe.
    /// </summary>
    public static class EmberfallBuild
    {
        private const string ScenePath = "Assets/Scenes/BoardNetworked.unity";
        private const string OutputPath = "Builds/Windows/Emberfall.exe";

        [MenuItem("Emberfall/Build/Windows Standalone (content + player)")]
        public static void BuildWindows()
        {
            AddressableAssetSettings.CleanPlayerContent();
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
            if (!string.IsNullOrEmpty(contentResult.Error))
                throw new BuildFailedException(
                    $"[EmberfallBuild] Addressables content build FAILED: {contentResult.Error}");
            Debug.Log($"[EmberfallBuild] Addressables content built ({contentResult.Duration:F1}s)");

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            // MUST throw, not log. A failed player build leaves the PREVIOUS run's Emberfall.exe
            // sitting next to freshly-rebuilt Addressables content, and neither the exe's timestamp
            // (Unity skips rewriting a content-identical launcher stub) nor Emberfall_Data's
            // directory mtime moves — so a stale build is indistinguishable from a good one by
            // inspection. Failing loudly is the only thing that tells you.
            if (summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"[EmberfallBuild] Player build {summary.result}: {summary.totalErrors} error(s). " +
                    $"'{OutputPath}' may still hold a STALE player from a previous build — do not distribute it.");

            Debug.Log($"[EmberfallBuild] Player build {summary.result}: " +
                      $"{summary.totalErrors} error(s), {summary.totalSize / (1024 * 1024)} MB → {OutputPath}");
        }
    }
}
