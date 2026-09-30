#if UNITY_ANDROID
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Turns on resource shrinking for the release build type of the generated launcher module.
    ///
    /// <para>Unity exposes Minify (R8) per build type but has no setting for <c>shrinkResources</c>,
    /// and the custom launcher template that could carry it is parked as
    /// <c>Assets/Plugins/Android/launcherTemplate.gradle.DISABLED</c>. AGP rejects
    /// <c>shrinkResources</c> without <c>minifyEnabled</c>, so this only fires when Release minify
    /// is on and is a no-op otherwise.</para>
    /// </summary>
    internal sealed class AndroidReleaseResourceShrinking : IPostGenerateGradleAndroidProject
    {
        private static readonly Regex ReleaseMinifyOn =
            new Regex(@"(release\s*\{\s*\r?\n(\s*)minifyEnabled true)", RegexOptions.CultureInvariant);

        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string projectPath)
        {
            string launcherGradle = Path.GetFullPath(Path.Combine(projectPath, "..", "launcher", "build.gradle"));
            if (!File.Exists(launcherGradle))
                throw new BuildFailedException(
                    $"[EmberfallBuild] Could not locate the generated launcher Gradle file at '{launcherGradle}'.");

            string contents = File.ReadAllText(launcherGradle);
            if (contents.Contains("shrinkResources", StringComparison.Ordinal))
                return;

            string updated = ReleaseMinifyOn.Replace(contents, "$1\n$2shrinkResources true", 1);
            if (updated == contents)
                return;

            File.WriteAllText(launcherGradle, updated);
            Debug.Log($"[EmberfallBuild] Enabled release resource shrinking in '{launcherGradle}'.");
        }
    }
}
#endif
