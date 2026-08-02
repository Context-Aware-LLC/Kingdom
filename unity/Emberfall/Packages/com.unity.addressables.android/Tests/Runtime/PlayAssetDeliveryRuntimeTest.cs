using System;
using System.IO;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Android;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Android;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.Exceptions;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.AddressableAssets.Android;
#endif

class PlayAssetDeliveryRuntimeTest : PlayAssetDeliveryBuildTestsBase, IPrebuildSetup, IPostBuildCleanup
{
    const string kNonAddressableTexture = "NonAddressableTexture";
    const string kRemoteTexture = "RemoteTexture.png";
    const string kSyncMessage = "Play Asset Delivery provider does not support synchronous Addressable loading. Please do not use WaitForCompletion with Play Asset Delivery provider.";
    const string kStartLoadMessage = "Start sync loading";
    const string kStopLoadMessage = "Stop sync loading";

#if UNITY_ANDROID
#if UNITY_EDITOR
    // running test in Editor playmode, default texture compression is expected
    const string kExpectedTextureFormat = "ETC2";
#else
    // running test on the device, best suitable texture compression is expected
    const string kExpectedTextureFormat = "ASTC";
#endif
#else
    // running test when target is set to Standalone Windows or MacOS, dxt5 texture compression is expected
    const string kExpectedTextureFormat = "DXT5";
#endif

    public void Setup()
    {
#if UNITY_EDITOR
        // Android specific build settings, other platforms are not affected
        PlayerSettings.Android.textureCompressionFormats = new[] { TextureCompressionFormat.ETC2, TextureCompressionFormat.ASTC };
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.splitApplicationBinary = true;
        PlayerSettings.SetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.Android, UnityEditor.Build.Il2CppCodeGeneration.OptimizeSize);
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
        EditorUserBuildSettings.buildAppBundle = true;

        InitAddressables();
        var settings = AddressableAssetSettingsDefaultObject.Settings;

        // Create Remote group
        var group = settings.CreateGroup(kRemoteGroupName, true, false, false, null, typeof(BundledAssetGroupSchema));
        var spriteEntry = settings.CreateOrMoveEntry(CreateTexture(Path.Combine(kSingleTestAssetFolder, kRemoteTexture)), group, false, false);

        // To test Remote group behavior REMOTE_GROUP_ADDRESS should be set in Yamato script (BOKKEN_HOST_IP:Port), otherwise all groups will be local
        var hostAddress = Environment.GetEnvironmentVariable("REMOTE_GROUP_ADDRESS");
        if (!string.IsNullOrEmpty(hostAddress))
        {
            settings.profileSettings.SetValue(settings.activeProfileId, AddressableAssetSettings.kRemoteLoadPath, $"http://{hostAddress}");
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(group.Settings, AddressableAssetSettings.kRemoteBuildPath);
            schema.LoadPath.SetVariableByName(group.Settings, AddressableAssetSettings.kRemoteLoadPath);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        }

        // To test texture packed with the application
        AssetDatabase.CreateFolder(Path.Combine("Assets", kTestFolder), "Resources");
        CreateTexture(Path.Combine(kSingleTestAssetFolder, "Resources", $"{kNonAddressableTexture}.png"));

        settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
        var builderInput = new AddressablesDataBuilderInput(settings);

        if (BuildPipeline.IsBuildTargetSupported(builderInput.TargetGroup, builderInput.Target))
        {
            var builder = GetBuilderOfType(settings, typeof(BuildScriptPlayAssetDelivery));
            var result = builder.BuildData<AddressableAssetBuildResult>(builderInput);
            Assert.IsTrue(string.IsNullOrEmpty(result.Error));
            settings.ActivePlayModeDataBuilderIndex = settings.DataBuilders.FindIndex(b => b is BuildScriptPackedPlayMode);
        }
        else
        {
            // running test in Editor without Standalone support, using FastMode to load addressables, no need to build data
            settings.ActivePlayModeDataBuilderIndex = settings.DataBuilders.FindIndex(b => b is BuildScriptFastMode);
        }
#endif
    }

    public void Cleanup()
    {
#if UNITY_EDITOR
        CleanupAddressables(false);
#endif
    }

    AsyncOperationHandle<Texture2D> opHandle;
    GetAssetPackStateAsyncOperation opGetAssetPackInfo;

    [UnityTest]
    [UnityPlatform(
        RuntimePlatform.Android,
        RuntimePlatform.WindowsPlayer,
        RuntimePlatform.OSXPlayer,
        RuntimePlatform.WindowsEditor,
        RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor
    )]
    public IEnumerator TexturesCanBeLoaded()
    {
#if !UNITY_EDITOR && UNITY_ANDROID
        // checking asset pack states only when running on the Android device
        var assetPackNames = new string[TotalNumberOfGroups];
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            assetPackNames[i] = GroupName(i).Item2;
        }
        opGetAssetPackInfo = AndroidAssetPacks.GetAssetPackStateAsync(assetPackNames);
        while (!opGetAssetPackInfo.isDone)
        {
            yield return opGetAssetPackInfo;
        }
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            var state = Array.Find(opGetAssetPackInfo.states, s => s.name == assetPackNames[i]);
            var status = GenerateDeliveryType(i) == DeliveryType.InstallTime || GenerateDeliveryType(i) == DeliveryType.None ? AndroidAssetPackStatus.Unknown : AndroidAssetPackStatus.NotInstalled;
            Assert.AreEqual(status, state.status, $"Before downloading: asset pack {state.name} delivery type {GenerateDeliveryType(i)}");
        }
