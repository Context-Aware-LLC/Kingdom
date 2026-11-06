using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Loads only the device's background tier. Handles are retained for the app session so a
    /// screen transition never invalidates a texture still painted by another screen/overlay.
    /// Both groups are local packed content; no network is needed to display sign-in or update art.
    /// </summary>
    public static class BackgroundArt
    {
        private static readonly Dictionary<string, AsyncOperationHandle<Texture2D>> Handles = new();
        private static readonly Dictionary<string, Texture2D> EditorTextures = new();
        private static readonly Dictionary<string, Sprite> Sprites = new();
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

        public static Texture2D LoadTexture(string key)
        {
#if UNITY_EDITOR
            return LoadEditorTexture(key);
#else
            var handle = Handle(key);
            var texture = handle.WaitForCompletion();
            return handle.Status == AsyncOperationStatus.Succeeded ? texture : null;
#endif
        }

        public static void LoadTextureAsync(string key, Action<Texture2D> ready)
        {
#if UNITY_EDITOR
            ready(LoadEditorTexture(key));
#else
            var handle = Handle(key);
            if (handle.IsDone)
                ready(handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null);
            else
                handle.Completed += completed => ready(completed.Status == AsyncOperationStatus.Succeeded ? completed.Result : null);
#endif
        }

        public static Sprite LoadSprite(string key)
        {
            if (Sprites.TryGetValue(key, out Sprite sprite)) return sprite;
            Texture2D texture = LoadTexture(key);
            if (texture == null) return null;
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            Sprites.Add(key, sprite);
            return sprite;
        }

        private static AsyncOperationHandle<Texture2D> Handle(string key)
        {
            string address = BackgroundVariantPolicy.Address(Tier, key);
            if (Handles.TryGetValue(address, out var handle)) return handle;
            handle = Addressables.LoadAssetAsync<Texture2D>(address);
            Handles.Add(address, handle);
            handle.Completed += completed =>
            {
                if (completed.Status != AsyncOperationStatus.Succeeded)
                    Debug.LogError($"[BackgroundArt] Could not load '{address}': {completed.OperationException}");
            };
            return handle;
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
            LoadTextureAsync(key, texture =>
            {
                if (texture != null) element.style.backgroundImage = Background.FromTexture2D(texture);
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
            foreach (Sprite sprite in Sprites.Values)
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
            foreach (var handle in Handles.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            Sprites.Clear();
            Handles.Clear();
            EditorTextures.Clear();
            _tier = null;
        }
    }
}
