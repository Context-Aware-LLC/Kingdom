using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    /// <summary>Review the shipping tutorial and narrator without resetting onboarding markers,
    /// replacing the open scene, or connecting a preview account to the server.</summary>
    public sealed class HeraldNarratorPreview : MonoBehaviour
    {
        private static HeraldNarratorPreview _instance;
        private FirstRunTidbit _tidbit;

        [MenuItem("Kingdom/Preview/Herald Tutorial")]
        public static void Show()
        {
            if (!Application.isPlaying) return;
            if (_instance != null) _instance.Close();

            var host = new GameObject("~HeraldTutorialPreview");
            _instance = host.AddComponent<HeraldNarratorPreview>();
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

            _instance._tidbit = new FirstRunTidbit(root);
            Tidbit sample = TidbitCatalog.For(TidbitTrigger.MainMenuArrival)[0];
            // Null IDs are intentionally never read/written as FirstRunMarkers.
            var preview = new Tidbit(null, sample.Trigger, sample.TitleKey, sample.BodyKey, sample.ArtKey);
            _instance._tidbit.Show(new[] { preview }, _instance.Close);
        }

        [MenuItem("Kingdom/Preview/Herald Tutorial", true)]
        private static bool CanShow() => Application.isPlaying;

        private void Close()
        {
            _tidbit?.Cancel();
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            _tidbit?.Cancel();
            if (_instance == this) _instance = null;
        }
    }
}
