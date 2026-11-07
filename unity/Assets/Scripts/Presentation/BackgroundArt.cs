using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Shows bundled mobile art, then upgrades larger devices from the remote content cache.
    /// Handles are retained for the app session so a
    /// screen transition never invalidates a texture still painted by another screen/overlay.
    /// No network is needed to display sign-in or update art. Phones never request remote art.
    /// </summary>
    public static class BackgroundArt
    {
        private static readonly List<AsyncOperationHandle<Texture2D>> Handles = new();
        private static readonly Dictionary<string, Texture2D> EditorTextures = new();
        private static readonly Dictionary<Texture2D, Sprite> Sprites = new();
        private static BackgroundLoadSequence<Texture2D> _loads;
        private static int _generation;
        private static BackgroundTier? _tier;

        public static BackgroundTier Tier => _tier ??= DetectTier();

        private static BackgroundTier DetectTier()
        {
#if !UNITY_EDITOR
            int smallestWidthDp = 0;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var resources = activity.Call<AndroidJavaObject>("getResources");
                using var configuration = resources.Call<AndroidJavaObject>("getConfiguration");
                smallestWidthDp = configuration.Get<int>("smallestScreenWidthDp");
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[BackgroundArt] Device class unavailable; using phone art: {error.Message}");
            }
#endif
#if UNITY_EDITOR
            // Allows both tiers to be checked in Play Mode without changing shipped device logic.
            string preview = UnityEditor.EditorPrefs.GetString("Kingdom.BackgroundArt.PreviewTier", "DesktopTablet");
            var tier = preview == "Mobile" ? BackgroundTier.Mobile : BackgroundTier.DesktopTablet;
#else
            var tier = BackgroundVariantPolicy.Choose(Application.isMobilePlatform, smallestWidthDp,
                SystemInfo.deviceModel, SystemInfo.maxTextureSize);
#endif
            Debug.Log($"[BackgroundArt] {BackgroundVariantPolicy.Folder(tier)} "
                + $"{BackgroundVariantPolicy.Width(tier)}x{BackgroundVariantPolicy.Height(tier)}");
            return tier;
        }

        /// <summary>May call ready twice: offline fallback, then the larger cached/downloaded art.</summary>
        public static async void LoadTextureAsync(string key, Action<Texture2D> ready)
        {
            int generation = _generation;
            try
            {
#if UNITY_EDITOR
                ready(LoadEditorTexture(key));
                await Task.CompletedTask;
#else
                _loads ??= new BackgroundLoadSequence<Texture2D>(LoadAssetAsync, () => Time.realtimeSinceStartupAsDouble);
                await _loads.LoadAsync(key, Tier, texture =>
                {
                    if (generation == _generation) ready(texture);
                });
#endif
            }
            catch (Exception error) { Debug.LogException(error); }
        }

        public static void Prewarm(string key) => LoadTextureAsync(key, _ => { });

        public static void LoadSpriteAsync(string key, Action<Sprite> ready)
        {
            LoadTextureAsync(key, texture =>
            {
                if (texture == null) return;
                if (!Sprites.TryGetValue(texture, out Sprite sprite))
                {
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    Sprites.Add(texture, sprite);
                }
                ready(sprite);
            });
        }

        private static async Task<Texture2D> LoadAssetAsync(BackgroundTier tier, string key)
        {
            string address = BackgroundVariantPolicy.Address(tier, key);
            int generation = _generation;
            try
            {
                if (tier == BackgroundTier.DesktopTablet) await CacheRemoteBundlesAsync(address);
                if (generation != _generation) return null;
                var handle = Addressables.LoadAssetAsync<Texture2D>(address);
                Handles.Add(handle);
                try { return await handle.Task; }
                catch
                {
                    if (generation == _generation && Handles.Remove(handle) && handle.IsValid()) Addressables.Release(handle);
                    throw;
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[BackgroundArt] '{address}' unavailable; keeping the local fallback. {error.Message}");
                return null;
            }
        }

        private static async Task CacheRemoteBundlesAsync(string address)
        {
            // Other loaders still use WaitForCompletion for LOCAL sprites/materials. An active
            // remote Addressables operation would make those calls wait for the network too.
            // Fill Unity's standard bundle cache outside ResourceManager, then let Addressables
            // load from that cache. Hash, cache key and CRC match AssetBundleProvider exactly.
            var locations = Addressables.LoadResourceLocationsAsync(address, typeof(Texture2D));
            try
            {
                var roots = await locations.Task;
                if (roots.Count == 0) throw new IOException($"No catalog entry for {address}");
                var visited = new HashSet<IResourceLocation>();
                foreach (var root in roots) await CacheDependenciesAsync(root, visited);
            }
            finally { if (locations.IsValid()) Addressables.Release(locations); }
        }

        private static async Task CacheDependenciesAsync(IResourceLocation location, HashSet<IResourceLocation> visited)
        {
            if (!visited.Add(location)) return;
            if (location.HasDependencies)
                foreach (var dependency in location.Dependencies) await CacheDependenciesAsync(dependency, visited);
            if (!(location.Data is AssetBundleRequestOptions options)) return;
            string url = Addressables.ResourceManager.TransformInternalId(location);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var cached = new CachedAssetBundle(options.BundleName, Hash128.Parse(options.Hash));
            if (Caching.IsVersionCached(cached)) return;
            using var request = UnityWebRequestAssetBundle.GetAssetBundle(url, cached, options.Crc);
            request.timeout = 20;
            ((DownloadHandlerAssetBundle)request.downloadHandler).autoLoadAssetBundle = false;
            var completion = new TaskCompletionSource<bool>();
            var operation = request.SendWebRequest();
            operation.completed += _ => completion.TrySetResult(true);
            await completion.Task;
            if (request.result != UnityWebRequest.Result.Success)
                throw new IOException($"Background download failed: {request.error}");
        }

#if UNITY_EDITOR
        private static Texture2D LoadEditorTexture(string key)
        {
            if (EditorTextures.TryGetValue(key, out Texture2D texture)) return texture;
            string path = BackgroundVariantPolicy.AssetPath(Tier, key);
            texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) Debug.LogError($"[BackgroundArt] Missing export '{path}'. Run export_background_variants.py.");
            else EditorTextures.Add(key, texture);
            return texture;
        }
