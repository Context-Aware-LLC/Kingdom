#if UNITY_ANDROID
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Kingdom.Presentation;
using UnityEditor.Android;
using UnityEngine;

namespace Kingdom.Editor
{
    // Keep the SDK's UUID/mapping preparation intact. Only its network tasks are deferred.
    internal sealed class AndroidSymbolUpload : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => int.MaxValue;

        [Serializable]
        private sealed class Configuration
        {
            public bool configured;
            public string organization;
            public string project;
            public string variant;
            public bool minified;
        }

        public void OnPostGenerateGradleAndroidProject(string projectPath)
        {
            string mode = Environment.GetEnvironmentVariable("KINGDOM_SYMBOL_UPLOAD");
            string launcher = Path.Combine(Path.GetDirectoryName(projectPath), "launcher");
            var configuration = new Configuration
            {
                configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    KingdomSentryCliOptions.AuthTokenVariable))
                    && !string.IsNullOrWhiteSpace(CrashReportingProject.Dsn)
                    && !string.IsNullOrWhiteSpace(CrashReportingProject.Project)
                    && !string.IsNullOrWhiteSpace(CrashReportingProject.Organization),
                organization = CrashReportingProject.Organization,
                project = CrashReportingProject.Project,
                variant = Environment.GetEnvironmentVariable("KINGDOM_PROFILER_BUILD") == "1"
                    ? "debug" : "release",
                minified = Environment.GetEnvironmentVariable("KINGDOM_PROFILER_BUILD") == "1"
                    ? UnityEditor.PlayerSettings.Android.minifyDebug
                    : UnityEditor.PlayerSettings.Android.minifyRelease
            };
            File.WriteAllText(Path.Combine(launcher, "kingdom-symbol-upload.json"),
                JsonUtility.ToJson(configuration, true), new UTF8Encoding(false));

            string gradlePath = Path.Combine(launcher, "build.gradle");
            string original = File.ReadAllText(gradlePath);
            string text = original.Replace("\r\n", "\n");
            // Unity can reuse this generated file. Remove our previous block instead of
            // accumulating callbacks, including the unconditional block from initial testing.
            text = text.Replace(@"
// Kingdom: symbol preparation stays in the SDK; network upload is a separate step.
tasks.configureEach {
    if (name in ['sentryUploadDebugFiles', 'sentryUploadProguardMapping', 'sentryUploadSymbols']) {
        enabled = false
    }
}
".Replace("\r\n", "\n"), "");
            text = Regex.Replace(text,
                @"(?ms)^// Kingdom: symbol upload policy \[start\]\n.*?^// Kingdom: symbol upload policy \[end\]\n?", "");
            // Evaluate the current environment at Gradle execution time. A cached project
            // must not keep uploads disabled after switching back to an inline release.
            text = text.TrimEnd() + @"

// Kingdom: symbol upload policy [start]
if (System.getenv('KINGDOM_SYMBOL_UPLOAD') in ['deferred', 'skip']) {
    tasks.configureEach {
        if (name in ['sentryUploadDebugFiles', 'sentryUploadProguardMapping', 'sentryUploadSymbols']) {
            enabled = false
        }
    }
}
// Kingdom: symbol upload policy [end]
".Replace("\r\n", "\n");
            if (text != original) File.WriteAllText(gradlePath, text, new UTF8Encoding(false));
            Debug.Log($"[KingdomBuild] Sentry network upload: {mode ?? "inline"}; reusable Gradle policy applied.");
        }
    }
}
#endif
