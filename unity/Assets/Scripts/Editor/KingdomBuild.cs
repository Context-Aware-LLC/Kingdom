using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// M2 D1 player-build entry point: Addressables content build + Windows standalone player
    /// in one step (content must be rebuilt whenever addressable assets change, or the player
    /// ships a stale catalog). Output: unity/Builds/Windows/Bad Ideas.exe.
    /// </summary>
    public static class KingdomBuild
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
        private const string PackVaultScenePath = "Assets/Scenes/PackVault.unity";

        /// <summary>
        /// EVERY shipped scene, in build order — index 0 is the boot scene.
        ///
        /// <para><b>This exists because the list used to be written out four times</b>: once per
        /// BuildPlayerOptions (Android, Windows Standalone, and a since-removed Windows DEV seat) and once in
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
            CollectionScenePath, ShopScenePath, PackVaultScenePath,
        };
        private const string OutputPath = "Builds/Windows/Bad Ideas.exe";

        private const string AndroidOutputPath = "Builds/Android/Kingdom.apk";

        /// <summary>
        /// Headless Android build entry point (EF-060): IL2CPP + ARM64 APK via the Android
        /// Addressables profile. Invoke via <c>-executeMethod Kingdom.Editor.KingdomBuild.BuildAndroid
        /// -buildTarget android</c> so the build target is already Android at startup.
        ///
        /// <para>IL2CPP and ARM64 are forced explicitly here so the build is deterministic
        /// regardless of whatever the Editor's per-user overrides happen to be. Local APKs use
        /// Unity's default debug signing. With <c>KINGDOM_PLAY_RELEASE=1</c> (set by
        /// <c>build-android.ps1 -PlayRelease</c>) the same path emits a Play AAB signed with the
        /// upload key instead -- see <see cref="PlaySigning"/>.</para>
        ///
        /// <para><b>ARM64 is not a preference and there is no x86_64 build to reach for.</b>
        /// Unity 6000.5.1f1 ships no Android x86_64 runtime — <c>Variations/il2cpp/Release/Libs</c>
        /// holds <c>arm64-v8a</c>, <c>armeabi-v7a</c> and <c>armv9</c> and nothing else — so
        /// setting <c>AndroidArchitecture.X86_64</c> leaves the architecture set empty and the
        /// build dies in <c>PrepareForBuild</c> with <i>"Target architecture not specified"</i>,
        /// a message that names neither x86_64 nor the missing libs. The enum member still
        /// exists, which is what makes this look available.</para>
        ///
        /// <para><b>THE EMULATOR IS STILL A TEST RIG, and the paragraph that used to stand here
        /// saying otherwise was WRONG (corrected 2026-08-31, Sam: "I run this all the time on the
        /// emulator").</b> It reasoned from the missing x86_64 libs straight to "no APK this repo
        /// can produce will install on an AVD", and that step does not follow: a modern x86_64
        /// system image TRANSLATES arm64. Measured on the Pixel_7 AVD (API 36.1,
        /// google_apis_playstore/x86_64) the same minute the claim was removed:</para>
        /// <code>
        /// ro.product.cpu.abilist        = x86_64,arm64-v8a   &lt;- advertises arm64
        /// ro.dalvik.vm.isa.arm64        = x86_64             &lt;- arm64 mapped onto x86_64
        /// ro.enable.native.bridge.exec  = 1
        /// adb install -r Kingdom.apk  -> Success
        /// </code>
        /// <para>So an arm64-v8a APK installs and runs there. The x86_64 paragraph above is about
        /// what Unity can BUILD and remains true; it says nothing about what an AVD can RUN, and
        /// conflating the two cost a session. Native-bridge translation arrived in the API 30 era,
        /// so any reasonably current image has it — check <c>abilist</c> rather than assuming
        /// either way.</para>
        /// <para>RETIREMENT TRIGGER: if <c>ro.product.cpu.abilist</c> on the project's AVD stops
        /// listing <c>arm64-v8a</c> (an older or non-Google image), the emulator stops being a rig
        /// and this paragraph, plus the emulator note in project memory, change together.</para>
        /// </summary>
        [MenuItem("Kingdom/Build/Android APK")]
        public static void BuildAndroid() => BuildAndroid(exportProject: false);

        /// <summary>
        /// The same Android build, written out as a Gradle project that Android Studio opens
        /// (<c>launcher/</c> + <c>unityLibrary/</c>) instead of an APK. Output:
        /// unity/Builds/AndroidStudio. Each export overwrites that folder, so an edit made there in
        /// Android Studio is lost on the next export unless it is carried back into Unity.
        /// </summary>
        [MenuItem("Kingdom/Build/Android Studio Project")]
        public static void ExportAndroidStudioProject() => BuildAndroid(exportProject: true);

        private const string AndroidStudioOutputPath = "Builds/AndroidStudio";

        private static void BuildAndroid(bool exportProject)
        {
            SyncSceneList();

            bool perfCaptureBuild = Environment.GetEnvironmentVariable(
                "KINGDOM_PERF_CAPTURE_BUILD") == "1";
            bool fastBuild = Environment.GetEnvironmentVariable("KINGDOM_FAST_BUILD") == "1";
            bool cleanBuild = Environment.GetEnvironmentVariable("KINGDOM_CLEAN_BUILD") == "1";

            // KINGDOM_PROFILER_BUILD=1 makes the APK a development player that the Editor's
            // Profiler can attach to over adb. It exists because `dumpsys meminfo` can say a pool
            // grew — Unknown +411 MB and Graphics +127 MB in one second, measured on a Pixel 5a
            // 2026-08-26 — and cannot say WHAT allocated it, which is the only question worth
            // asking once you know something is eating the device. Off by default and never set in
            // CI: a development player carries profiler overhead and is not shippable.
            bool profilerBuild = Environment.GetEnvironmentVariable(
                "KINGDOM_PROFILER_BUILD") == "1";
            string buildCommit = Environment.GetEnvironmentVariable("KINGDOM_BUILD_COMMIT");
            PlaySigning play = PlaySigning.FromEnvironment();
            if (fastBuild && (play != null || perfCaptureBuild))
                throw new BuildFailedException(
                    "[KingdomBuild] Fast iteration cannot be used for a Play release or performance capture.");
            if (play != null && (perfCaptureBuild || profilerBuild))
                throw new BuildFailedException(
                    "[KingdomBuild] A Play release cannot also be a perf-capture or profiler build.");
            if (play != null && exportProject)
                throw new BuildFailedException(
                    "[KingdomBuild] A Play release is an AAB; it cannot also be an Android Studio export.");

            string originalVersion = PlayerSettings.bundleVersion;
            bool originalExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            bool originalFrameTimingStats = PlayerSettings.enableFrameTimingStats;
            bool originalAppBundle = EditorUserBuildSettings.buildAppBundle;
            bool originalCustomKeystore = PlayerSettings.Android.useCustomKeystore;
            string originalKeystore = PlayerSettings.Android.keystoreName;
            string originalAlias = PlayerSettings.Android.keyaliasName;
            bool originalMinify = PlayerSettings.Android.minifyRelease;
            var originalCompiler = PlayerSettings.GetIl2CppCompilerConfiguration(NamedBuildTarget.Android);
            var originalCodeGeneration = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.Android);
