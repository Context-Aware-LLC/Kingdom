using Kingdom.Net.Client.Shop;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Optional override of the shop's authenticated HTTP gateway. With no override,
    /// <see cref="ShopScreenController"/> composes <see cref="HttpShopGateway"/> itself.
    ///
    /// <para>Static because a test or alternate host can replace the transport across a scene
    /// load; ordinary production flow leaves this null.</para>
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
