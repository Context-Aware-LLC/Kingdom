using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
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
        private const string ScenePath = "Assets/Scenes/Board.unity";
        private const string OutputPath = "Builds/Windows/Emberfall.exe";

        [MenuItem("Emberfall/Build/Windows Standalone (content + player)")]
        public static void BuildWindows()
        {
            AddressableAssetSettings.CleanPlayerContent();
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
            if (!string.IsNullOrEmpty(contentResult.Error))
            {
                Debug.LogError($"[EmberfallBuild] Addressables content build FAILED: {contentResult.Error}");
                return;
            }
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
            Debug.Log($"[EmberfallBuild] Player build {report.summary.result}: " +
                      $"{report.summary.totalErrors} error(s), {report.summary.totalSize / (1024 * 1024)} MB → {OutputPath}");
        }
    }
}