#endif

        // checking non-addressable texture
        var texture = Resources.Load<Texture2D>(kNonAddressableTexture);
        Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {kNonAddressableTexture} {texture.format}");

#if !UNITY_EDITOR && UNITY_ANDROID
        // checking for sync loading on Android while no asset packs are downloaded
        // there are multiple error messages and exceptions generated inside the main Addressables package if asset can't be loaded, but we're checking Addressables for Android specific messages only
        LogAssert.ignoreFailingMessages = true;

        // install time
        Debug.Log(kStartLoadMessage);
        LogAssert.Expect(LogType.Log, kStartLoadMessage);
        opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, TextureName(0)).Replace('\\', '/'));
        try
        {
            opHandle.WaitForCompletion();
        }
        catch (Exception e) {}
        // the first synchronous loading from install time asset pack works if Addressables are initialized before this operation starts, this may depend on the main Addressables package
        if (opHandle.Status == AsyncOperationStatus.Succeeded)
        {
            texture = opHandle.Result;
            Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {TextureName(0)} {texture.format}");
        }
        else
        {
            LogAssert.Expect(LogType.Error, kSyncMessage);
            LogAssert.Expect(LogType.Error, new RemoteProviderException(kSyncMessage).ToString());
        }
        Addressables.Release(opHandle);
        yield return new WaitForSeconds(1);

        // on demand
        Debug.Log(kStartLoadMessage);
        LogAssert.Expect(LogType.Log, kStartLoadMessage);
        opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, TextureName(5)).Replace('\\', '/'));
        try
        {
            opHandle.WaitForCompletion();
        }
        catch (Exception e) {}
        // the first synchronous loading from on demand asset pack should always fail
        LogAssert.Expect(LogType.Error, kSyncMessage);
        LogAssert.Expect(LogType.Error, new RemoteProviderException(kSyncMessage).ToString());
        Addressables.Release(opHandle);
        yield return new WaitForSeconds(1);

        Debug.Log(kStopLoadMessage);
        LogAssert.Expect(LogType.Log, kStopLoadMessage);
        // there should be no more error messages
        LogAssert.ignoreFailingMessages = false;
#endif

        // checking texture from Remote group
        opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, kRemoteTexture).Replace('\\', '/'));
        yield return opHandle;
        Assert.AreEqual(AsyncOperationStatus.Succeeded, opHandle.Status);
        texture = opHandle.Result;
        Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {kRemoteTexture} {texture.format}");
        Addressables.Release(opHandle);
        yield return null;

        // checking textures from Addressable groups
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, TextureName(i)).Replace('\\', '/'));
            yield return opHandle;

            Assert.AreEqual(AsyncOperationStatus.Succeeded, opHandle.Status);
            texture = opHandle.Result;
            Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {TextureName(i)} {texture.format}");
            Addressables.Release(opHandle);
            yield return null;
        }

#if !UNITY_EDITOR && UNITY_ANDROID
        opGetAssetPackInfo = AndroidAssetPacks.GetAssetPackStateAsync(assetPackNames);
        while (!opGetAssetPackInfo.isDone)
        {
            yield return opGetAssetPackInfo;
        }
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            var state = Array.Find(opGetAssetPackInfo.states, s => s.name == assetPackNames[i]);
            var status = GenerateDeliveryType(i) == DeliveryType.InstallTime || GenerateDeliveryType(i) == DeliveryType.None ? AndroidAssetPackStatus.Unknown : AndroidAssetPackStatus.Completed;
            Assert.AreEqual(status, state.status, $"After downloading: asset pack {state.name} delivery type {GenerateDeliveryType(i)}");
        }
#endif

        // check that sync downloading works (on Android only after asset packs are downloaded)
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, TextureName(i)).Replace('\\', '/'));
            opHandle.WaitForCompletion();
            Assert.AreEqual(AsyncOperationStatus.Succeeded, opHandle.Status);
            texture = opHandle.Result;
            Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {TextureName(i)} {texture.format}");
            Addressables.Release(opHandle);
            yield return null;
        }

        // trying to download from the same bundle second time to ensure that asset bundles caching works properly
        for (int i = 0; i < NumberOfGroups; ++i)
        {
            // Asking for dependencies, required to force address asset bundle for the second time
            var secondTexture = Path.Combine(kSingleTestAssetFolder, $"second_{TextureName(i)}").Replace('\\', '/');
            var opHandleDeps = Addressables.DownloadDependenciesAsync(secondTexture);
            yield return opHandleDeps;

            opHandle = Addressables.LoadAssetAsync<Texture2D>(secondTexture);
            yield return opHandle;

            Assert.AreEqual(AsyncOperationStatus.Succeeded, opHandle.Status);
            texture = opHandle.Result;
            Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture second_{TextureName(i)} {texture.format}");
            Addressables.Release(opHandle);
            yield return null;
        }

#if !UNITY_EDITOR && UNITY_ANDROID
        // try to remove downloaded asset packs from the device and load textures again (Android only)
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            AndroidAssetPacks.RemoveAssetPack(assetPackNames[i]);
        }
        Debug.Log("Asset packs removed");
        for (int i = 0; i < TotalNumberOfGroups; ++i)
        {
            opHandle = Addressables.LoadAssetAsync<Texture2D>(Path.Combine(kSingleTestAssetFolder, TextureName(i)).Replace('\\', '/'));
            yield return opHandle;

            Assert.AreEqual(AsyncOperationStatus.Succeeded, opHandle.Status);
            texture = opHandle.Result;
            Assert.IsTrue(texture.format.ToString().StartsWith(kExpectedTextureFormat), $"Texture {TextureName(i)} {texture.format}");
            Addressables.Release(opHandle);
            yield return null;
        }
#endif
    }
}
