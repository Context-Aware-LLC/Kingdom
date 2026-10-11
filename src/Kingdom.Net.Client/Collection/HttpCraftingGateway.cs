using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Collection
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

    /// <summary>The receipt for a Reduce of every spare copy. <see cref="Applied"/> only on a server-confirmed one.</summary>
    public sealed class ReduceSparesReply
    {
        public string Status { get; }
        public int CopiesReduced { get; }
        public long ShardsGained { get; }
        public long Shards { get; }
        public bool HasSnapshot { get; }
        public bool Applied => Status == "reduced" || Status == "replayed";

        public ReduceSparesReply(string status, int copiesReduced = 0, long shardsGained = 0, long shards = 0,
                                 bool hasSnapshot = false)
        {
            Status = status;
            CopiesReduced = copiesReduced;
            ShardsGained = shardsGained;
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
            => SendAsync(cardId, null, 1, accessToken, ct);

        public Task<CraftingReply> RenderAsync(int cardId, string accessToken, CancellationToken ct = default)
            => SendAsync(cardId, "render", 1, accessToken, ct);

        /// <summary>Break <paramref name="count"/> copies down in one server transaction: all of them or none.</summary>
        public Task<CraftingReply> ReduceAsync(int cardId, int count, string accessToken, CancellationToken ct = default)
            => SendAsync(cardId, "reduce", count, accessToken, ct);

        /// <summary>
        /// Break down every copy held past a deck cap. The request names no card: the server decides
        /// what is spare, and the reply says what it actually destroyed and paid.
        /// </summary>
        public async Task<ReduceSparesReply> ReduceSparesAsync(bool includeLegendary, string accessToken,
                                                               CancellationToken ct = default)
        {
            if (_baseUrl.Length == 0) return new ReduceSparesReply("unavailable");
            if (string.IsNullOrWhiteSpace(accessToken)) return new ReduceSparesReply("unauthenticated");
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            AuthHttpResponse response;
            try
            {
                response = await _http.PostJsonAsync(_baseUrl + "/crafting/spares/reduce",
                    includeLegendary ? "{\"includeLegendary\":true}" : "{\"includeLegendary\":false}",
                    headers, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new ReduceSparesReply("offline");
            }
            if (response.StatusCode == 401) return new ReduceSparesReply("unauthenticated");
            if (response.StatusCode == 503 || response.StatusCode == 404) return new ReduceSparesReply("unavailable");
            if (response.IsTransportFailure || (!response.IsSuccess && response.StatusCode != 409))
                return new ReduceSparesReply("offline");
            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status)) return new ReduceSparesReply("malformed");
            bool expected = response.StatusCode == 409
                ? status == "nospares"
                : status == "reduced" || status == "replayed";
            if (!expected ||
                !AuthJson.TryInt64(fields, "copiesReduced", out long copies) || copies < 0 || copies > int.MaxValue ||
                !AuthJson.TryInt64(fields, "shardsGained", out long gained) || gained < 0 ||
                !AuthJson.TryInt64(fields, "shards", out long shards) || shards < 0 ||
                (status == "reduced") != (copies > 0))
                return new ReduceSparesReply("malformed");
            return new ReduceSparesReply(status, (int)copies, gained, shards, true);
        }

        private async Task<CraftingReply> SendAsync(int cardId, string? action, int count, string accessToken, CancellationToken ct)
        {
            if (_baseUrl.Length == 0) return new CraftingReply("unavailable");
            if (string.IsNullOrWhiteSpace(accessToken)) return new CraftingReply("unauthenticated");
            if (cardId <= 0) return new CraftingReply("invalidcard");
            if (count < 1) return new CraftingReply("invalidcount");
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
                    : await _http.PostJsonAsync(url + "/" + action,
                        action == "reduce" ? "{\"count\":" + count.ToString(CultureInfo.InvariantCulture) + "}" : "{}",
                        headers, ct).ConfigureAwait(false);
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
                  status == "notenoughcopies" || status == "notcraftable" || status == "notreducible"
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
