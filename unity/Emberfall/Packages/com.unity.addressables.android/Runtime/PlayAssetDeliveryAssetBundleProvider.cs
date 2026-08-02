using System;
using System.ComponentModel;
using System.IO;
using System.Collections.Generic;
using UnityEngine.Android;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.Util;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Exceptions;

namespace UnityEngine.AddressableAssets.Android
{
#if UNITY_ANDROID && !UNITY_EDITOR
    // this class is required to generate error when trying loading synchronously
    class PlayAssetDeliveryResource
    {
        PlayAssetDeliveryAssetBundleProvider m_PlayAssetDeliveryAssetBundleProvider;
        ProvideHandle m_ProvideHandle;
        const string kSyncMessage = "Play Asset Delivery provider does not support synchronous Addressable loading. Please do not use WaitForCompletion with Play Asset Delivery provider.";

        internal PlayAssetDeliveryResource(PlayAssetDeliveryAssetBundleProvider provider, ProvideHandle provideHandle)
        {
            m_PlayAssetDeliveryAssetBundleProvider = provider;
            m_ProvideHandle = provideHandle;
            m_ProvideHandle.SetWaitForCompletionCallback(WaitForCompletionHandler);
        }

        bool WaitForCompletionHandler()
        {
            Debug.LogError(kSyncMessage);
            m_PlayAssetDeliveryAssetBundleProvider.CompleteInterface(m_ProvideHandle);
            m_ProvideHandle.Complete<AssetBundleResource>(null, false, new RemoteProviderException(kSyncMessage));
            return true;
        }
    }
#endif

    /// <summary>
    /// Ensures that the asset pack containing the AssetBundle is installed/downloaded before attemping to load the bundle.
    /// </summary>
    [DisplayName("Play Asset Delivery Provider")]
    public class PlayAssetDeliveryAssetBundleProvider : AssetBundleProvider, IUpdateReceiver
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Dictionary<string, HashSet<ProvideHandle>> m_ProviderInterfaces = new Dictionary<string, HashSet<ProvideHandle>>();
        List<string> m_AssetPackQueue = new List<string>();

        /// <inheritdoc/>
        public override void Provide(ProvideHandle providerInterface)
        {
            LoadFromAssetPack(providerInterface);
        }

        void LoadFromAssetPack(ProvideHandle providerInterface)
        {
            if (!PlayAssetDeliveryRuntimeData.Instance.Initialized)
            {
                // this can happen only when trying to load first asset using Play Asset Delivery synchronously
                new PlayAssetDeliveryResource(this, providerInterface);
                return;
            }

            string bundleName = Path.GetFileNameWithoutExtension(providerInterface.Location.InternalId.Replace("\\", "/"));
            if (!PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack.ContainsKey(bundleName))
            {
                // Bundle is either assigned to the generated asset packs, or not assigned to any asset pack
                base.Provide(providerInterface);
                return;
            }

            var assetPackNameToDownloadPath = PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath;
            var assetPackName = PlayAssetDeliveryRuntimeData.Instance.BundleNameToAssetPack[bundleName].AssetPackName;
            // Bundle is assigned to install-time AddressablesAssetPack
            if (assetPackName == CustomAssetPackUtility.kAddressablesAssetPackName)
            {
                assetPackNameToDownloadPath.Add(CustomAssetPackUtility.kAddressablesAssetPackName, Application.streamingAssetsPath);
                base.Provide(providerInterface);
                return;
            }
            // Check that asset pack is downloaded, but not added to assetPackNameToDownloadPath
            if (!assetPackNameToDownloadPath.ContainsKey(assetPackName))
            {
                var assetPackPath = AndroidAssetPacks.GetAssetPackPath(assetPackName);
                if (!string.IsNullOrEmpty(assetPackPath))
                {
                    // Asset pack was located on device. Proceed with loading the bundle.
                    assetPackNameToDownloadPath.Add(assetPackName, assetPackPath);
                }
            }
            // Bundle is assigned to the previously downloaded asset pack
            if (assetPackNameToDownloadPath.ContainsKey(assetPackName))
            {
                if (Directory.Exists(assetPackNameToDownloadPath[assetPackName]))
                {
                    base.Provide(providerInterface);
                    return;
                }
                // Downloaded asset pack doesn't exist, most likely it was deleted
                assetPackNameToDownloadPath.Remove(assetPackName);
            }
            // Download the asset pack
            new PlayAssetDeliveryResource(this, providerInterface);
            DownloadRemoteAssetPack(providerInterface, assetPackName);
        }

        /// <inheritdoc/>
        public override void Release(IResourceLocation location, object asset)
        {
            base.Release(location, asset);
            m_ProviderInterfaces.Clear();
        }

#if UNITY_ADDRESSABLES_3_0_OR_NEWER
        public override IOperationCacheKey CreateCacheKeyForLocation(ResourceManager rm, IResourceLocation location, Type desiredType)
#else
        internal override IOperationCacheKey CreateCacheKeyForLocation(ResourceManager rm, IResourceLocation location, Type desiredType)
#endif
        {
            return new IdCacheKey(location.GetType(), location.InternalId);
        }

