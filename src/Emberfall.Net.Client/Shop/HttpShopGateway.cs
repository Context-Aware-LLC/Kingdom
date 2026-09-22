using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;

namespace Emberfall.Net.Client.Shop
{
    /// <summary>A declined purchase is safe to try again; an uncertain response is not.</summary>
    public sealed class ShopPurchaseException : Exception
    {
        public bool OutcomeUnknown { get; }

        public ShopPurchaseException(string message, bool outcomeUnknown) : base(message)
            => OutcomeUnknown = outcomeUnknown;
    }

    /// <summary>Authenticated shop transport. The server owns prices, wallet and purchase outcomes.</summary>
    public sealed class HttpShopGateway : IShopGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;
        private readonly Func<Task<string>> _token;

        public HttpShopGateway(string baseUrl, IAuthHttp http, Func<Task<string>> token)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _token = token ?? throw new ArgumentNullException(nameof(token));
        }

        public async Task<ShopSnapshot> GetShopAsync(CancellationToken cancellationToken = default)
        {
            string token = await _token().ConfigureAwait(false);
            if (_baseUrl.Length == 0 || string.IsNullOrEmpty(token))
                throw new InvalidOperationException("The shop is not signed in or configured.");

            AuthHttpResponse response = await _http.GetAsync(_baseUrl + "/shop", Headers(token),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess || response.IsTransportFailure ||
                !TrySnapshot(response.Body, out ShopSnapshot? snapshot))
                throw new InvalidOperationException("The shop catalog is unavailable.");
            return snapshot!;
        }

        public async Task<PackPurchaseResult> PurchaseAsync(
            PackPurchaseRequest request, CancellationToken cancellationToken = default)
        {
            string token = await _token().ConfigureAwait(false);
            if (_baseUrl.Length == 0 || string.IsNullOrEmpty(token))
                throw new ShopPurchaseException("Sign in before buying a pack.", false);

            string body = AuthJson.Object(
                AuthJson.Field("packId", request.PackId),
                AuthJson.Field("currency", request.Currency.ToString().ToLowerInvariant()),
                AuthJson.Field("requestId", request.RequestId));
            AuthHttpResponse response = await _http.PostJsonAsync(_baseUrl + "/shop/purchase", body,
                Headers(token), cancellationToken).ConfigureAwait(false);

            // Retrying this exact body is safe: the server derives one account-scoped ledger key
            // from requestId. Never generate a new requestId on a transport retry.
            if (response.IsTransportFailure && !cancellationToken.IsCancellationRequested)
                response = await _http.PostJsonAsync(_baseUrl + "/shop/purchase", body,
                    Headers(token), cancellationToken).ConfigureAwait(false);

            if (response.IsTransportFailure)
                throw new ShopPurchaseException("Purchase status is unknown.", true);
            if (response.StatusCode == 409)
            {
                bool insufficient = AuthJson.TryReadTopLevel(response.Body, out var declined) &&
                    AuthJson.TryString(declined, "status", out string reason) &&
                    reason.Equals("insufficientfunds", StringComparison.OrdinalIgnoreCase);
                throw new ShopPurchaseException(insufficient
                    ? "Not enough currency for this pack."
                    : "Purchase status uncertain. Check Card Vault.", !insufficient);
            }
            if (response.StatusCode == 401 || response.StatusCode == 400 ||
                response.StatusCode == 503)
                throw new ShopPurchaseException("The server declined the purchase (" +
                    response.StatusCode.ToString(CultureInfo.InvariantCulture) + ").", false);
            if (!response.IsSuccess)
                throw new ShopPurchaseException("Purchase status is unknown.", true);

            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status) ||
                !(status.Equals("purchased", StringComparison.OrdinalIgnoreCase) ||
                  status.Equals("replayed", StringComparison.OrdinalIgnoreCase)) ||
                !AuthJson.TryString(fields, "packId", out string packId) ||
                !string.Equals(packId, request.PackId, StringComparison.Ordinal) ||
                !AuthJson.TryInt64(fields, "unopenedCount", out long unopened) || unopened < 0 ||
                !TrySnapshot(response.Body, out ShopSnapshot? snapshot))
                throw new ShopPurchaseException("Purchase succeeded but the receipt was unreadable.", true);

            return new PackPurchaseResult(snapshot!, packId, unopened);
        }

        private static List<KeyValuePair<string, string>> Headers(string token)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + token),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

        private static bool TrySnapshot(string body, out ShopSnapshot? snapshot)
        {
            snapshot = null;
            if (!AuthJson.TryReadTopLevel(body, out var fields) ||
                !AuthJson.TryReadNestedObject(body, "wallet", out var wallet) ||
                !AuthJson.TryInt64(wallet, "gold", out long gold) || gold < 0 ||
                !AuthJson.TryInt64(wallet, "gems", out long gems) ||
                !AuthJson.TryInt64(wallet, "shards", out long shards) || shards < 0 ||
                !AuthJson.TryInt64(fields, "vaultCount", out long vaultCount) || vaultCount < 0 ||
                !AuthJson.TryReadObjectArray(body, "packs", out var rows)) return false;

            var packs = new List<PackOffer>(rows.Count);
            foreach (var row in rows)
            {
                if (!AuthJson.TryString(row, "packId", out string packId) ||
                    !AuthJson.TryString(row, "displayName", out string displayName) ||
                    !AuthJson.TryInt64(row, "goldPrice", out long goldPrice) ||
                    !AuthJson.TryInt64(row, "gemPrice", out long gemPrice) ||
                    goldPrice < 0 || goldPrice > int.MaxValue ||
                    gemPrice < 0 || gemPrice > int.MaxValue) return false;
                packs.Add(new PackOffer(packId, displayName, (int)goldPrice, (int)gemPrice));
            }
            snapshot = new ShopSnapshot(new WalletBalances(gold, gems, shards), packs,
                ShopCatalog.GemBundles, vaultCount);
            return true;
        }
    }
}
