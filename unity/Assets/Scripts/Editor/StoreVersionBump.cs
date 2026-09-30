using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Bumps the version name and the store build number on every STORE build, however it is
    /// started — Unity's Build Profiles window included, which has no auto-increment of its own
    /// (Sam, 2026-09-29). Play refuses an upload whose version code is not above every earlier
    /// one, and App Store Connect does the same with the iOS build number.
    ///
    /// <para><b>What counts as a store build:</b> not a development build, and on Android also
    /// signed with a custom keystore — the upload key. Every debug APK
    /// (<c>build-android.ps1</c> without <c>-PlayRelease</c>, and the Editor's own debug-keyed
    /// builds) is left alone, so testing never burns version numbers.</para>
    ///
    /// <para><b>The version name bumps its LAST number and keeps its zero padding</b>:
    /// <c>0.0.0.01</c> becomes <c>0.0.0.02</c>, <c>1.0</c> becomes <c>1.1</c>. Each platform's
    /// store build bumps it, so building Android and then iOS for the same release leaves them one
    /// apart — set it by hand in Player Settings when the two must match.</para>
    ///
    /// <para><b>A failed build still spends its numbers.</b> Stores accept gaps; they refuse
    /// repeats, so bumping before the build and never rolling back is the safe side.</para>
    ///
    /// <para><c>EMBERFALL_VERSION_CODE</c> (set by <c>build-android.ps1 -PlayRelease -VersionCode
    /// N</c>) takes over the Android code instead: that script sets the number itself.</para>
    ///
    /// <para>Runs before <see cref="BuildStampWriter"/> (order -10 vs 0) so the stamp a tester
    /// sees carries the bumped version.</para>
    /// </summary>
    public sealed class StoreVersionBump : IPreprocessBuildWithReport
    {
        public int callbackOrder => -10;

        public void OnPreprocessBuild(BuildReport report)
        {
            BuildTarget target = report.summary.platform;
            if ((report.summary.options & BuildOptions.Development) != 0) return;

            if (target == BuildTarget.Android)
            {
                if (!PlayerSettings.Android.useCustomKeystore) return;
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EMBERFALL_VERSION_CODE")))
                    PlayerSettings.Android.bundleVersionCode += 1;
                PlayerSettings.bundleVersion = NextVersion(PlayerSettings.bundleVersion);
                Debug.Log($"[StoreVersionBump] Android store build: version {PlayerSettings.bundleVersion}, " +
                          $"versionCode {PlayerSettings.Android.bundleVersionCode}");
            }
            else if (target == BuildTarget.iOS)
            {
                PlayerSettings.iOS.buildNumber = NextBuildNumber(PlayerSettings.iOS.buildNumber);
                PlayerSettings.bundleVersion = NextVersion(PlayerSettings.bundleVersion);
                Debug.Log($"[StoreVersionBump] iOS store build: version {PlayerSettings.bundleVersion}, " +
                          $"build {PlayerSettings.iOS.buildNumber}");
            }
            else return;

            AssetDatabase.SaveAssets();
        }

        /// <summary>Increments the last run of digits, keeping its width: 0.0.0.01 → 0.0.0.02,
        /// 1.0.9 → 1.0.10. A version with no digits at all gets ".1" appended.</summary>
        public static string NextVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "0.1";
            Match last = Regex.Match(version, @"(\d+)(?!.*\d)");
            if (!last.Success) return version + ".1";
            string digits = last.Groups[1].Value;
            string next = (long.Parse(digits, CultureInfo.InvariantCulture) + 1)
                .ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
            return version.Substring(0, last.Index) + next + version.Substring(last.Index + digits.Length);
        }

        // iOS build numbers are dotted integers ("1", "1.0.3"); bump the last one the same way.
        private static string NextBuildNumber(string build) =>
            string.IsNullOrEmpty(build) || build == "0" ? "1" : NextVersion(build);
    }
}
