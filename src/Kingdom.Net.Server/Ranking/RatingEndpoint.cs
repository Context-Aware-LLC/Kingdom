using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Ranking
{
    /// <summary>
    /// Why <c>GET /rating</c> answered the way it did. The client branches on this string and never
    /// on the number: <see cref="Unranked"/> carries the default rating, which is indistinguishable
    /// from a rating somebody earned.
    /// </summary>
    public enum RatingReadStatus
    {
        Ranked,
        Unranked,
        Unauthenticated,
        Unavailable,
    }

    public sealed record RatingReadResult(
        RatingReadStatus Status,
        int Rating,
        long GamesPlayed,
        int HttpStatus);

    /// <summary>
    /// <c>GET /rating</c> — the signed-in player's own ladder standing. <c>/leaderboard</c> is public
    /// and answers the top of the ladder by display name, so before this a client had no way to learn
    /// its own rating unless it happened to be on that page.
    ///
    /// <para>Read-only by construction, and it answers for the bearer's own account and nobody
    /// else's: there is no player parameter to supply.</para>
    /// </summary>
    public static class RatingEndpoint
    {
        public static async Task<RatingReadResult> ReadAsync(
            string? bearer,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            IRatingStore? ratings,
            CancellationToken ct = default)
        {
            if (verifier is null || accounts is null || ratings is null)
                return Unavailable();

            if (string.IsNullOrWhiteSpace(bearer))
                return new RatingReadResult(RatingReadStatus.Unauthenticated, 0, 0, 401);

            TokenVerificationResult verified;
            try
            {
                verified = await verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Same rule as /quests: a verifier that could not reach its JWKS is an outage, not
                // a rejected token, and 401 would sign the player out over a network blip.
                return Unavailable();
            }

            if (!verified.IsSuccess || verified.Identity is null)
                return new RatingReadResult(RatingReadStatus.Unauthenticated, 0, 0, 401);

            try
            {
                Account account = await accounts.ResolveOrCreateAsync(
                    "supabase",
                    CredentialHash.Of(verified.Identity!.Subject),
                    verified.Identity!.Email,
                    ct).ConfigureAwait(false);

                PlayerStanding? standing = await ratings.GetStandingAsync(
                    AccountId.FromAuthenticatedAccount(account.PlayerId), ct).ConfigureAwait(false);

                return standing is null
                    ? new RatingReadResult(RatingReadStatus.Unranked, ratings.DefaultRating, 0, 200)
                    : new RatingReadResult(RatingReadStatus.Ranked, standing.Rating, standing.GamesPlayed, 200);
            }
            catch (Exception)
            {
                return Unavailable();
            }
        }

        private static RatingReadResult Unavailable()
            => new RatingReadResult(RatingReadStatus.Unavailable, 0, 0, 503);
    }
}
