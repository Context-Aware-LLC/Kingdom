using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Identity
{
    public interface IIdentityTokenVerifier
    {
        Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default);
    }

    /// <summary>The verified claims extracted from a Supabase JWT.</summary>
    /// <param name="DisplayName">
    /// The name the player gave themselves at sign-up (<c>user_metadata.display_name</c>), or
    /// empty. <b>Verified means the token is genuine, not that this string is safe</b>: a signed-in
    /// user can write anything into their own <c>user_metadata</c>, so this is peer-authored text
    /// and goes through <c>PlayerName.Check</c> before it is stored or shown to anybody.
    /// </param>
    public sealed record VerifiedIdentity(string Subject, string Email, bool EmailVerified, string DisplayName = "");

    public enum TokenVerificationFailure
    {
        /// <summary>Token is not a parseable JWT.</summary>
        Malformed,

        /// <summary>Algorithm is none, symmetric, or otherwise not in the accepted asymmetric set.
        /// Symmetric algs (HS256 etc.) are rejected because the same key can mint tokens; a game
        /// server must never hold a key that can forge its own users.</summary>
        AlgorithmRejected,

        /// <summary>The token's kid is not in the current or refreshed JWKS.</summary>
        UnknownKid,

        /// <summary>Signature does not verify against any matching key.</summary>
        SignatureInvalid,

        Expired,
        NotYetValid,
        WrongIssuer,
        WrongAudience,

        /// <summary>JWKS endpoint was unreachable or returned an unparseable response.</summary>
        JwksUnavailable,
    }

    public sealed class TokenVerificationResult
    {
        public bool IsSuccess { get; }
        public VerifiedIdentity? Identity { get; }
        public TokenVerificationFailure? Failure { get; }

        private TokenVerificationResult(VerifiedIdentity identity)
        {
            IsSuccess = true;
            Identity = identity;
        }

        private TokenVerificationResult(TokenVerificationFailure failure)
        {
            IsSuccess = false;
            Failure = failure;
        }

        public static TokenVerificationResult Ok(VerifiedIdentity identity) => new(identity);
        public static TokenVerificationResult Fail(TokenVerificationFailure failure) => new(failure);
    }
}
