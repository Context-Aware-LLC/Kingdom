using System;
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
        private const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
        private const string RosterScenePath = "Assets/Scenes/CharacterRoster.unity";
        private const string CreateScenePath = "Assets/Scenes/CharacterCreate.unity";
        private const string AccountScenePath = "Assets/Scenes/AccountCreate.unity";

        // Collection IS the deckbuilder (#154). There is no separate deckbuilder scene to list.
        private const string CollectionScenePath = "Assets/Scenes/Collection.unity";
        // The store left MainMenu for a scene of its own on 2026-08-20. Listing it here is what
        // keeps the Store button alive: this array is also what SyncSceneList writes, so a scene
        // missing from it is missing from the Editor build list too, however the .asset reads.
        private const string ShopScenePath = "Assets/Scenes/Shop.unity";
        private const string PackRevealScenePath = "Assets/Scenes/PackReveal.unity";

        /// <summary>
        /// EVERY shipped scene, in build order — index 0 is the boot scene.
        ///
        /// <para><b>This exists because the list used to be written out four times</b>: once per
        /// BuildPlayerOptions (Windows DEV, Android, Windows Standalone) and once in
        /// <see cref="SyncSceneList"/>. Adding CharacterCreate on 2026-08-23 updated
        /// <c>SyncSceneList</c> only, so the Editor's scene list knew about it and all three
        /// PLAYERS did not. The failure surfaced on device as a button that did nothing, because
        /// the caller's guard logs <c>"Scene 'X' is not in the build list"</c> and returns —
        /// correct behaviour, invisible to the player, and it names the one place that was
        /// already right.</para>
        ///
        /// <para>A scene added here reaches every build and the Editor together. Nothing else in
        /// this file may spell the list out again.</para>
        /// </summary>
        private static readonly string[] AllScenes =
        {
            MenuScenePath, ScenePath, RosterScenePath, CreateScenePath, AccountScenePath,
            CollectionScenePath, ShopScenePath, PackRevealScenePath,
        };
        private const string OutputPath = "Builds/Windows/Emberfall.exe";
        private const string DevOutputPath = "Builds/WindowsDev/Emberfall.exe";

        /// <summary>
        /// A second seat for local testing: boots the main menu, and is a <b>development</b> player so
        /// <c>DEVELOPMENT_BUILD</c> is defined and the account switcher exists at all
        /// (<see cref="Emberfall.Session.DeviceAccounts"/>).
        ///
        /// <para><b>Separate menu item and separate output directory, deliberately.</b> The shipping
        /// build above is what goes to testers, and turning it into a development player would hand
        /// them the account switcher along with everything else the flag unlocks. Two products, two
        /// folders, no flag to remember.</para>
        ///
        /// <para><b>Pair this with the Editor, not with a second copy of itself.</b> A player build and
        /// the Editor keep PlayerPrefs in different registry keys
        /// (<c>HKCU\Software\&lt;company&gt;\&lt;product&gt;</c> vs
        /// <c>HKCU\Software\Unity\UnityEditor\&lt;company&gt;\&lt;product&gt;</c>) while sharing one
        /// <c>Application.persistentDataPath</c> — so they hold independent active-account pointers over
        /// one shared <c>accounts.json</c>, which is exactly two seats. Two instances of this exe would
        /// share one pointer and fight over which account is signed in.</para>
        /// </summary>
        [MenuItem("Emberfall/Build/Windows DEV Second Seat (menu + development flag)")]
        public static void BuildWindowsDevSeat()
        {
            SyncSceneList();
            BuildAddressables();

            Directory.CreateDirectory(Path.GetDirectoryName(DevOutputPath));
            var options = new BuildPlayerOptions
            {
                // Menu first — it is the boot scene and the only screen that can switch accounts.
                // The board rides along so Find Game actually goes somewhere, and roster +
                // collection so Deck Builder isn't a dead end in this build too. Deck
                // Builder lands on the roster, and the roster is the only door to Collection (#154).
                scenes = AllScenes,
                locationPathName = DevOutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            };
            Report(BuildPipeline.BuildPlayer(options), DevOutputPath);
        }

        private const string AndroidOutputPath = "Builds/Android/Emberfall.apk";

        // Keystore is a debug signing config checked in alongside the source.
        // Alias, store password, and key password are the Android debug defaults.
        // Do not use this keystore for Play Store submissions.
        private const string KeystorePath   = "keystore/debug.keystore";
        private const string KeystorePass   = "android";
        private const string KeyAliasName   = "androiddebugkey";
        private const string KeyAliasPass   = "android";

        /// <summary>
        /// Headless Android build entry point (EF-060): IL2CPP + ARM64 APK via the Android
        /// Addressables profile. Invoke via <c>-executeMethod Emberfall.Editor.EmberfallBuild.BuildAndroid
        /// -buildTarget android</c> so the build target is already Android at startup.
        ///
        /// <para>IL2CPP and ARM64 are forced explicitly here so the build is deterministic
        /// regardless of whatever the Editor's per-user overrides happen to be.  The keystore
        /// is the checked-in debug config at <c>keystore/debug.keystore</c>; never use it
        /// for Play Store submissions.</para>
        /// </summary>
        [MenuItem("Emberfall/Build/Android APK (content + player, IL2CPP ARM64)")]
        public static void BuildAndroid()
        {
            SyncSceneList();

            bool perfCaptureBuild = Environment.GetEnvironmentVariable(
                "EMBERFALL_PERF_CAPTURE_BUILD") == "1";

            // EMBERFALL_PROFILER_BUILD=1 makes the APK a development player that the Editor's
            // Profiler can attach to over adb. It exists because `dumpsys meminfo` can say a pool
            // grew — Unknown +411 MB and Graphics +127 MB in one second, measured on a Pixel 5a
            // 2026-08-26 — and cannot say WHAT allocated it, which is the only question worth
            // asking once you know something is eating the device. Off by default and never set in
            // CI: a development player carries profiler overhead and is not shippable.
            bool profilerBuild = Environment.GetEnvironmentVariable(
                "EMBERFALL_PROFILER_BUILD") == "1";
            string buildCommit = Environment.GetEnvironmentVariable("EMBERFALL_BUILD_COMMIT");
            string originalVersion = PlayerSettings.bundleVersion;
            bool originalFrameTimingStats = PlayerSettings.enableFrameTimingStats;

            try
            {
                if (perfCaptureBuild)
                {
                    // FrameTimingManager is always live in development players, but release
                    // players require this build-time setting. Keep it scoped to the EF-064
                    // measurement APK so ordinary release builds carry no profiling overhead.
                    PlayerSettings.enableFrameTimingStats = true;

                    if (!string.IsNullOrEmpty(buildCommit))
                    {
                        string shortCommit = buildCommit.Substring(0, Math.Min(12, buildCommit.Length));
                        PlayerSettings.bundleVersion = $"{originalVersion}+perf.{shortCommit}";
                    }

                    Debug.Log($"[EmberfallBuild] EF-064 capture build: frame timing stats enabled; " +
                              $"version {PlayerSettings.bundleVersion}");
                }

                // Force IL2CPP + ARM64 for this build regardless of editor overrides.
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,
                    ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

                // Debug signing config checked in as config, not secret.
                PlayerSettings.Android.keystoreName   = KeystorePath;
                PlayerSettings.Android.keystorePass   = KeystorePass;
                PlayerSettings.Android.keyaliasName   = KeyAliasName;
                PlayerSettings.Android.keyaliasPass   = KeyAliasPass;
                PlayerSettings.Android.useCustomKeystore = true;

                BuildAndroidAddressables();

                Directory.CreateDirectory(Path.GetDirectoryName(AndroidOutputPath));
                var options = new BuildPlayerOptions
                {
                    scenes             = AllScenes,
                    locationPathName   = AndroidOutputPath,
                    target             = BuildTarget.Android,
                    targetGroup        = BuildTargetGroup.Android,
                    options            = profilerBuild
                        ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                        : BuildOptions.None,
                };
                if (profilerBuild)
                    // Attach over USB and let autoconnect do it. Measured 2026-08-26, Pixel 5a: the
                    // Editor creates `tcp:34998 localabstract:Unity-<package>` itself and the
                    // Profiler connects on 127.0.0.1:34998 — you do not add a forward by hand. The
                    // player ALSO listens on TCP 55000 and multicasts discovery on 225.0.0.222:54997,
                    // but that is the Wi-Fi path and the multicast does not survive the network here,
                    // so the device never shows in the dropdown. Do not chase 55000; it is a
                    // plausible-looking dead end that cost a session.
                    //
                    // Two limits worth knowing BEFORE you build, because neither announces itself:
                    //   * `Deep Profiler Supported: No` on Android IL2CPP — no per-callstack
                    //     allocation from the built-in Profiler, only which pool grew, which is what
                    //     `dumpsys meminfo` already told you.
                    //   * The Editor-side frame buffer grows without bound. Profiling a player that
                    //     is itself running away hit 1.28 GB in the out-of-process Profiler window
                    //     and stalled it; the window had to be killed. For an allocation question,
                    //     take a com.unity.memoryprofiler snapshot — it writes to a FILE and so
                    //     survives the player being OOM-killed — rather than recording frames.
                    Debug.Log("[EmberfallBuild] PROFILER build: development player, autoconnect "
                              + "profiler. Connect the phone by USB and launch the app; the Editor "
                              + "forwards tcp:34998 itself. Window > Analysis > Profiler, target "
                              + "`AndroidPlayer(...@ADB:<serial>)`. Stop Play Mode first — it steals "
                              + "the target and reads as 'cannot connect'. Deep Profiler is NOT "
                              + "supported here; use Memory Profiler snapshots for allocations.");
                Report(BuildPipeline.BuildPlayer(options), AndroidOutputPath);
            }
            finally
            {
                // These values identify and enable the measurement build, but they are not
                // project defaults. Restore them even when Addressables or IL2CPP fails.
                PlayerSettings.bundleVersion = originalVersion;
                PlayerSettings.enableFrameTimingStats = originalFrameTimingStats;
                AssetDatabase.SaveAssets();
            }
        }

        [MenuItem("Emberfall/Build/Windows Standalone (content + player)")]
        public static void BuildWindows()
        {
            SyncSceneList();
            BuildAddressables();

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            var options = new BuildPlayerOptions
            {
                // Menu first — testers enter through MainMenu and navigate from there.
                // Find Game, Single Player and Deck Builder each need their target scene
                // present or the button is a dead end in the shipping build. Deck Builder's
                // target is the roster, which reaches Collection in either mode (#154).
                scenes = AllScenes,
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            Report(BuildPipeline.BuildPlayer(options), OutputPath);
        }

        /// <summary>
        /// The one true product name. <b>Changing it moves every player's save data.</b>
        /// <see cref="Application.persistentDataPath"/> is <c>AppData/LocalLow/&lt;company&gt;/&lt;product&gt;</c>,
        /// so the collection root — profiles, rosters, decks, the account registry — is keyed on this
        /// string.
        /// </summary>
        private const string CanonicalProductName = "Emberfall";

        /// <summary>
        /// Restore <c>productName</c> after a multiplayer scenario has renamed it.
        ///
        /// <para><b>Why this exists.</b> Running a multiplayer scenario rewrites <c>productName</c> to
        /// <c>"Emberfall (Client)"</c> (the role name). That silently relocates
        /// <see cref="Application.persistentDataPath"/>, so the game boots against a brand-new empty
        /// collection and asks a returning player who they are — which is exactly what happened on
        /// 2026-07-27. The change lands in <c>ProjectSettings.asset</c> as an ordinary working-tree
        /// edit, and <b>committing it would permanently move the save location for every build</b>:
        /// the real collection becomes unreachable and any existing tester's collection orphans.</para>
        ///
        /// <para>Set through <c>PlayerSettings</c> rather than by editing the asset, because Unity holds
        /// project settings in memory and would overwrite a file edited underneath it.</para>
        /// </summary>
        [MenuItem("Emberfall/Build/Restore Product Name (undo scenario rename)")]
        public static void RestoreProductName()
        {
            string current = PlayerSettings.productName;

            // Set and save UNCONDITIONALLY, even when the in-memory value already looks right.
            // The scenario writes the rename into ProjectSettings.asset from ITS OWN process, so this
            // Editor can hold "Emberfall" in memory while the file on disk — the thing git commits —
            // says "Emberfall (Client)". An early return on `current == canonical` therefore skipped the
            // one step that mattered and left the file wrong (observed 2026-07-27). The in-memory value
            // is not evidence about the file.
            PlayerSettings.productName = CanonicalProductName;
            AssetDatabase.SaveAssets();

            if (current == CanonicalProductName)
                Debug.Log($"[EmberfallBuild] productName in memory was already '{CanonicalProductName}'; " +
                          "re-saved so ProjectSettings.asset on disk agrees. Collection root: " +
                          $"{Application.persistentDataPath}");
            else
                Debug.LogWarning($"[EmberfallBuild] productName was '{current}' — restored to " +
                                 $"'{CanonicalProductName}'. A renamed product points the game at a " +
                                 "different collection root; never commit that rename.");
        }

        /// <summary>
        /// Put the six playable scenes in <c>EditorBuildSettings</c>, menu first.
        ///
        /// <para><b>This is what makes scene navigation work in the EDITOR.</b>
        /// <c>SceneManager.LoadScene(name)</c> resolves against the build list, so with an empty list —
        /// which is how this project sat until 2026-07-27 — every menu button that loads a scene is a
        /// dead button in the Editor, with no exception thrown and nothing logged. The Editor is one of
        /// the two seats for local two-player testing, so it has to work there, not only in a player.</para>
        /// </summary>
        [MenuItem("Emberfall/Build/Sync Scene List (fixes Editor scene nav)")]
        public static void SyncSceneList()
        {
            string[] paths = AllScenes;
            var entries = new EditorBuildSettingsScene[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                entries[i] = new EditorBuildSettingsScene(paths[i], enabled: true);

            EditorBuildSettings.scenes = entries;

            // Assigning the property only sets it in memory, so ask for a save.
            //
            // WARNING: on 6000.5.1f1 this does NOT reach ProjectSettings/EditorBuildSettings.asset.
            // Measured 2026-08-11 during #154: after removing a scene, the in-memory list read back
            // correctly through EditorBuildSettings.scenes while the file on disk kept the removed
            // entry, and its mtime did not move across two SyncSceneList runs or a File/Save Project.
            // (The 2026-07-27 note that used to sit here claimed the opposite; it no longer holds.)
            //
            // What that means for you: this menu item fixes Editor scene nav for THIS session, but it
            // is not a way to change what git sees. After running it, diff EditorBuildSettings.asset
            // and hand-edit the file to match if you need the change committed — the in-memory list is
            // authoritative, so a file edited to agree with it will not be clobbered by a later flush.
            AssetDatabase.SaveAssets();

            Debug.Log($"[EmberfallBuild] Build scene list set: {string.Join(", ", paths)}");
        }

        /// <summary>
        /// Refuse the build up front if a scene has unsaved changes.
        ///
        /// <para><b>Order is the whole point.</b> Addressables' own <c>BuildPlayerContent</c> makes this
        /// same check — but only after a full clean-and-rebuild of the content, which cost 8m36s on
        /// 2026-07-27 before reporting a one-word failure ("Unsaved scenes") that was knowable in
        /// milliseconds. A precondition belongs before the expensive step, not inside it.</para>
        ///
        /// <para>It reports the scene by name and does <b>not</b> save for you: saving writes whatever
        /// is currently in that scene to disk, and a build script is the wrong thing to be making that
        /// decision unattended.</para>
        /// </summary>
        private static void RequireSavedScenes()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;

                throw new BuildFailedException(
                    $"[EmberfallBuild] Scene '{scene.name}' ({scene.path}) has unsaved changes. " +
                    "Addressables will not build content over a dirty scene. Save it (Ctrl+S) or " +
                    "reload it to discard, then build again — this check is here so you find out now " +
                    "rather than after the content rebuild.");
            }
        }

        // Profile name is the stable human-readable key; the corresponding m_Id lives in
        // AddressableAssetSettings.asset. If the profile is renamed there, update this too.
        private const string AndroidProfileName = "Android";

        /// <summary>
        /// Builds Addressables content for Android using the dedicated Android profile.
        ///
        /// <para><b>The active build target MUST already be Android before calling this.</b>
        /// Texture compression (ASTC via <see cref="CardArtPostprocessor"/>) is applied by
        /// Unity's TextureImporter at content-build time and requires Android to be the active
        /// platform — the profile alone does not change what compression is used.</para>
        ///
        /// <para>The Android profile hardcodes <c>BuildTarget = Android</c> so
        /// <c>[BuildTarget]</c> path tokens always resolve to <c>Android</c>, even if the
        /// Editor's active target changes between profile selection and build. Called by
        /// EF-060's <c>BuildAndroid()</c> after it switches the active build target.</para>
        /// </summary>
        [MenuItem("Emberfall/Build/Android Addressables Content (requires Android active target)")]
        public static void BuildAndroidAddressables()
        {
            var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new BuildFailedException("[EmberfallBuild] AddressableAssetSettings not found.");

            string androidProfileId = settings.profileSettings.GetProfileId(AndroidProfileName);
            if (string.IsNullOrEmpty(androidProfileId))
                throw new BuildFailedException(
                    $"[EmberfallBuild] Addressables profile '{AndroidProfileName}' not found. " +
                    "Check AddressableAssetSettings.asset.");

            string previousProfileId = settings.activeProfileId;
            settings.activeProfileId = androidProfileId;
            try
            {
                BuildAddressables();
            }
            finally
            {
                settings.activeProfileId = previousProfileId;
            }
        }

        private static void BuildAddressables()
        {
            RequireSavedScenes();

            AddressableAssetSettings.CleanPlayerContent();
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
            if (!string.IsNullOrEmpty(contentResult.Error))
                throw new BuildFailedException(
                    $"[EmberfallBuild] Addressables content build FAILED: {contentResult.Error}");
            Debug.Log($"[EmberfallBuild] Addressables content built ({contentResult.Duration:F1}s)");
        }

        private static void Report(BuildReport report, string outputPath)
        {
            var summary = report.summary;

            // MUST throw, not log. A failed player build leaves the PREVIOUS run's Emberfall.exe
            // sitting next to freshly-rebuilt Addressables content, and neither the exe's timestamp
            // (Unity skips rewriting a content-identical launcher stub) nor Emberfall_Data's
            // directory mtime moves — so a stale build is indistinguishable from a good one by
            // inspection. Failing loudly is the only thing that tells you.
            if (summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"[EmberfallBuild] Player build {summary.result}: {summary.totalErrors} error(s). " +
                    $"'{outputPath}' may still hold a STALE player from a previous build — do not distribute it.");

            Debug.Log($"[EmberfallBuild] Player build {summary.result}: " +
                      $"{summary.totalErrors} error(s), {summary.totalSize / (1024 * 1024)} MB → {outputPath}");
        }
    }
}
