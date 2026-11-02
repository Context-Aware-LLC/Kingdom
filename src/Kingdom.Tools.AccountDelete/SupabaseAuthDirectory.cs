using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// Supabase Auth's Admin API (<c>/auth/v1/admin/users</c>) with the project's secret key.
    ///
    /// <para><b>Deliberately here and not in the game server.</b> The server's <c>SupabaseAdmin</c>
    /// can mint a sign-in link and nothing else; the power to delete a user lives only in this tool,
    /// which only an approved plan runs.</para>
    ///
    /// <para>The key is sent only to the project's own URL and never printed.</para>
    /// </summary>
    internal sealed class SupabaseAuthDirectory : IAuthDirectory
    {
        /// <summary>Pages are read until one comes back empty, so a server-side cap on
        /// <c>per_page</c> cannot make the search stop early.</summary>
        internal const int PerPage = 200;
        private const int MaxPages = 10_000;

        private readonly HttpClient _http;
        private readonly string _base;
        private readonly string _key;

        public SupabaseAuthDirectory(HttpClient http, string projectUrl, string secretKey)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _base = (projectUrl ?? "").TrimEnd('/') + "/auth/v1/admin/users";
            _key = secretKey ?? "";
        }

        public async Task<IReadOnlyList<AuthUser>> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            var found = new List<AuthUser>();
            for (int page = 1; page <= MaxPages; page++)
            {
                using var request = Request(HttpMethod.Get, $"{_base}?page={page}&per_page={PerPage}");
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Supabase list users answered {(int)response.StatusCode}.");

                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!body.RootElement.TryGetProperty("users", out JsonElement users) || users.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Supabase list users answered without a users array.");
                if (users.GetArrayLength() == 0) return found;

                foreach (JsonElement u in users.EnumerateArray())
                {
                    if (!Matches(u, email)) continue;
                    string id = Str(u, "id") ?? throw new InvalidOperationException("A Supabase user has no id.");
                    var providers = new SortedSet<string>(StringComparer.Ordinal);
                    int identities = 0;
                    if (u.TryGetProperty("identities", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement i in ids.EnumerateArray())
                        {
                            identities++;
                            if (Str(i, "provider") is string p) providers.Add(p);
                        }
                    }
                    found.Add(new AuthUser(id, providers.ToList(), identities));
                }
            }
            throw new InvalidOperationException("Supabase list users did not end.");
        }

        public async Task<AuthDeleteResult> DeleteUserAsync(string userId, CancellationToken ct = default)
        {
            // The id goes into the path, so it must be exactly a uuid.
            if (!Guid.TryParseExact(userId, "D", out Guid id))
                throw new ArgumentException("A Supabase user id is a uuid.", nameof(userId));

            using var request = Request(HttpMethod.Delete, $"{_base}/{id:D}");
            request.Content = new StringContent("{\"should_soft_delete\":false}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return AuthDeleteResult.NotFound;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Supabase delete user answered {(int)response.StatusCode}.");
            return AuthDeleteResult.Deleted;
        }

        /// <summary>The user's own email, or an identity's (a Google sign-in carries it there).</summary>
        internal static bool Matches(JsonElement user, string email)
        {
            if (Eq(Str(user, "email"), email)) return true;
            if (!user.TryGetProperty("identities", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement i in ids.EnumerateArray())
            {
                if (Eq(Str(i, "email"), email)) return true;
                if (i.TryGetProperty("identity_data", out JsonElement data) && Eq(Str(data, "email"), email)) return true;
            }
            return false;
        }

        private static bool Eq(string? a, string email) => a is not null && DeletionPlan.NormalizeEmail(a) == email;

        private static string? Str(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;

        private HttpRequestMessage Request(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("apikey", _key);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            return request;
        }
    }
}
