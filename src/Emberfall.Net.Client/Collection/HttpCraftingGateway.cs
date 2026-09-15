using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;

namespace Emberfall.Net.Client.Collection
{
    public sealed class CraftingReply
    {
        public string Status { get; }
        public int CardId { get; }
        public int CopiesOwned { get; }
        public long Shards { get; }
        public bool HasSnapshot { get; }
        public bool Applied => Status == "rendered" || Status == "reduced" || Status == "replayed";

        public CraftingReply(string status, int cardId = 0, int copiesOwned = 0, long shards = 0, bool hasSnapshot = false)
        {
            Status = status;
            CardId = cardId;
            CopiesOwned = copiesOwned;
            Shards = shards;
            HasSnapshot = hasSnapshot;
        }
    }

    public sealed class HttpCraftingGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpCraftingGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public Task<CraftingReply> ReadAsync(int cardId, string accessToken, CancellationToken ct = default)
            => SendAsync(cardId, null, accessToken, ct);

        public Task<CraftingReply> RenderAsync(int cardId, string accessToken, CancellationToken ct = default)
            => SendAsync(cardId, "render", accessToken, ct);

        public Task<CraftingReply> ReduceAsync(int cardId, string accessToken, CancellationToken ct = default)
            => SendAsync(cardId, "reduce", accessToken, ct);

        private async Task<CraftingReply> SendAsync(int cardId, string? action, string accessToken, CancellationToken ct)
        {
            if (_baseUrl.Length == 0) return new CraftingReply("unavailable");
            if (string.IsNullOrWhiteSpace(accessToken)) return new CraftingReply("unauthenticated");
            if (cardId <= 0) return new CraftingReply("invalidcard");
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            string url = _baseUrl + "/crafting/" + cardId.ToString(CultureInfo.InvariantCulture);
            AuthHttpResponse response;
            try
            {
                response = action == null
                    ? await _http.GetAsync(url, headers, ct).ConfigureAwait(false)
                    : await _http.PostJsonAsync(url + "/" + action, "{}", headers, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new CraftingReply("offline");
            }
            if (response.StatusCode == 401) return new CraftingReply("unauthenticated");
            if (response.StatusCode == 503 || response.StatusCode == 404) return new CraftingReply("unavailable");
            if (response.StatusCode == 400) return new CraftingReply("invalidcard");
            if (response.IsTransportFailure || (!response.IsSuccess && response.StatusCode != 409))
                return new CraftingReply("offline");
            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status)) return new CraftingReply("malformed");
            bool expected = response.StatusCode == 409
                ? status == "insufficientshards" || status == "atcopycap" || status == "ownsnone" ||
                  status == "notcraftable" || status == "notreducible"
                : action == null ? status == "ready"
                : status == "replayed" || status == (action == "render" ? "rendered" : "reduced");
            if (!expected ||
                !AuthJson.TryInt64(fields, "cardId", out long id) || id != cardId ||
                !AuthJson.TryInt64(fields, "copiesOwned", out long copies) || copies < 0 || copies > int.MaxValue ||
                !AuthJson.TryInt64(fields, "shards", out long shards) || shards < 0)
                return new CraftingReply("malformed");
            return new CraftingReply(status, cardId, (int)copies, shards, true);
        }
    }
}
