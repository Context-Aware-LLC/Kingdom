using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    public sealed record CraftingResult(string Status, int HttpStatus, CraftOutcome? Craft = null);

    public static class CraftingEndpoint
    {
        public static async Task<CraftingResult> ExecuteAsync(
            string? bearerToken, int cardId, string? action,
            IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            CraftingService? crafting, ICardDatabase cards,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken)) return new("unauthenticated", 401);
            if (verifier is null || accounts is null || crafting is null) return new("unavailable", 503);
            var verified = await verifier.VerifyAsync(bearerToken, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null) return new("unauthenticated", 401);
            if (cardId <= 0 || !cards.TryGet(cardId, out CardDefinition def) || !def.Collectible)
                return new("invalidcard", 400);
            if (action is not null && action != "render" && action != "reduce") return new("invalidaction", 400);

            Account account = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of(verified.Identity.Subject), verified.Identity.Email, ct).ConfigureAwait(false);
            if (!EconomyAccountPolicy.IsDurable(account)) return new("unauthenticated", 401);

            if (action is null)
                return new("ready", 200, await crafting.ReadAsync(account, cardId, ct).ConfigureAwait(false));

            // One server-minted transaction per click. The client reconciles a lost response by
            // reading balances; it never automatically repeats a mutation.
            string transactionId = Guid.NewGuid().ToString("N");
            if (action == "render")
            {
                RenderOutcome result = await crafting.RenderAsync(account, cardId, transactionId, ct).ConfigureAwait(false);
                bool applied = result.Status is RenderStatus.Rendered or RenderStatus.Replayed;
                return new(result.Status.ToString().ToLowerInvariant(), applied ? 200 : 409, result.Craft);
            }

            ReduceOutcome reduced = await crafting.ReduceAsync(account, cardId, transactionId, ct).ConfigureAwait(false);
            bool success = reduced.Status is ReduceStatus.Reduced or ReduceStatus.Replayed;
            return new(reduced.Status.ToString().ToLowerInvariant(), success ? 200 : 409, reduced.Craft);
        }
    }
}
