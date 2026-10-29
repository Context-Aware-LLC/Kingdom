using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// The store could not be asked: Google did not answer, or refused OUR credential. Never a verdict
    /// on the receipt — the endpoint answers 503 and the client keeps the purchase pending, because
    /// telling an honest player their real receipt is invalid is the failure this type exists to
    /// prevent.
    /// </summary>
    public sealed class StoreUnavailableException : Exception
    {
        public StoreUnavailableException(string message) : base(message) { }
    }

    /// <summary>Tells the store a granted purchase was delivered. Separate from validation because
    /// it runs only AFTER the ledger write: acknowledging first and then failing to grant would leave
    /// a paid purchase the store will never refund.</summary>
    public interface IStorePurchaseAcknowledger
    {
        Task AcknowledgeAsync(StoreReceipt receipt, CancellationToken ct = default);
    }

    /// <summary>
    /// Google Play's adapter behind <see cref="IStoreReceiptValidator"/> (EF-080d). The receipt
    /// payload is <c>{"productId","purchaseToken"}</c> exactly as the Play Billing library handed them
    /// to the client; everything the economy trusts comes back from the Play Developer API, never from
    /// that payload.
    ///
    /// <para><b>The player binding is <c>obfuscatedExternalAccountId</c>.</b> The client sets it to
    /// the server's own <see cref="Account.PlayerId"/> (read from <c>GET /shop</c>) before it buys, so
    /// Google stores it inside the purchase. A token lifted from one player's device therefore names
    /// that player, and <see cref="GemGrantService"/> rejects it from anyone else's bearer.</para>
    ///
    /// <para><b>Play product ids ARE the product keys</b> (<c>gems.tier.200</c> …): the Play Console
    /// one-time products were created under those ids, so there is no mapping table to drift.</para>
    /// </summary>
    public sealed class PlayStoreReceiptValidator : IStoreReceiptValidator, IStorePurchaseAcknowledger
    {
        public const string Provider = "play";

        /// <summary>The service account JSON key: inline JSON, or a path to the file.</summary>
        public const string ServiceAccountEnvVar = "KINGDOM_PLAY_SERVICE_ACCOUNT";

        /// <summary>The application id purchases are checked against. Defaults to the shipped one.</summary>
        public const string PackageNameEnvVar = "KINGDOM_PLAY_PACKAGE_NAME";

        public const string DefaultPackageName = "com.contextaware.kobi";

        private const string Scope = "https://www.googleapis.com/auth/androidpublisher";
        private const string ApiRoot = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";

        private readonly HttpClient _http;
        private readonly string _packageName;
        private readonly Func<CancellationToken, Task<string>> _accessToken;

        public PlayStoreReceiptValidator(HttpClient http, string packageName,
                                         Func<CancellationToken, Task<string>> accessToken)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _packageName = string.IsNullOrWhiteSpace(packageName) ? DefaultPackageName : packageName;
            _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        }

        /// <summary>Null when no service account is configured, which turns <c>/shop/gems</c> off.</summary>
        public static PlayStoreReceiptValidator? FromEnvironment()
        {
            string? raw = KingdomEnv.Get(ServiceAccountEnvVar);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string json = raw!.TrimStart().StartsWith("{", StringComparison.Ordinal) ? raw : File.ReadAllText(raw);
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var credential = GoogleServiceAccountToken.FromJson(json, Scope, http);
            return new PlayStoreReceiptValidator(http,
                KingdomEnv.Get(PackageNameEnvVar) ?? DefaultPackageName, credential.GetAsync);
        }

        public async Task<StoreReceiptValidation> ValidateAsync(StoreReceipt receipt, CancellationToken ct = default)
        {
            if (!TryReadPayload(receipt, out string productId, out string token))
                return StoreReceiptValidation.Invalid("malformed_receipt");
            if (!GemBundleCatalog.TryGet(productId, out _))
                return StoreReceiptValidation.Invalid("unknown_product");

            using JsonDocument? purchase = await SendAsync(HttpMethod.Get, PurchaseUrl(productId, token), ct)
                .ConfigureAwait(false);
            if (purchase == null) return StoreReceiptValidation.Invalid("unknown_token");
            JsonElement root = purchase.RootElement;

            // 0 Purchased, 1 Canceled, 2 Pending (Play's deferred purchase).
            int state = TryInt(root, "purchaseState", out int s) ? s : -1;
            if (state == 1) return StoreReceiptValidation.Invalid("canceled");
            if (state != 0 && state != 2) return StoreReceiptValidation.Invalid("unknown_purchase_state");

            // Multi-quantity is a per-product Play Console switch, off for every gem tier. A purchase
            // of N would be granted once by GemGrantService, so refuse it loudly rather than short
            // the player N-1 bundles.
            if (TryInt(root, "quantity", out int quantity) && quantity > 1)
                return StoreReceiptValidation.Invalid("multi_quantity_unsupported");

            TryString(root, "obfuscatedExternalAccountId", out string playerId);
            // A pending purchase may not have an order id yet. Nothing is written for it, so any
            // stable id serves; the grant that follows payment carries the real one.
            string transactionId = TryString(root, "orderId", out string orderId)
                ? orderId
                : state == 2 ? "pending-" + Hash(token) : "";
            if (transactionId.Length == 0) return StoreReceiptValidation.Invalid("no_order_id");

            var validated = new ValidatedStorePurchase(Provider, transactionId, productId, playerId);
            return state == 0 ? StoreReceiptValidation.Valid(validated) : StoreReceiptValidation.Pending(validated);
        }

        public async Task AcknowledgeAsync(StoreReceipt receipt, CancellationToken ct = default)
        {
            if (!TryReadPayload(receipt, out string productId, out string token)) return;
            // Acknowledging an already-acknowledged purchase answers 400; the client's own consume
            // also acknowledges, so either one landing is enough.
            using JsonDocument? _ = await SendAsync(HttpMethod.Post, PurchaseUrl(productId, token) + ":acknowledge", ct)
                .ConfigureAwait(false);
        }

        private string PurchaseUrl(string productId, string token)
            => ApiRoot + Uri.EscapeDataString(_packageName) + "/purchases/products/"
               + Uri.EscapeDataString(productId) + "/tokens/" + Uri.EscapeDataString(token);

        /// <summary>The body on success; null when Google says the token is not one of ours (4xx other
        /// than an auth failure). Throws <see cref="StoreUnavailableException"/> for anything else.</summary>
        private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, CancellationToken ct)
        {
            try
            {
                string bearer = await _accessToken(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new StoreUnavailableException("Play refused the service account (" + (int)response.StatusCode + ").");
                int code = (int)response.StatusCode;
                if (code >= 400 && code < 500) return null;
                if (!response.IsSuccessStatusCode)
                    throw new StoreUnavailableException("Play answered " + code + ".");
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new StoreUnavailableException("Play could not be reached: " + ex.GetType().Name);
            }
        }

        private static bool TryReadPayload(StoreReceipt receipt, out string productId, out string token)
        {
            productId = "";
            token = "";
            if (receipt == null || string.IsNullOrEmpty(receipt.Payload)) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(receipt.Payload);
                return TryString(doc.RootElement, "productId", out productId)
                       && TryString(doc.RootElement, "purchaseToken", out token);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Hash(string value)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement e)
                || e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString() ?? "";
            return value.Length > 0;
        }

        private static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement e)
                   && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }
    }

    /// <summary>
    /// An OAuth access token for a Google service account, by the JWT-bearer grant. The assertion is
    /// signed by <see cref="JsonWebTokenHandler"/>, not by hand. Cached until five minutes before it
    /// expires; one refresh at a time.
    /// </summary>
    internal sealed class GoogleServiceAccountToken
    {
        private readonly string _email;
        private readonly RSA _key;
        private readonly string _tokenUri;
        private readonly string _scope;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private string? _token;
        private DateTimeOffset _refreshAt;

        private GoogleServiceAccountToken(string email, RSA key, string tokenUri, string scope, HttpClient http)
        {
            _email = email;
            _key = key;
            _tokenUri = tokenUri;
            _scope = scope;
            _http = http;
        }

        public static GoogleServiceAccountToken FromJson(string json, string scope, HttpClient http)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string email = root.GetProperty("client_email").GetString()
                           ?? throw new InvalidDataException("Service account has no client_email.");
            string pem = root.GetProperty("private_key").GetString()
                         ?? throw new InvalidDataException("Service account has no private_key.");
            string tokenUri = root.TryGetProperty("token_uri", out JsonElement uri) && uri.GetString() is string u
                ? u
                : "https://oauth2.googleapis.com/token";
            RSA rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new GoogleServiceAccountToken(email, rsa, tokenUri, scope, http);
        }

        public async Task<string> GetAsync(CancellationToken ct)
        {
            if (_token != null && DateTimeOffset.UtcNow < _refreshAt) return _token;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_token != null && DateTimeOffset.UtcNow < _refreshAt) return _token;

                DateTime now = DateTime.UtcNow;
                string assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = _email,
                    Audience = _tokenUri,
                    IssuedAt = now,
                    NotBefore = now,
                    Expires = now.AddMinutes(60),
                    Claims = new Dictionary<string, object> { ["scope"] = _scope },
                    SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key), SecurityAlgorithms.RsaSha256),
                });
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                });
                using HttpResponseMessage response = await _http.PostAsync(_tokenUri, form, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new StoreUnavailableException("Google refused the service account (" + (int)response.StatusCode + ").");
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                string token = body.RootElement.GetProperty("access_token").GetString()
                               ?? throw new StoreUnavailableException("Google returned no access token.");
                int lifetime = body.RootElement.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int secs)
                    ? secs
                    : 3600;
                _token = token;
                _refreshAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, lifetime - 300));
                return token;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
