using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene host for the pack-opening screen — the peer of <see cref="ShopScreenController"/>,
    /// and framed the same way: this type owns only what a scene adds — where Back goes, and what
    /// is on screen on arrival. The reveal itself is entirely <see cref="PackRevealPanel"/>.
    ///
    /// <para><b>This is a frame, not a finished screen.</b> It exists so pack opening has somewhere
    /// of its own to grow — the presentation is deliberately the store's, unstyled and unarranged,
    /// and is meant to be replaced rather than extended.</para>
    ///
    /// <para><b>The receipt arrives by static handoff</b>, the same shape as
    /// <see cref="ShopScreenController.ReturnScene"/> and <see cref="ShopRuntime"/>: a scene load
    /// takes no arguments, so the caller writes what it wants shown and then loads. Nothing here
    /// opens a pack — a receipt is server-authored and this screen only presents one.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// PackRevealScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PackRevealScreenController : MonoBehaviour
    {
        /// <summary>Where Done returns to, set by whoever opened the screen. The default keeps the
        /// button working when the scene is entered directly from the Editor.</summary>
        public static string ReturnScene = "MainMenu";

        /// <summary>The receipt to reveal, written by the caller before it loads this scene.</summary>
        public static PackOpenReceipt Pending;

        private PackRevealPanel _reveal;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[PackReveal] No rootVisualElement — is the UXML assigned?");
                return;
            }

            _reveal = new PackRevealPanel(root, Exit);

            if (Pending == null)
            {
                // Nothing to show and nothing to be done about it: the screen cannot open a pack.
                // Leave rather than sit on an empty stage, which reads as a pack that awarded zero cards.
                Debug.LogWarning("[PackReveal] Entered with no pending receipt — returning to " +
                                 $"'{ReturnScene}'. Set PackRevealScreenController.Pending before loading this scene.");
                Exit();
                return;
            }

            _reveal.Show(Pending);

            // Consumed. A receipt is one opening; leaving it set would replay the same cards the
            // next time anything lands here, including a return trip from the collection.
            Pending = null;
        }

        private void Exit() => Go(ReturnScene);

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the
        /// Editor). Mirrors <c>ShopScreenController.Go</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[PackReveal] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
