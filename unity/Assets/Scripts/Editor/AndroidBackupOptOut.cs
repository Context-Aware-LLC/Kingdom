#if UNITY_ANDROID
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Turns Android's automatic cloud backup OFF for this app.
    ///
    /// <para><b>Why: the device's files are a CACHE, and Android was restoring a stale one.</b>
    /// Since the login handshake landed, the server is the record for a player's cards, characters
    /// and decks — a reinstall is supposed to get them back from the account, and the local JSON is
    /// only what the last sync wrote. Android's auto-backup does not know that. It silently restores
    /// the old files into a fresh install, ahead of the app's first line of code.</para>
    ///
    /// <para><b>Measured on a Pixel 5a, 2026-09-02.</b> A full uninstall and reinstall came back with
    /// <c>characters.json</c>, <c>decks.json</c> and <c>profile.json</c> already present, all three
    /// written within 7ms of each other and six seconds BEFORE Unity logged <c>ApplicationInfo</c>.
    /// The restored <c>decks.json</c> predated the numeric-card-id migration, so the deck drawer
    /// threw <c>CollectionFormatException</c> on a build that had never saved a deck. The PlayerPrefs
    /// holding the Supabase session came back too, which is why no sign-in was ever asked for.</para>
    ///
    /// <para><b>Two things it costs, both correct.</b> A reinstall now needs a fresh sign-in — which
    /// is the designed behaviour, since identity is portable by email rather than by device backup.
    /// And an uninstall genuinely clears the device, which is what makes a reinstall test mean
    /// anything: with backup on, a restored roster could have come from Google rather than from the
    /// player's account, and the two are indistinguishable on screen.</para>
    ///
    /// <para><b>Done here, not in <c>Assets/Plugins/Android/AndroidManifest.xml</c>.</b> A custom main
    /// manifest is a BASE that Unity copies and then overwrites every attribute it owns from
    /// PlayerSettings — a rule that has already cost this project a session, because an edit there
    /// reads exactly like it worked while the device disagrees. This runs after Unity has finished
    /// writing the Gradle project, so there is nothing left to overwrite it. PlayerSettings has no
    /// API for this attribute, so a post-process is the only lever that is not inert.</para>
    ///
    /// <para><b>Verify it in the built manifest, never in this file:</b>
    /// <c>grep allowBackup Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/src/main/AndroidManifest.xml</c>
    /// after a build. The log line below says the same thing at build time.</para>
    /// </summary>
    internal sealed class AndroidBackupOptOut : IPostGenerateGradleAndroidProject
    {
        /// <summary>After <see cref="AndroidGradleDependencyAlignment"/>, which touches Gradle rather
        /// than the manifest — the two are independent, and the order only needs to be stable.</summary>
        public int callbackOrder => 1100;

        public void OnPostGenerateGradleAndroidProject(string projectPath)
        {
            string manifestPath = Path.Combine(projectPath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                // Loud, because silence here is indistinguishable from success and the consequence
                // is a build that quietly keeps restoring stale data.
                Debug.LogError($"[KingdomBuild] No AndroidManifest.xml at '{manifestPath}' — " +
                               "auto-backup could NOT be disabled and this build will restore " +
                               "stale local data on reinstall.");
                return;
            }

            string manifest = File.ReadAllText(manifestPath);

            if (manifest.Contains("android:allowBackup=\"false\"", StringComparison.Ordinal))
            {
                Debug.Log("[KingdomBuild] Android auto-backup already disabled.");
                return;
            }

            string updated = manifest.Contains("android:allowBackup=", StringComparison.Ordinal)
                ? Regex.Replace(manifest, "android:allowBackup=\"[^\"]*\"", "android:allowBackup=\"false\"")
                : Regex.Replace(manifest, "<application", "<application android:allowBackup=\"false\"",
                                RegexOptions.None);

            if (ReferenceEquals(updated, manifest) || updated == manifest)
            {
                Debug.LogError("[KingdomBuild] Could not find an <application> element to disable " +
                               "auto-backup on. This build will restore stale local data on reinstall.");
                return;
            }

            File.WriteAllText(manifestPath, updated);
            Debug.Log($"[KingdomBuild] Android auto-backup DISABLED in '{manifestPath}'. " +
                      "Uninstall now clears the device; the account is what restores a player.");
        }
    }
}
#endif