#endif

        public static void Bind(VisualElement element, string key)
        {
            if (element == null) return;
            // A download must not retain an entire screen after it has been discarded. Allow
            // pre-attach bindings too: controllers call BindTree before inserting cloned UXML.
            var weak = new WeakReference<VisualElement>(element);
            LoadTextureAsync(key, texture =>
            {
                if (texture != null && weak.TryGetTarget(out var target))
                    target.style.backgroundImage = Background.FromTexture2D(texture);
            });
        }

        /// <summary>Bind the authored screen and overlay classes after their UXML is cloned.</summary>
        public static void BindTree(VisualElement root)
        {
            if (root == null) return;
            BindElement(root);
            root.Query<VisualElement>().ForEach(BindElement);
        }

        private static void BindElement(VisualElement element)
        {
            if (element.ClassListContains("main-menu")) Bind(element, "MainMenu_Backdrop");
            else if (element.ClassListContains("account-create-screen")) Bind(element, "Login_Backdrop");
            else if (element.ClassListContains("mm-modal--gate")) Bind(element, "UpdateGate_Backdrop");
            else if (element.ClassListContains("character-roster")) Bind(element, "CharacterRoster_Backdrop");
            else if (element.ClassListContains("collection") || element.name == "shop-overlay") Bind(element, "CollectionPlate_Vault");
            else if (element.ClassListContains("pack-vault-screen") || element.ClassListContains("reveal-overlay"))
                Bind(element, "PackVault_AncientCache_Backdrop");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _generation++;
            foreach (Sprite sprite in Sprites.Values)
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
            foreach (var handle in Handles)
                if (handle.IsValid()) Addressables.Release(handle);
            Sprites.Clear();
            Handles.Clear();
            EditorTextures.Clear();
            _loads = null;
            _tier = null;
        }
    }
}
