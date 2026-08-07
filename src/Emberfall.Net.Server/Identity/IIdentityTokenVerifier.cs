using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Identity
{
    public interface IIdentityTokenVerifier
    {
        Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default);
    }

    /// <summary>The verified claims extracted from a Supabase JWT.</summary>
    public sealed record VerifiedIdentity(string Subject, string Email, bool EmailVerified);

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