        void DownloadRemoteAssetPack(ProvideHandle providerInterface, string assetPackName)
        {
            if (!m_ProviderInterfaces.ContainsKey(assetPackName))
            {
                if (m_AssetPackQueue.Count == 0)
                {
                    Addressables.ResourceManager.AddUpdateReceiver(this);
                }
                m_ProviderInterfaces[assetPackName] = new HashSet<ProvideHandle>();
                m_AssetPackQueue.Add(assetPackName);
            }
            m_ProviderInterfaces[assetPackName].Add(providerInterface);
        }

        void FailDownloadAssetPack(string assetPackName, string message)
        {
            Debug.LogError(message);
            foreach (var pi in m_ProviderInterfaces[assetPackName])
            {
                pi.Complete<AssetBundleResource>(null, false, new RemoteProviderException(message));
            }
            m_ProviderInterfaces.Remove(assetPackName);
        }

        void CheckDownloadStatus(AndroidAssetPackInfo info)
        {
            var message = "";
            switch (info.status)
            {
                case AndroidAssetPackStatus.Failed:
                    message = $"Failed to retrieve the state of asset pack '{info.name}'.";
                    break;
                case AndroidAssetPackStatus.Unknown:
                    message = $"Asset pack '{info.name}' is unavailable for this application. This can occur if the app was not installed through Google Play.";
                    break;
                case AndroidAssetPackStatus.Canceled:
                    message = $"Cancelled asset pack download request '{info.name}'.";
                    break;
                case AndroidAssetPackStatus.WaitingForWifi:
#if UNITY_6000_5_OR_NEWER
                case AndroidAssetPackStatus.RequiresUserConfirmation:
                    AndroidAssetPacks.ShowConfirmationDialogAsync(OnConfirmationDialogComplete);
#else
                    AndroidAssetPacks.RequestToUseMobileDataAsync(OnRequestToUseMobileDataComplete);
#endif
                    break;
                case AndroidAssetPackStatus.Completed:
                    {
                        var assetPackPath = AndroidAssetPacks.GetAssetPackPath(info.name);
                        if (!string.IsNullOrEmpty(assetPackPath))
                        {
                            // Asset pack was located on device. Proceed with loading the bundle.
                            PlayAssetDeliveryRuntimeData.Instance.AssetPackNameToDownloadPath.Add(info.name, assetPackPath);
                            if (m_ProviderInterfaces.ContainsKey(info.name))
                            {
                                foreach (var pi in m_ProviderInterfaces[info.name])
                                {
                                    base.Provide(pi);
                                }
                                m_ProviderInterfaces.Remove(info.name);
                            }
                        }
                        else
                        {
                            message = $"Downloaded asset pack '{info.name}' but cannot locate it on device.";
                        }
                        break;
                    }
            }

            if (!string.IsNullOrEmpty(message))
            {
                FailDownloadAssetPack(info.name, message);
            }
        }

        /// <inheritdoc/>
        public void Update(float unscaledDeltaTime)
        {
            if (m_AssetPackQueue.Count == 0) {
                return;
            }

            // Note that most methods in the AndroidAssetPacks class are either direct wrappers of java APIs in Google's PlayCore plugin,
            // or depend on values that the PlayCore API returns. If the PlayCore plugin is missing, calling these methods will throw an InvalidOperationException exception.
            try
            {
                AndroidAssetPacks.DownloadAssetPackAsync(m_AssetPackQueue.ToArray(), CheckDownloadStatus);
            }
            catch (InvalidOperationException ioe)
            {
                Debug.LogError(ioe);
                foreach (var assetPackName in m_AssetPackQueue)
                {
                    var message = $"Cannot retrieve state for asset pack '{assetPackName}'. This might be because PlayCore Plugin is not installed: {ioe.Message}";
                    FailDownloadAssetPack(assetPackName, message);
                }
            }
            m_AssetPackQueue.Clear();
            Addressables.ResourceManager.RemoveUpdateReciever(this);
        }

#if UNITY_6000_5_OR_NEWER
        void OnConfirmationDialogComplete(AndroidAssetPackConfirmationDialogResult result)
        {
            if (result.consentGiven)
                return;
            var message = "Consent to install asset pack(s) was not given.";
            FailAllInterfaces(message);
        }
#else
        void OnRequestToUseMobileDataComplete(AndroidAssetPackUseMobileDataRequestResult result)
        {
            if (result.allowed)
                return;
            var message = "Request to use mobile data was denied.";
            FailAllInterfaces(message);
        }
#endif

        void FailAllInterfaces(string message)
        {
            Debug.LogError(message);
            foreach (var p in m_ProviderInterfaces)
            {
                foreach (var pi in p.Value)
                {
                    pi.Complete<AssetBundleResource>(null, false, new RemoteProviderException(message));
                }
            }
            m_ProviderInterfaces.Clear();
        }

        internal void CompleteInterface(ProvideHandle handle)
        {
            foreach (var p in m_ProviderInterfaces)
            {
                if (p.Value.Remove(handle))
                {
                    if (p.Value.Count == 0)
                    {
                        m_ProviderInterfaces.Remove(p.Key);
                    }
                    return;
                }
            }
        }
#else
        /// <inheritdoc/>
        public void Update(float unscaledDeltaTime)
        {
        }
#endif
    }
}
