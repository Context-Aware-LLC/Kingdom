using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>What <c>GET /account</c> answers with, before it becomes JSON.</summary>
    public sealed class AccountReadResult
    {
        /// <summary>"ready" | "unauthenticated" | "unavailable", the same three as <c>/session</c>.</summary>
        public SessionResultStatus Status { get; }
        public string? Email { get; }

        /// <summary>When the account row was first created, by the server's clock. Meaningful only
        /// under <see cref="SessionResultStatus.Ready"/>.</summary>
        public DateTimeOffset? CreatedAt { get; }

        public AccountReadResult(SessionResultStatus status, string? email = null,
                                 DateTimeOffset? createdAt = null)
        {
            Status = status;
            Email = email;
            CreatedAt = createdAt;
        }

        public int HttpStatus => Status switch
        {
            SessionResultStatus.Ready => 200,
            SessionResultStatus.Unauthenticated => 401,
            SessionResultStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>GET /account</c>: the facts the settings Account view shows about the bearer (Sam,
    /// 2026-11-06). Read-only on purpose. <c>/session</c> could carry the same fields, but it stamps
    /// last-seen and fires the onboarding grant, and opening a settings view is not a login.
    /// </summary>
    public static class AccountEndpoint
    {
        public static async Task<AccountReadResult> ReadAsync(
            string? bearerToken,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new AccountReadResult(SessionResultStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new AccountReadResult(SessionResultStatus.Unauthenticated);

            if (accountStore is null)
                return new AccountReadResult(SessionResultStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            return new AccountReadResult(SessionResultStatus.Ready, account.Email ?? identity.Email,
                                         account.CreatedAt);
        }
    }
}
