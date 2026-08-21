using Emberfall.Net.Client.Shop;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Composition seam for the account transport delivered with the server wallet/purchase path.
    /// The store can render its locked catalog without one, but all purchase controls stay disabled.
    ///
    /// <para>Static because the store is a scene now: <see cref="ShopScreenController"/> binds
    /// whatever the menu already put here, and statics are what survive a scene load untouched.
    /// It lived inside ShopPanelController.cs until 2026-08-20 — a leftover from when the store
    /// was an overlay in the main menu and there was only one file to put it in.</para>
    /// </summary>
    public static class ShopRuntime
    {
        public static IShopGateway Gateway { get; set; }

        /// <summary>Override seam for packaging art. Left null in every ordinary path — the store
        /// defaults to <see cref="PackArtProvider"/>, which reads the art folders — so this exists
        /// for a test or a future streamed loader, not for wiring.</summary>
        public static IPackArtProvider PackArt { get; set; }
    }
}
