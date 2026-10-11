using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Collection;

namespace Kingdom.Session
{
    public static class CraftingGateway
    {
        private static HttpCraftingGateway Create()
            => new HttpCraftingGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(30));

        public static Task<CraftingReply> ReadAsync(int cardId, string accessToken, CancellationToken ct = default)
            => Create().ReadAsync(cardId, accessToken, ct);

        public static Task<CraftingReply> RenderAsync(int cardId, string accessToken, CancellationToken ct = default)
            => Create().RenderAsync(cardId, accessToken, ct);

        public static Task<CraftingReply> ReduceAsync(int cardId, string accessToken, CancellationToken ct = default)
            => Create().ReduceAsync(cardId, accessToken, ct);
    }
}
