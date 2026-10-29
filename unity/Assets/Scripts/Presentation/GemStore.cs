using System;
using System.Collections.Generic;
using System.Linq;
using Kingdom.Net.Client.Shop;
using UnityEngine;
using UnityEngine.Purchasing;

namespace Kingdom.Presentation
{
    /// <summary>What the gem section should say, and whether its buttons work.</summary>
    public enum GemStoreState
    {
        /// <summary>Not an Android build: there is no store to buy from.</summary>
        Unsupported,
        Connecting,
        Ready,
        StoreDown,
        Pending,
        Granted,
        Retry,
        Rejected,
        Failed,
    }

    /// <summary>
    /// Google Play billing for gem bundles, through Unity IAP 5. Static because the store connection
    /// outlives the Shop scene: a purchase interrupted by leaving it still completes and is redeemed
    /// the next time the shop opens, or at the next launch, when Play redelivers it.
    ///
    /// <para><b>The order of a purchase is the whole design.</b> Play hands over a token; the server
    /// checks it with Google and grants the Gems (<see cref="IGemRedeemGateway"/>); only then is the
    /// purchase confirmed (consumed) with Play. Confirming first would let a purchase the server never
    /// granted disappear from the store, and the player would have paid for nothing. So every outcome
    /// other than <see cref="GemRedeemOutcome.Granted"/> leaves the purchase unconfirmed, and Play
    /// keeps redelivering it.</para>
    ///
    /// <para><b>The wallet never comes from the store.</b> The balance shown is the one the server
    /// returns with the grant.</para>
    /// </summary>
    public static class GemStore
    {
        private static StoreController _store;
        private static IGemRedeemGateway _gateway;
        private static bool _connected;
        private static bool _productsFetched;
        private static readonly List<PendingOrder> Unredeemed = new List<PendingOrder>();

        /// <summary>The state changed; the wallet is set only alongside <see cref="GemStoreState.Granted"/>.</summary>
        public static event Action<GemStoreState, WalletBalances?> Changed;

        public static GemStoreState State { get; private set; } = GemStoreState.Unsupported;

        public static bool IsSupported => Application.platform == RuntimePlatform.Android;

        public static bool CanBuy => _connected && _productsFetched && _gateway != null;

        /// <summary>Connects once per process; later calls only retry anything left unredeemed.</summary>
        public static async void EnsureStarted(IGemRedeemGateway gateway)
        {
            if (!IsSupported || gateway == null) return;
            _gateway = gateway;
            if (_store != null)
            {
                RetryUnredeemed();
                return;
            }

            _store = UnityIAPServices.StoreController();
            _store.OnStoreConnected += OnConnected;
            _store.OnStoreDisconnected += OnDisconnected;
            _store.OnProductsFetched += OnProductsFetched;
            _store.OnProductsFetchFailed += OnProductsFetchFailed;
            _store.OnPurchasePending += OnPurchasePending;
            _store.OnPurchaseConfirmed += _ => { };
            _store.OnPurchaseFailed += OnPurchaseFailed;
            _store.OnPurchaseDeferred += _ => Set(GemStoreState.Pending);
            // Redelivered purchases (paid, never confirmed) arrive through OnPurchasePending.
            _store.ProcessPendingOrdersOnPurchasesFetched(true);

            Set(GemStoreState.Connecting);
            try
            {
                await _store.Connect();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GemStore] connect failed: {ex.GetType().Name}: {ex.Message}");
                Set(GemStoreState.StoreDown);
            }
        }

        /// <summary>The store's own price string, in the player's currency, once products are fetched.</summary>
        public static string LocalizedPrice(string productKey)
        {
            if (!_productsFetched) return null;
            string price = _store?.GetProductById(productKey)?.metadata?.localizedPriceString;
            return string.IsNullOrEmpty(price) ? null : price;
        }

        /// <summary>Starts Play's purchase sheet. <paramref name="storeAccountId"/> is stamped into the
        /// purchase so the server can tell whose it is; without it there is nothing to buy with.</summary>
        public static bool Buy(string productKey, string storeAccountId)
        {
            if (!CanBuy || string.IsNullOrEmpty(storeAccountId)) return false;
            _store.GooglePlayStoreExtendedService?.SetObfuscatedAccountId(storeAccountId);
            _store.PurchaseProduct(productKey);
            return true;
        }

        private static void OnConnected()
        {
            _connected = true;
            var definitions = new List<ProductDefinition>();
            foreach (GemBundleOffer bundle in ShopCatalog.GemBundles)
                definitions.Add(new ProductDefinition(bundle.ProductKey, ProductType.Consumable));
            _store.FetchProducts(definitions);
        }

        private static void OnDisconnected(StoreConnectionFailureDescription failure)
        {
            _connected = false;
            Debug.LogWarning($"[GemStore] store disconnected: {failure?.message}");
            Set(GemStoreState.StoreDown);
        }

        private static void OnProductsFetched(List<Product> products)
        {
            _productsFetched = true;
            Set(GemStoreState.Ready);
            _store.FetchPurchases();
        }

        private static void OnProductsFetchFailed(ProductFetchFailed failure)
        {
            Debug.LogWarning($"[GemStore] product fetch failed: {failure?.FailureReason}");
            Set(GemStoreState.StoreDown);
        }

        private static void OnPurchasePending(PendingOrder order)
        {
            if (!Unredeemed.Contains(order)) Unredeemed.Add(order);
            Redeem(order);
        }

        private static void RetryUnredeemed()
        {
            foreach (PendingOrder order in Unredeemed.ToArray()) Redeem(order);
        }

        private static async void Redeem(PendingOrder order)
        {
            Product product = order.CartOrdered.Items().FirstOrDefault()?.Product;
            string productKey = product?.definition?.storeSpecificId;
            string token = order.Info.Google?.PurchaseToken ?? order.Info.TransactionID;
            if (_gateway == null || string.IsNullOrEmpty(productKey) || string.IsNullOrEmpty(token)) return;

            GemRedeemResult result;
            try
            {
                result = await _gateway.RedeemGemsAsync(productKey, token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GemStore] redeem failed: {ex.GetType().Name}: {ex.Message}");
                result = new GemRedeemResult(GemRedeemOutcome.Retry);
            }

            switch (result.Outcome)
            {
                case GemRedeemOutcome.Granted:
                    Unredeemed.Remove(order);
                    _store.ConfirmPurchase(order);
                    Set(GemStoreState.Granted, result.Wallet);
                    break;
                case GemRedeemOutcome.Pending:
                    Set(GemStoreState.Pending);
                    break;
                case GemRedeemOutcome.Rejected:
                    // Not confirmed, so not consumed: if the server is wrong, Play still holds the
                    // purchase and it can be redeemed once that is fixed.
                    Unredeemed.Remove(order);
                    Debug.LogWarning($"[GemStore] server rejected purchase of {productKey}");
                    Set(GemStoreState.Rejected);
                    break;
                default:
                    Set(GemStoreState.Retry);
                    break;
            }
        }

        private static void OnPurchaseFailed(FailedOrder order)
        {
            Debug.LogWarning($"[GemStore] purchase failed: {order.FailureReason} {order.Details}");
            Set(order.FailureReason == PurchaseFailureReason.UserCancelled ? GemStoreState.Ready : GemStoreState.Failed);
        }

        private static void Set(GemStoreState state, WalletBalances? wallet = null)
        {
            State = state;
            Changed?.Invoke(state, wallet);
        }
    }
}
