using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Identity
{
    public enum SignInTokenStatus
    {
        Issued,

        /// <summary>Supabase has no user with that address.</summary>
        NoSuchUser,

        /// <summary>Supabase did not answer, or refused the key.</summary>
        Unavailable,
    }

    /// <param name="TokenHash">Redeemed by the client at Supabase's <c>verify</c>, once.</param>
    /// <param name="TokenType">The link type it was minted as, which <c>verify</c> needs.</param>
    /// <param name="UserId">The Supabase user the token signs in as.</param>
    public sealed record SignInToken(
        SignInTokenStatus Status, string TokenHash = "", string TokenType = "", string UserId = "");

    /// <summary>Supabase's admin API, for the one thing the game server asks of it.</summary>
    public interface ISupabaseAdmin
    {
        /// <summary>A single-use sign-in token for the existing user at <paramref name="email"/>.
        /// Never creates a user and never sends mail.</summary>
        Task<SignInToken> IssueSignInTokenAsync(string email, CancellationToken ct = default);
    }

    /// <summary>
    /// <c>POST /auth/v1/admin/generate_link</c> with the project's secret key (#425). Supabase mints
    /// the token and Supabase issues the session when the client redeems it, so the identity stays
    /// Supabase's and this server never holds a refresh token.
    ///
    /// <para><b>A <c>recovery</c> link, never a <c>magiclink</c>.</b> GoTrue turns a magic link for
    /// an unknown address into a sign-up, which would hand a restoring player a new, empty account;
    /// a recovery link for one answers 404. Generating a link sends no mail.</para>
    ///
    /// <para><b>The secret key is the most powerful credential this server holds</b>: it bypasses
    /// every row policy and can act as any user. It is read from the environment, sent only to the
    /// project's own URL, and never logged.</para>
    /// </summary>
    public sealed class SupabaseAdmin : ISupabaseAdmin
    {
        public const string SecretKeyEnvVar = "EMBERFALL_SUPABASE_SECRET_KEY";

        private const string LinkType = "recovery";

        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _secretKey;

        public SupabaseAdmin(HttpClient http, string projectUrl, string secretKey)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _endpoint = (projectUrl ?? "").TrimEnd('/') + "/auth/v1/admin/generate_link";
            _secretKey = secretKey ?? "";
        }

        /// <summary>Null unless both the project URL and the secret key are set, which turns the
        /// restore off.</summary>
        public static SupabaseAdmin? FromEnvironment(string? projectUrl)
        {
            string? key = Environment.GetEnvironmentVariable(SecretKeyEnvVar);
            if (string.IsNullOrWhiteSpace(projectUrl) || string.IsNullOrWhiteSpace(key)) return null;
            return new SupabaseAdmin(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, projectUrl!, key!.Trim());
        }

        public async Task<SignInToken> IssueSignInTokenAsync(string email, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return new SignInToken(SignInTokenStatus.NoSuchUser);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Headers.TryAddWithoutValidation("apikey", _secretKey);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _secretKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { type = LinkType, email }), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return new SignInToken(SignInTokenStatus.NoSuchUser);
                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine("[restore] Supabase generate_link answered {0}.", (int)response.StatusCode);
                    return new SignInToken(SignInTokenStatus.Unavailable);
                }

                // The user's fields with the link's beside them, at the top level.
                using JsonDocument body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                JsonElement root = body.RootElement;
                if (!TryString(root, "hashed_token", out string hash) || !TryString(root, "id", out string userId))
                    return new SignInToken(SignInTokenStatus.Unavailable);
                string type = TryString(root, "verification_type", out string named) ? named : LinkType;
                return new SignInToken(SignInTokenStatus.Issued, hash, type, userId);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("[restore] Supabase generate_link failed: {0}", ex.GetType().Name);
                return new SignInToken(SignInTokenStatus.Unavailable);
            }
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement e)
                || e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString() ?? "";
            return value.Length > 0;
        }
    }
}
