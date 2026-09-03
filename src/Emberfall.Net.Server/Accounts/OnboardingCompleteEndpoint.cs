using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>POST /onboarding/complete</c> answers with.</summary>
    public enum OnboardingCompleteStatus
    {
        /// <summary>The account is marked onboarded — whether this call did it or a prior one
        /// already had. 200.</summary>
        Completed,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store. Genuinely cannot answer. 503.</summary>
        Unavailable,
    }

    /// <summary>The onboarding-complete answer, before it becomes JSON.</summary>
    public sealed class OnboardingCompleteResult
    {
        public OnboardingCompleteStatus Status { get; }

        public OnboardingCompleteResult(OnboardingCompleteStatus status)
        {
            Status = status;
        }

        public int HttpStatus => Status switch
        {
            OnboardingCompleteStatus.Completed => 200,
            OnboardingCompleteStatus.Unauthenticated => 401,
            OnboardingCompleteStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>POST /onboarding/complete</c> — the terminal write of the login handshake. Spec:
    /// <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para><b>The only route in this server that sets <c>Onboarded</c>.</b> It runs at the END of
    /// onboarding, after the entitlement grant has landed — never at character creation
    /// (<see cref="CharacterCreateEndpoint"/> deliberately does not touch it). A player who drops
    /// before the grant must come back still not-onboarded so the flow retries; marking them early
    /// would strand them owning nothing with nothing left to retry.</para>
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/>,
    /// <see cref="RosterEndpoint"/> and <see cref="CharacterCreateEndpoint"/> do — verifier, then
    /// <c>CredentialHash.Of(identity.Subject)</c>, then <c>ResolveOrCreateAsync</c>. Delegates the
    /// write itself to <see cref="IAccountStore.MarkOnboardedAsync"/>, which is one-way and
    /// idempotent by design — calling this endpoint twice (a client retrying a dropped response)
    /// returns <see cref="OnboardingCompleteStatus.Completed"/> both times, never an error.</para>
    ///
    /// <para><b>There is deliberately NO roster precondition, and asking for one misreads the flag</b>
    /// (Sam, 2026-09-03, closing #344). <c>Onboarded</c> is unrelated to character creation: it
    /// records that the onboarding FLOW completed, and the thing it is really guarding is the
    /// entitlement grant, which fires from <see cref="SessionEndpoint"/> and
    /// <see cref="CollectionEndpoint"/> without a character existing. Requiring at least one
    /// character before the flag may be set would bind a one-way account flag to a per-character
    /// fact and would refuse a legitimate completion for a player who has one and deleted it. Do not
    /// add the check; the client is what knows the flow finished.</para>
    /// </summary>
    public static class OnboardingCompleteEndpoint
    {
        public static async Task<OnboardingCompleteResult> CompleteAsync(
            string? bearerToken,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new OnboardingCompleteResult(OnboardingCompleteStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new OnboardingCompleteResult(OnboardingCompleteStatus.Unauthenticated);

            if (accountStore is null)
                return new OnboardingCompleteResult(OnboardingCompleteStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            await accountStore.MarkOnboardedAsync(account.PlayerId, ct).ConfigureAwait(false);

            return new OnboardingCompleteResult(OnboardingCompleteStatus.Completed);
        }
    }
}
