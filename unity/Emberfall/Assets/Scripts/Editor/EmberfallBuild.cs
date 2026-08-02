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
        private const string LocalScenePath = "Assets/Scenes/Board.unity";
        private const string DeckbuilderScenePath = "Assets/Scenes/DeckbuilderSpike.unity";
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
                // Both board scenes ride along so Find Game and Single Player actually go somewhere,
                // and the deckbuilder so its own menu button isn't a dead end in this build too.
                scenes = new[] { MenuScenePath, ScenePath, LocalScenePath, DeckbuilderScenePath },
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

            // Force IL2CPP + ARM64 for this build regardless of editor overrides.
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android,
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
                scenes             = new[] { MenuScenePath, ScenePath, LocalScenePath, DeckbuilderScenePath },
                locationPathName   = AndroidOutputPath,
                target             = BuildTarget.Android,
                targetGroup        = BuildTargetGroup.Android,
                options            = BuildOptions.None,
            };
            Report(BuildPipeline.BuildPlayer(options), AndroidOutputPath);
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
                // present or the button is a dead end in the shipping build.
                scenes = new[] { MenuScenePath, ScenePath, LocalScenePath, DeckbuilderScenePath },
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
        /// Put the four playable scenes in <c>EditorBuildSettings</c>, menu first.
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
            string[] paths = { MenuScenePath, ScenePath, LocalScenePath, DeckbuilderScenePath };
            var entries = new EditorBuildSettingsScene[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                entries[i] = new EditorBuildSettingsScene(paths[i], enabled: true);

            EditorBuildSettings.scenes = entries;

            // Assigning the property only sets it in memory. Without this, EditorBuildSettings.asset
            // still reads `m_Scenes: []` on disk — so the change is invisible to git, does not survive
            // an Editor restart, and cannot be committed. Verified on 2026-07-27 by reading the asset
            // straight after the assignment.
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