#if UNITY_ANDROID
            var originalLto = UnityEditor.Android.UserBuildSettings.linkTimeOptimization;
#endif
            string outputPath = play != null ? PlayOutputPath
                : exportProject ? AndroidStudioOutputPath
                : fastBuild ? "Builds/Android/Kingdom-fast.apk"
                : AndroidOutputPath;

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

                    Debug.Log($"[KingdomBuild] EF-064 capture build: frame timing stats enabled; " +
                              $"version {PlayerSettings.bundleVersion}");
                }

                // Force IL2CPP + ARM64 for this build regardless of editor overrides.
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,
                    ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

                // Optimized builds must not depend on an active Editor build profile. Fast
                // iteration explicitly trades native runtime speed and R8 shrinking for build time.
                PlayerSettings.Android.minifyRelease = !fastBuild;
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android,
                    fastBuild ? Il2CppCompilerConfiguration.Debug : Il2CppCompilerConfiguration.Master);
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.Android,
                    fastBuild ? Il2CppCodeGeneration.OptimizeSize : Il2CppCodeGeneration.OptimizeSpeed);
#if UNITY_ANDROID
                // The Android editor assemblies are only referenced while Android is the active
                // target, which this entry point already requires.
                UnityEditor.Android.UserBuildSettings.linkTimeOptimization =
                    fastBuild ? Unity.Android.Types.AndroidLinkTimeOptimization.None
                        : Unity.Android.Types.AndroidLinkTimeOptimization.Thin;
