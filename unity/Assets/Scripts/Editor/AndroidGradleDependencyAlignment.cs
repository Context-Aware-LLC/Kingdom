#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Keeps Kotlin's split stdlib artifacts on one version in generated Android builds.
    ///
    /// <para>AndroidX currently selects kotlin-stdlib 1.8.22 while kotlinx-coroutines 1.6.4
    /// requests kotlin-stdlib-jdk7/jdk8 1.6.21. Kotlin 1.8 folded the JDK7/JDK8 classes into
    /// the main stdlib, so packaging those older companion jars beside 1.8.22 produces duplicate
    /// classes and fails <c>:launcher:checkReleaseDuplicateClasses</c>. The 1.8.22 companion
    /// artifacts are empty compatibility shims, which is the intended aligned graph.</para>
    /// </summary>
    internal sealed class AndroidGradleDependencyAlignment : IPostGenerateGradleAndroidProject
    {
        private const string Marker = "// KINGDOM_KOTLIN_STDLIB_ALIGNMENT";

        private const string AlignmentBlock = @"

// KINGDOM_KOTLIN_STDLIB_ALIGNMENT
// Kotlin 1.8 merged the JDK7/JDK8 classes into kotlin-stdlib. Keep the compatibility
// artifacts aligned so old coroutine metadata cannot add class-bearing 1.6.x jars.
allprojects {
    configurations.configureEach {
        resolutionStrategy {
            force 'org.jetbrains.kotlin:kotlin-stdlib:1.8.22'
            force 'org.jetbrains.kotlin:kotlin-stdlib-jdk7:1.8.22'
            force 'org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.8.22'
        }
    }
}
";

        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string projectPath)
        {
            string rootGradlePath = FindRootGradleFile(projectPath);
            string contents = File.ReadAllText(rootGradlePath);
            if (contents.Contains(Marker, StringComparison.Ordinal))
                return;

            File.AppendAllText(rootGradlePath, AlignmentBlock);
            Debug.Log($"[KingdomBuild] Aligned Kotlin stdlib artifacts in '{rootGradlePath}'.");
        }

        private static string FindRootGradleFile(string projectPath)
        {
            var directory = new DirectoryInfo(projectPath);
            while (directory != null)
            {
                string settingsPath = Path.Combine(directory.FullName, "settings.gradle");
                string buildPath = Path.Combine(directory.FullName, "build.gradle");
                if (File.Exists(settingsPath) && File.Exists(buildPath))
                    return buildPath;

                directory = directory.Parent;
            }

            throw new BuildFailedException(
                $"[KingdomBuild] Could not locate the generated root Gradle project above '{projectPath}'.");
        }
    }
}
#endif
