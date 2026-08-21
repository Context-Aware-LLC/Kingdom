using Emberfall.Net.Client.Shop;
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
    /// <see cref="ShopRuntime.Gateway"/> and <see cref="ShopRuntime.PackArt"/> survive a
    /// scene load untouched, so this screen binds the same transport the menu would have.</para>
    ///
    /// <para>The purchase flow itself stays entirely in <see cref="ShopPanelController"/>. This
    /// type owns only what a scene adds: where Back goes, opening on arrival, and — since the
    /// PackReveal scene landed — where a bought pack goes to be opened.</para>
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

            // PackArt stays an override seam (a test or a future streamed loader can set it); with
            // nobody overriding it the rails come from the art folders on disk. The gateway has no
            // such default on purpose — an unbound wallet must read as unavailable, never as zero.
            _shop = new ShopPanelController(
                root,
                ShopRuntime.Gateway,
                ShopRuntime.PackArt ?? new PackArtProvider(),
                Exit,
                RevealOnItsOwnScreen);
            _shop.Open();
        }

        private void Exit() => Go(ReturnScene);

        /// <summary>Send a freshly bought pack to the PackReveal scene rather than opening it over
        /// the store. Returns false without touching anything if that scene cannot be loaded, which
        /// hands the reveal back to the store's own overlay — a receipt is already-purchased cards,
        /// so the one unacceptable outcome is showing it nowhere.
        ///
        /// <para><c>Pending</c> is written only after the scene is known to be loadable. Setting it
        /// first and failing the load would leave a receipt armed for whatever lands on that scene
        /// next, replaying cards the player already saw.</para></summary>
        private static bool RevealOnItsOwnScreen(PackOpenReceipt receipt)
        {
            if (receipt == null) return false;

            const string revealScene = "PackReveal";
            if (!Application.CanStreamedLevelBeLoaded(revealScene))
            {
                Debug.LogError($"[Shop] Scene '{revealScene}' is not in the build list, so the pack " +
                               "is being opened over the store instead. Run Emberfall/Build/Sync Scene List.");
                return false;
            }

            // Come back to the store, not to whatever opened the store: the player is mid-shopping
            // and the wallet they just spent from is here.
            PackRevealScreenController.ReturnScene = "Shop";
            PackRevealScreenController.Pending = receipt;
            UnityEngine.SceneManagement.SceneManager.LoadScene(revealScene);
            return true;
        }

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
