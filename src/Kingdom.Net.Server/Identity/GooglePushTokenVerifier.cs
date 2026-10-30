using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kingdom.Net.Server.Identity
{
    public enum PushTokenVerdict
    {
        Accepted,

        /// <summary>Not a token Pub/Sub minted for this subscription. Answer 401.</summary>
        Rejected,

        /// <summary>Google's keys could not be fetched. Answer 503 so Pub/Sub redelivers: a refund
        /// must not be dropped because a key fetch failed.</summary>
        Unavailable,
    }

    /// <summary>Checks the bearer a Pub/Sub push subscription sends. A seam so the endpoint's tests
    /// need no Google keys.</summary>
    public interface IPushTokenVerifier
    {
        Task<PushTokenVerdict> VerifyAsync(string rawToken, CancellationToken ct = default);
    }

    /// <summary>
    /// Verifies the OIDC token a Pub/Sub push subscription signs with its authentication service
    /// account. Three things make a token ours: Google signed it (<c>oauth2/v3/certs</c>), its
    /// <c>aud</c> is the audience the subscription was created with, and its <c>email</c> is that
    /// service account, verified. Any Google account can mint a Google-signed token for any audience,
    /// so the email check is the one that matters most.
    ///
    /// <para>Fail closed, the same as <see cref="SupabaseTokenVerifier"/>: RS256 only, keys cached
    /// for five minutes, one refresh on an unknown kid.</para>
    /// </summary>
    public sealed class GooglePushTokenVerifier : IPushTokenVerifier
    {
        public const string AudienceEnvVar = "KINGDOM_PLAY_PUSH_AUDIENCE";
        public const string ServiceAccountEnvVar = "KINGDOM_PLAY_PUSH_ACCOUNT";

        private const string JwksUri = "https://www.googleapis.com/oauth2/v3/certs";
        private static readonly string[] Issuers = { "https://accounts.google.com", "accounts.google.com" };

        private readonly string _audience;
        private readonly string _serviceAccount;
        private readonly HttpClient _http;
        private static readonly TimeSpan MinRefreshGap = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _cacheMaxAge = TimeSpan.FromMinutes(5);
        private readonly SemaphoreSlim _lock = new(1, 1);
        private IList<SecurityKey>? _cachedKeys;
        private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
        private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

        public GooglePushTokenVerifier(string audience, string serviceAccount, HttpClient? http = null)
        {
            if (string.IsNullOrWhiteSpace(audience)) throw new ArgumentException("An audience is required.", nameof(audience));
            if (string.IsNullOrWhiteSpace(serviceAccount))
                throw new ArgumentException("A service account is required.", nameof(serviceAccount));
            _audience = audience;
            _serviceAccount = serviceAccount;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>Null unless both <see cref="AudienceEnvVar"/> and <see cref="ServiceAccountEnvVar"/>
        /// are set, which keeps <c>/store/play/notifications</c> off.</summary>
        public static GooglePushTokenVerifier? FromEnvironment()
        {
            string? audience = KingdomEnv.Get(AudienceEnvVar);
            string? account = KingdomEnv.Get(ServiceAccountEnvVar);
            if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(account)) return null;
            return new GooglePushTokenVerifier(audience!.Trim(), account!.Trim());
        }

        public async Task<PushTokenVerdict> VerifyAsync(string rawToken, CancellationToken ct = default)
        {
            var handler = new JsonWebTokenHandler();
            JsonWebToken jwt;
            try { jwt = handler.ReadJsonWebToken(rawToken); }
            catch { return PushTokenVerdict.Rejected; }
            if (!string.Equals(jwt.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
                return PushTokenVerdict.Rejected;

            IList<SecurityKey> keys;
            try
            {
                keys = await GetKeysAsync(refresh: false, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(jwt.Kid) && !ContainsKid(keys, jwt.Kid))
                    keys = await GetKeysAsync(refresh: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return PushTokenVerdict.Unavailable; }

            var tvp = new TokenValidationParameters
            {
                ValidIssuers = Issuers,
                ValidAudience = _audience,
                IssuerSigningKeys = keys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true,
                AlgorithmValidator = (algorithm, _, _, _) =>
                    string.Equals(algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal),
            };

            TokenValidationResult result;
            try { result = await handler.ValidateTokenAsync(rawToken, tvp).ConfigureAwait(false); }
            catch { return PushTokenVerdict.Rejected; }
            if (!result.IsValid) return PushTokenVerdict.Rejected;

            string email = result.Claims.TryGetValue("email", out object? e) ? e?.ToString() ?? "" : "";
            bool verified = result.Claims.TryGetValue("email_verified", out object? v) &&
                            (v is bool b ? b : string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
            return verified && string.Equals(email, _serviceAccount, StringComparison.OrdinalIgnoreCase)
                ? PushTokenVerdict.Accepted
                : PushTokenVerdict.Rejected;
        }

        private async Task<IList<SecurityKey>> GetKeysAsync(bool refresh, CancellationToken ct)
        {
            if (Fresh(refresh)) return _cachedKeys!;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Fresh(refresh)) return _cachedKeys!;
                string json = await _http.GetStringAsync(JwksUri, ct).ConfigureAwait(false);
                _cachedKeys = new JsonWebKeySet(json).GetSigningKeys();
                _fetchedAt = DateTimeOffset.UtcNow;
                _cacheExpiry = _fetchedAt + _cacheMaxAge;
                return _cachedKeys;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>The route is unauthenticated until this check passes, so a stream of made-up kids
        /// must not become a stream of fetches: a forced refresh waits out <see cref="MinRefreshGap"/>.</summary>
        private bool Fresh(bool refresh)
        {
            if (_cachedKeys == null) return false;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return refresh ? now < _fetchedAt + MinRefreshGap : now < _cacheExpiry;
        }

        private static bool ContainsKid(IList<SecurityKey> keys, string kid)
        {
            foreach (SecurityKey k in keys)
                if (k.KeyId == kid) return true;
            return false;
        }
    }
}
