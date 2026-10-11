using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kingdom.Editor
{
    /// <summary>
    /// #195 — stamps the build's version, ordinal and commit into <c>Assets/Resources</c> so
    /// <see cref="BuildStamp"/> can show a tester which build they are holding.
    ///
    /// <para>Runs as <see cref="IPreprocessBuildWithReport"/>, so every build gets a stamp with no
    /// one having to remember — including the ones driven from
    /// <c>Kingdom/Build/Android APK</c>.</para>
    ///
    /// <para><b>The ordinal is the git commit count</b>, not a number CI stores. See
    /// <see cref="BuildStamp"/> for why that is the better of the two.</para>
    ///
    /// <para><b>THIS WRITES A TRACKED-LOOKING FILE INTO Assets/, AND THAT FILE IS GITIGNORED.</b>
    /// It has to live under <c>Resources</c> to be readable from a player, and it must never be
    /// committed: a stamp in git would claim a commit that is not the one being built, which is
    /// worse than no stamp at all. <c>.gitignore</c> carries both it and its <c>.meta</c>.</para>
    ///
    /// <para><b>Git may not be there</b> — a clean CI image, a source zip, a machine with no git on
    /// PATH. Then the ordinal is <c>0</c> and the sha is <c>nogit</c>, and the build still succeeds.
    /// Failing a build over a cosmetic footer would be the wrong trade, but it logs loudly, because
    /// an APK that cannot say what it is defeats the point of the issue.</para>
    /// </summary>
    public sealed class BuildStampWriter : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private const string ResourcesDir = "Assets/Resources";
        private static readonly string AssetPath =
            ResourcesDir + "/" + BuildStamp.ResourceName + ".txt";

        public void OnPreprocessBuild(BuildReport report) => Write(report.summary.platform);

        // NO MENU ITEM (removed 2026-09-04, Sam auditing the Kingdom menu). There was a
        // "Build/Write Build Stamp (what a tester sees)" entry here, and it was redundant the day
        // it was written: this class is an IPreprocessBuildWithReport, so OnPreprocessBuild above
        // stamps EVERY build with no one having to remember. The menu item's only distinct effect
        // was to write a stamp OUTSIDE a build — i.e. to leave Assets/Resources holding a stamp
        // for a build that was never made, which is worse than no stamp at all.

        private static string Write(BuildTarget target)
        {
            string revision = Git("rev-list --count HEAD", "0");
            string sha = Git("rev-parse --short HEAD", "nogit");
            string text = string.Join(BuildStamp.Separator.ToString(),
                Application.version,
                revision,
                sha,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                StoreBuild(target));

            if (revision == "0" || sha == "nogit")
                Debug.LogWarning("[BuildStamp] git did not answer, so this build cannot name its "
                    + "own commit (revision=" + revision + " sha=" + sha + "). The footer will say "
                    + "so rather than inventing a number, but a tester's report from this build "
                    + "will not be traceable.");

            Directory.CreateDirectory(ResourcesDir);
            File.WriteAllText(AssetPath, text);
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
            return text;
        }

        /// <summary>What the update gate compares against the server's minimum
        /// (<see cref="BuildStamp.StoreBuild"/>). Read AFTER <see cref="StoreVersionBump"/> has
        /// run, so a store build stamps the number it is uploaded under. An iOS build number set
        /// by hand to a dotted form reads as 0 on the device, which is below every minimum — keep
        /// it a plain integer, as <see cref="StoreVersionBump"/> does.</summary>
        private static string StoreBuild(BuildTarget target)
        {
            if (target == BuildTarget.Android)
                return PlayerSettings.Android.bundleVersionCode.ToString(CultureInfo.InvariantCulture);
            if (target == BuildTarget.iOS)
                return PlayerSettings.iOS.buildNumber;
            return "0";
        }

        /// <summary>Run one git command in the repo, or return <paramref name="fallback"/>.</summary>
        private static string Git(string args, string fallback)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    // The project lives at <repo>/unity, so dataPath's parent is not the
                    // repo root — but git walks up, so any path inside the tree resolves it.
                    WorkingDirectory = Application.dataPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return fallback;
                    string outText = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 && outText.Length > 0 ? outText : fallback;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BuildStamp] git '" + args + "' failed: " + ex.Message);
                return fallback;
            }
        }
    }
}
