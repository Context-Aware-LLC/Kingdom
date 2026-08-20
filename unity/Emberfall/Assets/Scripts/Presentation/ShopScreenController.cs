using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene host for the store — the peer of <see cref="CollectionController"/> and
    /// <see cref="CharacterRosterController"/>, and for the same reason: the store is a
    /// destination, not a modal. It rendered as an overlay inside MainMenuScreen.uxml until
    /// 2026-08-20, which put a third of the menu's markup in a file about the menu.
    ///
    /// <para>The split costs nothing at the seams because the store's composition is static:
    /// <see cref="ShopRuntime.Gateway"/> and <see cref="ShopRuntime.PackagingArt"/> survive a
    /// scene load untouched, so this screen binds the same transport the menu would have.</para>
    ///
    /// <para>The purchase flow itself stays entirely in <see cref="ShopPanelController"/>. This
    /// type owns only what a scene adds: where Back goes, and opening on arrival.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// ShopScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ShopScreenController : MonoBehaviour
    {
        /// <summary>Where Back returns to, set by whoever opened the store — the same
        /// caller-writes-its-own-way-home shape <see cref="CollectionController.ReturnScene"/>
        /// uses. The default keeps Back working if the scene is entered directly from the
        /// Editor.</summary>
        public static string ReturnScene = "MainMenu";

        private ShopPanelController _shop;

        private void OnEnable()
        {
            // Unlike the menu, this UXML's top-level element needs no unwrapping: nothing here is
            // gated on a hidden root class, so the synthetic per-panel wrapper is a fine query
            // root — every element the panel controller wants is a descendant of it either way.
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[Shop] No rootVisualElement — is the UXML assigned?");
                return;
            }

            // PackagingArt stays an override seam (a test or a future streamed loader can set it);
            // with nobody overriding it the packaging slots resolve through the same sprite
            // pipeline as the rest of the meta screens. The gateway has no such default on
            // purpose — an unbound wallet must read as unavailable, never as zero.
            _shop = new ShopPanelController(
                root,
                ShopRuntime.Gateway,
                ShopRuntime.PackagingArt ?? new UiSpriteBoosterArtProvider(),
                Exit);
            _shop.Open();
        }

        private void Exit() => Go(ReturnScene);

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the
        /// Editor). Mirrors <c>MainMenuController.Go</c> — see there for why
        /// <c>CanStreamedLevelBeLoaded</c> over <c>GetBuildIndexByScenePath</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[Shop] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
