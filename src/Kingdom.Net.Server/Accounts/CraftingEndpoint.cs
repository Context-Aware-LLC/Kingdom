using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    public sealed record CraftingResult(string Status, int HttpStatus, CraftOutcome? Craft = null);

    public sealed record ReduceSparesResult(string Status, int HttpStatus, ReduceSparesOutcome? Spares = null);

    public static class CraftingEndpoint
    {
        public static async Task<CraftingResult> ExecuteAsync(
            string? bearerToken, int cardId, string? action,
            IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            CraftingService? crafting, ICardDatabase cards,
            CancellationToken ct = default, int count = 1)
        {
            if (string.IsNullOrWhiteSpace(bearerToken)) return new("unauthenticated", 401);
            if (verifier is null || accounts is null || crafting is null) return new("unavailable", 503);
            var verified = await verifier.VerifyAsync(bearerToken, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null) return new("unauthenticated", 401);
            if (cardId <= 0 || !cards.TryGet(cardId, out CardDefinition def) || !def.PoolEligible)
                return new("invalidcard", 400);
            if (action is not null && action != "render" && action != "reduce") return new("invalidaction", 400);
            // The one client-chosen number on this route. Its floor is here; its ceiling is the copies
            // the ledger holds, which only CraftingService can read under the lock.
            if (count < 1) return new("invalidcount", 400);

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

            ReduceOutcome reduced = await crafting.ReduceAsync(account, cardId, count, transactionId, ct).ConfigureAwait(false);
            bool success = reduced.Status is ReduceStatus.Reduced or ReduceStatus.Replayed;
            return new(reduced.Status.ToString().ToLowerInvariant(), success ? 200 : 409, reduced.Craft);
        }

        /// <summary>
        /// The bulk Reduce: the spare copies the player left selected, in one server-minted
        /// transaction. <paramref name="cards"/> is card id to copies, and null when the body did
        /// not parse into one.
        /// </summary>
        public static async Task<ReduceSparesResult> ReduceSparesAsync(
            string? bearerToken, IReadOnlyDictionary<int, int>? cards,
            IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            CraftingService? crafting,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken)) return new("unauthenticated", 401);
            if (verifier is null || accounts is null || crafting is null) return new("unavailable", 503);
            var verified = await verifier.VerifyAsync(bearerToken, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity is null) return new("unauthenticated", 401);
            if (cards is null || cards.Count == 0) return new("invalidrequest", 400);

            Account account = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of(verified.Identity.Subject), verified.Identity.Email, ct).ConfigureAwait(false);
            if (!EconomyAccountPolicy.IsDurable(account)) return new("unauthenticated", 401);

            ReduceSparesOutcome outcome = await crafting.ReduceSparesAsync(
                account, cards, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            return new(outcome.Status.ToString().ToLowerInvariant(),
                outcome.Status == ReduceSparesStatus.NotSpare ? 409 : 200, outcome);
        }
    }
}
