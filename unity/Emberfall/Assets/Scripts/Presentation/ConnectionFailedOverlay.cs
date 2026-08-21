using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// #232: the only way out of a failed networked boot besides killing the app. Before this,
    /// a rejected deck, a full server, and a dead network drop all left the player stranded on
    /// whatever loading/versus overlay was up, with no retry and no back button.
    ///
    /// Built by <see cref="NetworkedGameBootstrap.BootFailed"/> — no scene wiring, same
    /// Resources-loaded UXML/PanelSettings pattern <see cref="MatchSettingsController"/> uses.
    /// Retry reloads this scene (a fresh connect attempt, same shape as
    /// MatchSettingsController.ReloadThisScene); Back to menu loads MainMenu, the same scene
    /// name MatchEndMenuReturn already returns to after an ordinary match end.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ConnectionFailedOverlay : MonoBehaviour
    {
        private const string MainMenuSceneName = "MainMenu";

        public void Init(string reason)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[ConnectionFailed] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }

            var reasonLabel = root.Q<Label>("reason");
            if (reasonLabel != null) reasonLabel.text = reason;

            var retry = root.Q<Button>("btn-retry");
            retry?.RegisterCallback<ClickEvent>(_ => ReloadThisScene());

            var back = root.Q<Button>("btn-back");
            back?.RegisterCallback<ClickEvent>(_ => GoToMenu());
        }

        private static void ReloadThisScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.name);
        }

        private static void GoToMenu()
        {
            if (!Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                Debug.LogError($"[ConnectionFailed] Scene '{MainMenuSceneName}' is not in the build list; cannot return to menu.");
                return;
            }
            SceneManager.LoadScene(MainMenuSceneName);
        }
    }
}