#endif
                Debug.Log(fastBuild
                    ? "[KingdomBuild] FAST ITERATION: Debug C++, smaller code generation, no LTO or R8. Not for performance measurements."
                    : "[KingdomBuild] OPTIMIZED: Master C++, faster runtime code generation, Thin LTO and R8.");

                // This local APK path uses Unity's default debug signing. Do not silently persist
                // a custom signing configuration into ProjectSettings whenever this menu runs.
                PlayerSettings.Android.useCustomKeystore = false;
                EditorUserBuildSettings.buildAppBundle = false;
                EditorUserBuildSettings.exportAsGoogleAndroidProject = exportProject;

                if (play != null)
                {
                    // Play release: an AAB signed with the UPLOAD key, which lives outside the repo
                    // (C:\Users\User\.keystore\KingdomOfBadIdeas). Every signing field is put back
                    // in `finally` -- Unity writes keystoreName into ProjectSettings on save, and
                    // a committed path to one machine's keystore is a build that fails everywhere
                    // else. The passwords are never serialized by Unity at all.
                    PlayerSettings.Android.useCustomKeystore = true;
                    PlayerSettings.Android.keystoreName = play.KeystorePath;
                    PlayerSettings.Android.keystorePass = play.Password;
                    PlayerSettings.Android.keyaliasName = play.Alias;
                    PlayerSettings.Android.keyaliasPass = play.Password;
                    EditorUserBuildSettings.buildAppBundle = true;

                    // The version code is the one thing a Play build SHOULD persist: Play refuses
                    // an upload whose code is not above every earlier one, so it is a floor.
                    if (play.VersionCode > 0)
                        PlayerSettings.Android.bundleVersionCode = play.VersionCode;
                    Debug.Log($"[KingdomBuild] PLAY RELEASE: AAB signed with alias '{play.Alias}', " +
                              $"versionCode {PlayerSettings.Android.bundleVersionCode}, " +
                              $"version {PlayerSettings.bundleVersion}");
                }

                BuildAndroidAddressables(cleanBuild);

                Directory.CreateDirectory(exportProject ? outputPath : Path.GetDirectoryName(outputPath));
                var options = new BuildPlayerOptions
                {
                    scenes             = AllScenes,
                    locationPathName   = outputPath,
                    target             = BuildTarget.Android,
                    targetGroup        = BuildTargetGroup.Android,
                    options            = profilerBuild
                        ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                        : BuildOptions.None,
                };
                if (cleanBuild) options.options |= BuildOptions.CleanBuildCache;
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
                    Debug.Log("[KingdomBuild] PROFILER build: development player, autoconnect "
                              + "profiler. Connect the phone by USB and launch the app; the Editor "
                              + "forwards tcp:34998 itself. Window > Analysis > Profiler, target "
                              + "`AndroidPlayer(...@ADB:<serial>)`. Stop Play Mode first — it steals "
                              + "the target and reads as 'cannot connect'. Deep Profiler is NOT "
                              + "supported here; use Memory Profiler snapshots for allocations.");
                Report(BuildPipeline.BuildPlayer(options), outputPath);
            }
            finally
            {
                // These values identify and enable the measurement build, but they are not
                // project defaults. Restore them even when Addressables or IL2CPP fails.
                // A Play build keeps the version StoreVersionBump gave it; only the perf
                // capture's "+perf.<sha>" suffix is temporary.
                if (play == null) PlayerSettings.bundleVersion = originalVersion;
                PlayerSettings.enableFrameTimingStats = originalFrameTimingStats;
                PlayerSettings.Android.minifyRelease = originalMinify;
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, originalCompiler);
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.Android, originalCodeGeneration);
#if UNITY_ANDROID
                UnityEditor.Android.UserBuildSettings.linkTimeOptimization = originalLto;
