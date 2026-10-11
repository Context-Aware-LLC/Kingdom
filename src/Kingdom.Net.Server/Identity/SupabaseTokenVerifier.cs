using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kingdom.Net.Server.Identity
{
    /// <summary>
    /// Verifies a Supabase-issued JWT against the project JWKS. Accepts only asymmetric algorithms
    /// (RS256/ES256 family); explicitly rejects alg:none and all symmetric algorithms (HS256 etc.)
    /// because a symmetric key can mint tokens — a game server must never hold one.
    ///
    /// <para><b>Fail closed.</b> An unreachable JWKS endpoint, unknown kid, or unparseable token is
    /// always a rejection. There is no guest fallback and no pass-through.</para>
    ///
    /// <para><b>JWKS cache.</b> Keys are fetched once and cached for <c>cacheMaxAge</c> (default
    /// 5 minutes). An unknown kid triggers a single refresh before returning <see cref="TokenVerificationFailure.UnknownKid"/>,
    /// so a key rotation does not cause a hard failure for the duration of a cache window.</para>
    ///
    /// <para><b>Configuration.</b> Read from <c>EMBERFALL_SUPABASE_URL</c>, mirroring how
    /// <c>EMBERFALL_TELEMETRY_DB</c> is read in <see cref="Telemetry.TelemetryDrain"/>.</para>
    /// </summary>
    public sealed class SupabaseTokenVerifier : IIdentityTokenVerifier, IDisposable
    {
        // Supabase uses RS256 today; ES256 is in-scope per the spec. Include the full asymmetric
        // families so a key rotation to a stronger variant does not break the gate.
        private static readonly HashSet<string> AsymmetricAlgorithms = new(StringComparer.Ordinal)
        {
            SecurityAlgorithms.RsaSha256,
            SecurityAlgorithms.RsaSha384,
            SecurityAlgorithms.RsaSha512,
            SecurityAlgorithms.EcdsaSha256,
            SecurityAlgorithms.EcdsaSha384,
            SecurityAlgorithms.EcdsaSha512,
            SecurityAlgorithms.RsaSsaPssSha256,
            SecurityAlgorithms.RsaSsaPssSha384,
            SecurityAlgorithms.RsaSsaPssSha512,
        };

        private readonly string _issuer;
        private readonly string _jwksUri;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly TimeSpan _cacheMaxAge;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private IList<SecurityKey>? _cachedKeys;
        private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

        public SupabaseTokenVerifier(
            string supabaseProjectUrl,
            HttpClient? httpClient = null,
            TimeSpan? cacheMaxAge = null)
        {
            string url = supabaseProjectUrl.TrimEnd('/');
            _issuer = $"{url}/auth/v1";
            _jwksUri = $"{url}/auth/v1/.well-known/jwks.json";
            _ownsHttp = httpClient == null;
            _http = httpClient ?? new HttpClient();
            _cacheMaxAge = cacheMaxAge ?? TimeSpan.FromMinutes(5);
        }

        /// <summary>Reads <c>EMBERFALL_SUPABASE_URL</c> from the environment, mirroring the
        /// <c>EMBERFALL_TELEMETRY_DB</c> pattern in <see cref="Telemetry.TelemetryDrain"/>.</summary>
        public static SupabaseTokenVerifier FromEnvironment()
        {
            string url = Environment.GetEnvironmentVariable("EMBERFALL_SUPABASE_URL")
                ?? throw new InvalidOperationException(
                    "EMBERFALL_SUPABASE_URL is not set. " +
                    "Set it to the Supabase project URL (e.g. https://<ref>.supabase.co).");
            return new SupabaseTokenVerifier(url);
        }

        public async Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
        {
            var handler = new JsonWebTokenHandler();

            // Parse the header first — cheap, no network, lets us reject alg:none and symmetric
            // algorithms before fetching the JWKS.
            JsonWebToken jwt;
            try { jwt = handler.ReadJsonWebToken(rawToken); }
            catch { return TokenVerificationResult.Fail(TokenVerificationFailure.Malformed); }

            string alg = jwt.Alg ?? string.Empty;
            if (string.IsNullOrEmpty(alg) ||
                alg.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                !AsymmetricAlgorithms.Contains(alg))
                return TokenVerificationResult.Fail(TokenVerificationFailure.AlgorithmRejected);

            IList<SecurityKey> keys;
            try { keys = await GetKeysAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { return TokenVerificationResult.Fail(TokenVerificationFailure.JwksUnavailable); }

            // If the header names a specific kid and none of the cached keys match, try one refresh
            // before giving up — this handles key rotation without a permanent failure window.
            string? kid = jwt.Kid;
            if (!string.IsNullOrEmpty(kid) && !ContainsKid(keys, kid))
            {
                try { keys = await RefreshKeysAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { return TokenVerificationResult.Fail(TokenVerificationFailure.JwksUnavailable); }

                if (!ContainsKid(keys, kid))
                    return TokenVerificationResult.Fail(TokenVerificationFailure.UnknownKid);
            }

            var tvp = new TokenValidationParameters
            {
                ValidIssuer = _issuer,
                ValidAudience = "authenticated",
                IssuerSigningKeys = keys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true,
                // Second layer: if the library somehow resolves a symmetric key (e.g. a future
                // code path we haven't anticipated), reject it here too. Defense in depth.
                AlgorithmValidator = (algorithm, _, _, _) => AsymmetricAlgorithms.Contains(algorithm),
            };

            TokenValidationResult result;
            try { result = await handler.ValidateTokenAsync(rawToken, tvp); }
            catch { return TokenVerificationResult.Fail(TokenVerificationFailure.Malformed); }

            if (!result.IsValid)
            {
                return result.Exception switch
                {
                    SecurityTokenExpiredException => TokenVerificationResult.Fail(TokenVerificationFailure.Expired),
                    SecurityTokenNotYetValidException => TokenVerificationResult.Fail(TokenVerificationFailure.NotYetValid),
                    SecurityTokenInvalidIssuerException => TokenVerificationResult.Fail(TokenVerificationFailure.WrongIssuer),
                    SecurityTokenInvalidAudienceException => TokenVerificationResult.Fail(TokenVerificationFailure.WrongAudience),
                    SecurityTokenSignatureKeyNotFoundException => TokenVerificationResult.Fail(TokenVerificationFailure.UnknownKid),
                    SecurityTokenInvalidSignatureException => TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid),
                    SecurityTokenInvalidAlgorithmException => TokenVerificationResult.Fail(TokenVerificationFailure.AlgorithmRejected),
                    _ => TokenVerificationResult.Fail(TokenVerificationFailure.Malformed),
                };
            }

            var claims = result.Claims;
            string subject = claims.TryGetValue("sub", out object? subObj) ? subObj?.ToString() ?? "" : "";
            string email = claims.TryGetValue("email", out object? emailObj) ? emailObj?.ToString() ?? "" : "";
            // Supabase uses email_confirmed_at (ISO timestamp) rather than the standard email_verified bool.
            bool emailVerified = claims.TryGetValue("email_confirmed_at", out object? ecaObj)
                && !string.IsNullOrEmpty(ecaObj?.ToString());

            return TokenVerificationResult.Ok(
                new VerifiedIdentity(subject, email, emailVerified, DisplayNameFrom(claims)));
        }

        /// <summary>
        /// <c>user_metadata.display_name</c> — where the client's sign-up writes the player's name
        /// (<c>SupabaseAuthBackend.DisplayNameKey</c>). Empty when absent or not a string.
        ///
        /// <para>Supabase stamps <c>user_metadata</c> into a token when it MINTS it, so a rename is
        /// seen here only after the player's next token refresh.</para>
        /// </summary>
        private static string DisplayNameFrom(IDictionary<string, object> claims)
        {
            if (!claims.TryGetValue("user_metadata", out object? metadata) || metadata is null) return "";

            // The handler hands a nested object back as a JsonElement; a claim that arrived as a
            // JSON string is parsed the same way.
            try
            {
                System.Text.Json.JsonElement element;
                if (metadata is System.Text.Json.JsonElement direct) element = direct;
                else
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(metadata.ToString() ?? "");
                    element = parsed.RootElement.Clone();
                }

                return element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       element.TryGetProperty("display_name", out var name) &&
                       name.ValueKind == System.Text.Json.JsonValueKind.String
                    ? name.GetString() ?? ""
                    : "";
            }
            catch (System.Text.Json.JsonException)
            {
                return "";
            }
        }

        private async Task<IList<SecurityKey>> GetKeysAsync(CancellationToken ct)
        {
            if (_cachedKeys != null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cachedKeys;
            return await RefreshKeysAsync(ct);
        }

        private async Task<IList<SecurityKey>> RefreshKeysAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                // Re-check inside the lock: another waiter may have already refreshed.
                if (_cachedKeys != null && DateTimeOffset.UtcNow < _cacheExpiry)
                    return _cachedKeys;

                string json = await _http.GetStringAsync(_jwksUri, ct);
                var keySet = new JsonWebKeySet(json);
                IList<SecurityKey> keys = keySet.GetSigningKeys();
                _cachedKeys = keys;
                _cacheExpiry = DateTimeOffset.UtcNow + _cacheMaxAge;
                return keys;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static bool ContainsKid(IList<SecurityKey> keys, string kid)
        {
            foreach (var k in keys)
                if (k.KeyId == kid) return true;
            return false;
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
            _lock.Dispose();
        }
    }
}
