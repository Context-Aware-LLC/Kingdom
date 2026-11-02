using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// The landing site's <c>prereg</c> table in Cloudflare D1 <c>kobi-site</c>, through Cloudflare's
    /// D1 query API with a token scoped to D1. The worker stores addresses trimmed and lower-cased
    /// (<c>web/landing/src/worker.js</c>), which is the form <see cref="DeletionPlan.Email"/> takes.
    /// </summary>
    internal sealed class D1PreregStore : IPreregStore
    {
        /// <summary>The <c>kobi-site</c> database id, as <c>web/landing/wrangler.jsonc</c> binds it.
        /// A test reads that file and fails if the two part.</summary>
        internal const string KobiSiteDatabaseId = "d9d0ed21-5218-448c-9b8c-2722f7fbe7ae";

        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _token;

        public D1PreregStore(HttpClient http, string accountId, string apiToken, string databaseId = KobiSiteDatabaseId)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (!IsToken(accountId) || !IsToken(databaseId))
                throw new ArgumentException("A Cloudflare account or database id is hex and hyphens only.");
            _endpoint = $"https://api.cloudflare.com/client/v4/accounts/{accountId}/d1/database/{databaseId}/query";
            _token = apiToken ?? "";
        }

        public async Task<long> CountAsync(string email, CancellationToken ct = default)
        {
            JsonElement result = await QueryAsync("select count(*) as n from prereg where email = ?1", email, ct);
            JsonElement rows = result.GetProperty("results");
            return rows.GetArrayLength() == 0 ? 0 : rows[0].GetProperty("n").GetInt64();
        }

        public async Task<long> DeleteAsync(string email, CancellationToken ct = default)
        {
            JsonElement result = await QueryAsync("delete from prereg where email = ?1", email, ct);
            return result.TryGetProperty("meta", out JsonElement meta) && meta.TryGetProperty("changes", out JsonElement c)
                ? c.GetInt64() : 0;
        }

        private async Task<JsonElement> QueryAsync(string sql, string email, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { sql, @params = new[] { email } }), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Cloudflare D1 answered {(int)response.StatusCode}.");

            using JsonDocument body = JsonDocument.Parse(text);
            JsonElement root = body.RootElement;
            if (!root.TryGetProperty("success", out JsonElement ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("result", out JsonElement results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() != 1)
                throw new InvalidOperationException("Cloudflare D1 did not report one successful statement.");
            JsonElement first = results[0];
            if (first.TryGetProperty("success", out JsonElement stmtOk) && stmtOk.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Cloudflare D1 reported the statement failed.");
            return first.Clone();
        }

        private static bool IsToken(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
            foreach (char c in s)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' or '-')) return false;
            return true;
        }
    }
}
