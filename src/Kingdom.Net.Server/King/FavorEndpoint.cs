using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// The name the board and the Roll may show for a stored one. A player with no valid name plays
    /// under <c>MatchGate</c>'s per-match seat handle (<c>seat-17</c>), and that is what banks; a
    /// handle names nobody, and on a board that lasts a week it reads as a bug. Such a name, and a
    /// blank one, goes out as <c>""</c>, and the client draws its own placeholder in the player's
    /// language (<c>king_favor_unnamed</c>). Not null: on the Roll, null already means "no Favorite".
    /// </summary>
    public static class FavorNames
    {
        private const string SeatPrefix = "seat-";

        public static string Shown(string? stored) => IsUnnamed(stored) ? "" : stored!;

        /// <summary>Blank, or exactly <c>seat-</c> and digits, the form <c>MatchGate</c> mints.
        /// <c>PlayerName</c> allows a player to choose that form too; they get the placeholder.</summary>
        public static bool IsUnnamed(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (!name!.StartsWith(SeatPrefix, StringComparison.Ordinal) || name.Length == SeatPrefix.Length) return false;
            for (int i = SeatPrefix.Length; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }
    }

    /// <summary>Why <c>GET /king/favor/me</c> answered the way it did. The client branches on this.</summary>
    public enum FavorStandingStatus
    {
        Ok,
        Unauthenticated,
        Unavailable,
    }

    /// <param name="Place">Null when the player has no score in the current reign.</param>
    /// <param name="Seals">Commission ids of every reign the player scored in, each once, oldest first.</param>
    public sealed record FavorStandingResult(
        FavorStandingStatus Status, int? Place, int? Score, IReadOnlyList<string> Seals, int HttpStatus);

    /// <summary>
    /// <c>GET /king/favor/me</c> — the bearer's own place on this reign's board, which the board
    /// itself cannot show once they are off its hundred rows, and the commissions whose seal they
    /// hold in the Ledger. Read-only, and for the bearer's own account only: there is no player
    /// parameter. The no-King case is answered by the route before this is reached.
    /// </summary>
    public static class FavorEndpoint
    {
        /// <summary>How many rows <c>GET /king/favor</c> answers.</summary>
        public const int BoardLimit = 100;

        private static readonly IReadOnlyList<string> NoSeals = Array.Empty<string>();

        public static async Task<FavorStandingResult> ReadStandingAsync(
            string? bearer,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            IFavorStore favor,
            int reign,
            CancellationToken ct = default)
        {
            if (verifier is null || accounts is null || favor is null) return Unavailable();
            if (string.IsNullOrWhiteSpace(bearer)) return Unauthenticated();

            TokenVerificationResult verified;
            try
            {
                verified = await verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Same rule as /rating: an unreachable JWKS is an outage, not a rejected token.
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

                bool lowerIsBetter = CommissionCatalog.Find(KingSchedule.For(reign).Commission)?.LowerIsBetter ?? false;
                int? place = await favor.PlaceAsync(reign, account.PlayerId, lowerIsBetter, ct).ConfigureAwait(false);
                int? score = place is null
                    ? null
                    : await favor.BestAsync(reign, account.PlayerId, ct).ConfigureAwait(false);

                // Commission ids are achievement ids, so a seal names its Ledger page directly.
                var seals = new List<string>();
                foreach (int played in await favor.ReignsPlayedAsync(account.PlayerId, ct).ConfigureAwait(false))
                {
                    string commission = KingSchedule.For(played).Commission;
                    if (!seals.Contains(commission)) seals.Add(commission);
                }
                return new FavorStandingResult(FavorStandingStatus.Ok, place, score, seals, 200);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Unavailable();
            }
        }

        private static FavorStandingResult Unauthenticated()
            => new FavorStandingResult(FavorStandingStatus.Unauthenticated, null, null, NoSeals, 401);

        private static FavorStandingResult Unavailable()
            => new FavorStandingResult(FavorStandingStatus.Unavailable, null, null, NoSeals, 503);
    }
}
