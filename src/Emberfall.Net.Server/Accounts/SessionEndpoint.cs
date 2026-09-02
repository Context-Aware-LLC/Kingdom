using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>GET /session</c> answers with. The client BLOCKS its boot on this, so the
    /// only two outcomes that may produce a session are <see cref="Ready"/> — everything else is a
    /// screen, never a degraded launch.</summary>
    public enum SessionResultStatus
    {
        /// <summary>The account resolved. <see cref="SessionResult.Onboarded"/> is authoritative.</summary>
        Ready,

        /// <summary>No bearer token, or one Supabase refused. 401 — the client clears the session
        /// and returns to sign-in, per RefreshAsync's existing rule that a 4xx kills a token.</summary>
        Unauthenticated,

        /// <summary>The box has no account store. Genuinely cannot answer. 503 — the client RETRIES
        /// and keeps its token; it must never read this as "new player".</summary>
        Unavailable,
    }

    /// <summary>The handshake's answer, before it becomes JSON.</summary>
    public sealed class SessionResult
    {
        public SessionResultStatus Status { get; }
        public string PlayerId { get; }
        public string? Email { get; }

        /// <summary>Meaningful only when <see cref="Status"/> is <see cref="SessionResultStatus.Ready"/>.
        /// False on every other status is a C# default, NOT an answer — which is exactly why those
        /// statuses never become a session on the client.</summary>
        public bool Onboarded { get; }

        public SessionResult(SessionResultStatus status, string playerId = "",
                             string? email = null, bool onboarded = false)
        {
            Status = status;
            PlayerId = playerId ?? "";
            Email = email;
            Onboarded = onboarded;
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
    /// <c>GET /session</c> — the login handshake. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para><b>This is the route that makes the login gate mean what its name implies.</b>
    /// <c>SignInGateController.IsRequired</c> reads a session off local disk and says nothing about
    /// a server, so before this existed a returning player booted the entire menu — roster, deck
    /// builder, character creation — on unverified local state, and the first real server contact
    /// was a match join.</para>
    ///
    /// <para><b>It fires the onboarding grant, for the reason <c>CollectionEndpoint</c> already
    /// gives.</b> The grant fires when an account RESOLVES, and a player who never queues never
    /// resolved one. Idempotent on <c>signup:{playerId}</c>, so a third call site grants exactly
    /// once. A grant failure must NOT fail the handshake — that would turn a database blip into a
    /// game that will not start.</para>
    /// </summary>
    public static class SessionEndpoint
    {
        public static async Task<SessionResult> ReadAsync(
            string? bearerToken,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new SessionResult(SessionResultStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct);
            if (!verified.IsSuccess)
                return new SessionResult(SessionResultStatus.Unauthenticated);

            if (accountStore is null)
                return new SessionResult(SessionResultStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct);

            if (onboarding is not null)
            {
                try
                {
                    await onboarding.EnsureGrantedAsync(account, ct);
                }
                catch (System.Exception ex)
                {
                    // Logged, never swallowed silently: a grant failing every time is invisible
                    // otherwise. But it does not cost the player their launch.
                    System.Console.Error.WriteLine("[session] onboarding grant failed: {0}", ex.Message);
                }
            }

            return new SessionResult(SessionResultStatus.Ready, account.PlayerId,
                                     account.Email, account.Onboarded);
        }
    }
}
