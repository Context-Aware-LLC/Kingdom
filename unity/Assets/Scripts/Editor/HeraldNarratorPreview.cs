using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    /// <summary>Review the shipping tutorial and narrator without resetting onboarding markers,
    /// replacing the open scene, or connecting a preview account to the server.</summary>
    [InitializeOnLoad]
    public static class HeraldNarratorPreview
    {
        private static GameObject _host;
        private static FirstRunTidbit _tidbit;

        static HeraldNarratorPreview()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) Close();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Close;
        }

        [MenuItem("Kingdom/Preview/Herald Tutorial")]
        public static void Show()
        {
            if (!Application.isPlaying) return;
            Close();

            var host = new GameObject("~HeraldTutorialPreview");
            _host = host;
            var document = host.AddComponent<UIDocument>();
            document.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(
                "Assets/UI/Common/KingdomPanelSettings.asset");
            document.sortingOrder = 50;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/UI/MainMenu/MainMenuScreen.uxml");

            VisualElement root = document.rootVisualElement.Q("root");
            root.RemoveFromClassList("mm-root--hidden");
            root.RemoveFromClassList("mm-root--entering");
            // Keep the real screen beneath the preview; use its actual tutorial markup/styles.
            foreach (VisualElement child in root.Children())
                if (child.name != "tidbit-overlay") child.style.display = DisplayStyle.None;

            _tidbit = new FirstRunTidbit(root);
            Tidbit sample = TidbitCatalog.For(TidbitTrigger.MainMenuArrival)[0];
            // Null IDs are intentionally never read/written as FirstRunMarkers.
            var preview = new Tidbit(null, sample.Trigger, sample.TitleKey, sample.BodyKey, sample.ArtKey);
            _tidbit.Show(new[] { preview }, Close);
            root.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (_host == host) Close();
            });
        }

        [MenuItem("Kingdom/Preview/Herald Tutorial", true)]
        private static bool CanShow() => Application.isPlaying;

        private static void Close()
        {
            FirstRunTidbit tidbit = _tidbit;
            GameObject host = _host;
            _tidbit = null;
            _host = null;
            tidbit?.Cancel();
            if (host == null) return;
            host.SetActive(false);
            if (Application.isPlaying) Object.Destroy(host);
            else Object.DestroyImmediate(host);
        }
    }
}
