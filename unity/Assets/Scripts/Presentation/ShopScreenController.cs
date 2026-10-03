using Emberfall.Net.Client.Shop;
using Emberfall.Session;
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
    /// <para>The default gateway is composed here from the authenticated account transport;
    /// <see cref="ShopRuntime.Gateway"/> and <see cref="ShopRuntime.PackArt"/> remain override seams.</para>
    ///
    /// <para>The purchase flow itself stays entirely in <see cref="ShopPanelController"/>. This
    /// type owns only what a scene adds: where Back goes and opening on arrival. Bought packs
    /// remain unopened on the Vault shelf until the player chooses one.</para>
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
        public static string ReturnScene = ScreenNav.MainMenu;

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

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();
            FriendsDrawerController.Mount();

            // PackArt stays an override seam (a test or a future streamed loader can set it); with
            // nobody overriding it the rails come from the art folders on disk. The authenticated
            // shop gateway is the default; /collection remains a read-only wallet fallback.
            IShopGateway gateway = ShopRuntime.Gateway ?? new HttpShopGateway(
                CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(15),
                AuthGateway.TokenForConnectAsync);
            _shop = new ShopPanelController(
                root,
                gateway,
                ShopRuntime.PackArt ?? new PackArtProvider(),
                Exit,
                OpenVault,
                RefreshCollectionWallet);
            _shop.Open();

            // First look (#403). The catalog is already drawn by the constructor, so the card sits
            // over a store the player can see, not over a fetch.
            new FirstRunTidbit(root).Show(TidbitTrigger.ShopArrival);
        }

        private async void RefreshCollectionWallet()
        {
            ShopPanelController shop = _shop;
            if (shop == null || !AuthGateway.IsSignedIn) return;

            try
            {
                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (string.IsNullOrEmpty(accessToken)) return;
                var snapshot = await CollectionGateway.FetchAsync(accessToken);
                if (isActiveAndEnabled && ReferenceEquals(_shop, shop) && !shop.HasShopSnapshot)
                    shop.RenderCollectionWallet(snapshot);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Shop] wallet refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Exit() => ScreenNav.Go(ReturnScene);

        private void OpenVault()
        {
            PackVaultController.ReturnScene = ScreenNav.Shop;
            PackVaultController.Pending = null;
            PackVaultController.Mode = PackVaultMode.Vault;
            ScreenNav.Go(ScreenNav.PackVault);
        }

    }
}
