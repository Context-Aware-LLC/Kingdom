using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// Why <c>GET /achievements</c> answered the way it did. The client branches on this and never
    /// on the list's length: "earned nothing yet" and "the read failed" must not look the same.
    /// </summary>
    public enum AchievementReadStatus
    {
        Known,
        Unauthenticated,
        Unavailable,
    }

    /// <summary>One row of <c>GET /achievements</c>.</summary>
    /// <param name="PlayGamesId">Empty while the achievement has no Play Console entry.</param>
    public readonly record struct AchievementView(string Id, string PlayGamesId, DateTimeOffset EarnedAt);

    public sealed record AchievementReadResult(
        AchievementReadStatus Status,
        IReadOnlyList<AchievementView> Achievements,
        int HttpStatus);

    /// <summary>
    /// <c>GET /achievements</c> — what the player has earned, with the store id the client unlocks.
    /// Read-only by construction: the server is the only thing that may earn one, and the client
    /// copies its answer to Play Games, never the other way.
    /// </summary>
    public static class AchievementsEndpoint
    {
        public static async Task<AchievementReadResult> ReadAsync(
            string? bearer,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            AchievementService? achievements,
            CancellationToken ct = default)
        {
            if (verifier is null || accounts is null || achievements is null)
                return Unavailable();

            if (string.IsNullOrWhiteSpace(bearer)) return Unauthenticated();

            TokenVerificationResult verified;
            try
            {
                verified = await verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A verifier that could not reach its JWKS is an outage, not a rejected token.
                return Unavailable();
            }

            if (!verified.IsSuccess || verified.Identity is null) return Unauthenticated();

            try
            {
                Account account = await accounts.ResolveOrCreateAsync(
                    "supabase",
                    CredentialHash.Of(verified.Identity!.Subject),
                    verified.Identity!.Email,
                    ct).ConfigureAwait(false);

                IReadOnlyList<EarnedAchievement> earned =
                    await achievements.EarnedAsync(account.PlayerId, ct).ConfigureAwait(false);

                var views = new List<AchievementView>(earned.Count);
                foreach (EarnedAchievement row in earned)
                {
                    // A row the catalog no longer names still comes back: it was earned, and an id is
                    // retired, never removed. It simply has nothing to unlock.
                    string playGamesId = AchievementCatalog.Find(row.AchievementId)?.PlayGamesId ?? "";
                    views.Add(new AchievementView(row.AchievementId, playGamesId, row.EarnedAt));
                }
                return new AchievementReadResult(AchievementReadStatus.Known, views, 200);
            }
            catch (Exception)
            {
                return Unavailable();
            }
        }

        private static AchievementReadResult Unauthenticated()
            => new AchievementReadResult(AchievementReadStatus.Unauthenticated, Array.Empty<AchievementView>(), 401);

        private static AchievementReadResult Unavailable()
            => new AchievementReadResult(AchievementReadStatus.Unavailable, Array.Empty<AchievementView>(), 503);
    }
}