#endif
                // Back to whatever the Keystore Manager had, not to off: Sam signs store builds
                // from the Build Profiles window with the upload key configured there.
                PlayerSettings.Android.useCustomKeystore = originalCustomKeystore;
                PlayerSettings.Android.keystoreName = originalKeystore;
                PlayerSettings.Android.keyaliasName = originalAlias;
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
                EditorUserBuildSettings.buildAppBundle = originalAppBundle;
                EditorUserBuildSettings.exportAsGoogleAndroidProject = originalExport;
                AssetDatabase.SaveAssets();
            }
        }

        private const string PlayOutputPath = "Builds/Android/KingdomOfBadIdeas.aab";

        /// <summary>Upload-key signing for a Play release, read from the environment that
        /// <c>tools/ci/build-android.ps1 -PlayRelease</c> sets. Null means an ordinary local APK.
        /// A half-set environment is refused rather than falling back to debug signing, because an
        /// AAB signed with the wrong key is only discovered when Play rejects the upload.</summary>
        private sealed class PlaySigning
        {
            public string KeystorePath, Alias, Password;
            public int VersionCode;

            public static PlaySigning FromEnvironment()
            {
                if (Environment.GetEnvironmentVariable("KINGDOM_PLAY_RELEASE") != "1") return null;
                var s = new PlaySigning
                {
                    KeystorePath = Environment.GetEnvironmentVariable("KINGDOM_UPLOAD_KEYSTORE"),
                    Alias = Environment.GetEnvironmentVariable("KINGDOM_UPLOAD_KEY_ALIAS"),
                    Password = Environment.GetEnvironmentVariable("KINGDOM_UPLOAD_KEYSTORE_PASS"),
                };
                if (string.IsNullOrEmpty(s.KeystorePath) || !File.Exists(s.KeystorePath))
                    throw new BuildFailedException(
                        $"[KingdomBuild] Play release: upload keystore not found at '{s.KeystorePath}'.");
                if (string.IsNullOrEmpty(s.Alias) || string.IsNullOrEmpty(s.Password))
                    throw new BuildFailedException(
                        "[KingdomBuild] Play release: KINGDOM_UPLOAD_KEY_ALIAS and " +
                        "KINGDOM_UPLOAD_KEYSTORE_PASS must both be set.");
                int.TryParse(Environment.GetEnvironmentVariable("KINGDOM_VERSION_CODE"), out s.VersionCode);
                return s;
            }
        }

        [MenuItem("Kingdom/Build/Windows Standalone")]
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
        /// The one true product name. It is the label under the app icon on Android and iOS, and on
        /// Windows <b>changing it moves every player's save data</b>:
        /// <see cref="Application.persistentDataPath"/> is <c>AppData/LocalLow/&lt;company&gt;/&lt;product&gt;</c>,
        /// so the desktop collection root — profiles, rosters, decks, the account registry — is keyed on
        /// this string. (On mobile the save root is keyed on the package id instead.) Renamed from
        /// "Kingdom" 2026-09-28, before any store upload, for the Kingdom of Bad Ideas name.
        /// </summary>
        private const string CanonicalProductName = "Bad Ideas";

        /// <summary>
        /// Restore <c>productName</c> after a multiplayer scenario has renamed it.
        ///
        /// <para><b>Why this exists.</b> Running a multiplayer scenario rewrites <c>productName</c> to
        /// <c>"Kingdom (Client)"</c> (the role name). That silently relocates
        /// <see cref="Application.persistentDataPath"/>, so the game boots against a brand-new empty
        /// collection and asks a returning player who they are — which is exactly what happened on
        /// 2026-07-27. The change lands in <c>ProjectSettings.asset</c> as an ordinary working-tree
        /// edit, and <b>committing it would permanently move the save location for every build</b>:
        /// the real collection becomes unreachable and any existing tester's collection orphans.</para>
        ///
        /// <para>Set through <c>PlayerSettings</c> rather than by editing the asset, because Unity holds
        /// project settings in memory and would overwrite a file edited underneath it.</para>
        /// </summary>
        [MenuItem("Kingdom/Build/Restore Product Name")]
        public static void RestoreProductName()
        {
            string current = PlayerSettings.productName;

            // Set and save UNCONDITIONALLY, even when the in-memory value already looks right.
            // The scenario writes the rename into ProjectSettings.asset from ITS OWN process, so this
            // Editor can hold the right name in memory while the file on disk — the thing git commits —
            // says "Kingdom (Client)". An early return on `current == canonical` therefore skipped the
            // one step that mattered and left the file wrong (observed 2026-07-27). The in-memory value
            // is not evidence about the file.
            PlayerSettings.productName = CanonicalProductName;
            AssetDatabase.SaveAssets();

            if (current == CanonicalProductName)
                Debug.Log($"[KingdomBuild] productName in memory was already '{CanonicalProductName}'; " +
                          "re-saved so ProjectSettings.asset on disk agrees. Collection root: " +
                          $"{Application.persistentDataPath}");
            else
                Debug.LogWarning($"[KingdomBuild] productName was '{current}' — restored to " +
                                 $"'{CanonicalProductName}'. A renamed product points the game at a " +
                                 "different collection root; never commit that rename.");
        }

        /// <summary>
        /// Put the six playable scenes in <c>EditorBuildSettings</c>, menu first.
        ///
        /// <para><b>This is what makes scene navigation work in the EDITOR.</b>
        /// <c>SceneManager.LoadScene(name)</c> resolves against the build list.</para>
        /// </summary>
        [MenuItem("Kingdom/Build/Sync Scene List")]
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

            Debug.Log($"[KingdomBuild] Build scene list set: {string.Join(", ", paths)}");
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
                    $"[KingdomBuild] Scene '{scene.name}' ({scene.path}) has unsaved changes. " +
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
        public static void BuildAndroidAddressables(bool clean = false)
        {
            var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new BuildFailedException("[KingdomBuild] AddressableAssetSettings not found.");

            string androidProfileId = settings.profileSettings.GetProfileId(AndroidProfileName);
            if (string.IsNullOrEmpty(androidProfileId))
                throw new BuildFailedException(
                    $"[KingdomBuild] Addressables profile '{AndroidProfileName}' not found. " +
                    "Check AddressableAssetSettings.asset.");

            string previousProfileId = settings.activeProfileId;
            settings.activeProfileId = androidProfileId;
            try
            {
                BuildAddressables(clean);
            }
            finally
            {
                settings.activeProfileId = previousProfileId;
            }
        }

        private static void BuildAddressables(bool clean = true)
        {
            RequireSavedScenes();

            if (clean) AddressableAssetSettings.CleanPlayerContent();
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
            if (!string.IsNullOrEmpty(contentResult.Error))
                throw new BuildFailedException(
                    $"[KingdomBuild] Addressables content build FAILED: {contentResult.Error}");
            Debug.Log($"[KingdomBuild] Addressables content built ({contentResult.Duration:F1}s)");
        }

        private static void Report(BuildReport report, string outputPath)
        {
            var summary = report.summary;

            // MUST throw, not log. A failed player build leaves the PREVIOUS run's Bad Ideas.exe
            // sitting next to freshly-rebuilt Addressables content, and neither the exe's timestamp
            // (Unity skips rewriting a content-identical launcher stub) nor Bad Ideas_Data's
            // directory mtime moves — so a stale build is indistinguishable from a good one by
            // inspection. Failing loudly is the only thing that tells you.
            if (summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"[KingdomBuild] Player build {summary.result}: {summary.totalErrors} error(s). " +
                    $"'{outputPath}' may still hold a STALE player from a previous build — do not distribute it.");

            Debug.Log($"[KingdomBuild] Player build {summary.result}: " +
                      $"{summary.totalErrors} error(s), {summary.totalSize / (1024 * 1024)} MB → {outputPath}");
        }
    }
}
