using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Social;

namespace Kingdom.Net.Server.Identity
{
    /// <summary>What <c>POST /play-games/restore</c> answered. The lower-cased name is the wire
    /// value.</summary>
    public enum PlayGamesRestoreStatus
    {
        Restored,

        /// <summary>No account holds this Play Games profile, or the one that did can no longer be
        /// reached by its address. The client shows the sign-in screen.</summary>
        NotLinked,

        /// <summary>Google refused the auth code.</summary>
        Refused,
        RateLimited,
        Unavailable,
    }

    public sealed record PlayGamesRestoreResult(
        PlayGamesRestoreStatus Status, int HttpStatus, string TokenHash = "", string TokenType = "");

    /// <summary>
    /// Seamless restore (#425): a device with no session signs in to Play Games, and the account that
    /// Play Games profile is linked to (<c>POST /play-games/link</c>, #423) comes back without the
    /// sign-in screen. Google's approved use of Play Games for accounts.
    ///
    /// <para><b>Play Games is the key to the account, never the account.</b> The answer is a
    /// single-use Supabase token for the linked account's own Supabase user, which the client
    /// redeems with Supabase. The session, and the identity behind it, are Supabase's.</para>
    ///
    /// <para><b>Unauthenticated, because it is what authenticates.</b> Its one input is a server auth
    /// code, which only Google can turn into a player id and only for this game's Game server
    /// credential; a client can name no id here, so it can restore no account but the one linked to
    /// the Play Games profile signed in on that phone.</para>
    ///
    /// <para><b>The linked account's address is checked against its identity.</b> The account row
    /// keeps the address it was first seen with. Supabase is asked for that address's user, and the
    /// token is answered only if that user's id hashes to the account's own credential: an address
    /// that moved to somebody else restores nobody.</para>
    /// </summary>
    public sealed class PlayGamesRestoreEndpoint
    {
        private readonly IPlayGamesApi? _playGames;
        private readonly IPlayGamesLinkStore? _links;
        private readonly IAccountStore? _accounts;
        private readonly ISupabaseAdmin? _admin;
        private readonly FriendRequestLimiter _limiter;

        public PlayGamesRestoreEndpoint(
            IPlayGamesApi? playGames,
            IPlayGamesLinkStore? links,
            IAccountStore? accounts,
            ISupabaseAdmin? admin,
            FriendRequestLimiter limiter)
        {
            _playGames = playGames;
            _links = links;
            _accounts = accounts;
            _admin = admin;
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        }

        /// <param name="caller">What the rate limit is keyed on: the client's address.</param>
        public async Task<PlayGamesRestoreResult> RestoreAsync(
            string? authCode, string caller, CancellationToken ct = default)
        {
            if (_playGames is null || _links is null || _accounts is null || _admin is null)
                return Answer(PlayGamesRestoreStatus.Unavailable);
            if (!_limiter.TryTake("restore:" + caller)) return Answer(PlayGamesRestoreStatus.RateLimited);
            if (string.IsNullOrWhiteSpace(authCode)) return Answer(PlayGamesRestoreStatus.Refused);

            try
            {
                PlayGamesSession session = await _playGames.OpenAsync(authCode!, withFriends: false, ct)
                    .ConfigureAwait(false);
                if (session.Status == PlayGamesCallStatus.CodeRefused) return Answer(PlayGamesRestoreStatus.Refused);
                if (session.Status != PlayGamesCallStatus.Ok || string.IsNullOrEmpty(session.PlayerId))
                    return Answer(PlayGamesRestoreStatus.Unavailable);

                IReadOnlyDictionary<string, string> holders =
                    await _links.FindAsync(new[] { session.PlayerId! }, ct).ConfigureAwait(false);
                if (!holders.TryGetValue(session.PlayerId!, out string? playerId))
                    return Answer(PlayGamesRestoreStatus.NotLinked);

                Account? account = await _accounts.FindByPlayerIdAsync(playerId, ct).ConfigureAwait(false);
                if (account is null || account.CredentialKind != "supabase" || string.IsNullOrWhiteSpace(account.Email))
                    return Answer(PlayGamesRestoreStatus.NotLinked);

                SignInToken token = await _admin.IssueSignInTokenAsync(account.Email!, ct).ConfigureAwait(false);
                if (token.Status == SignInTokenStatus.Unavailable) return Answer(PlayGamesRestoreStatus.Unavailable);
                if (token.Status != SignInTokenStatus.Issued) return Answer(PlayGamesRestoreStatus.NotLinked);
                if (CredentialHash.Of(token.UserId) != account.CredentialHash)
                {
                    Console.Error.WriteLine("[restore] the address on account {0} now belongs to another identity; " +
                                            "not restored.", account.PlayerId);
                    return Answer(PlayGamesRestoreStatus.NotLinked);
                }

                return new PlayGamesRestoreResult(PlayGamesRestoreStatus.Restored, 200, token.TokenHash, token.TokenType);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("[restore] failed: {0}", ex.GetType().Name);
                return Answer(PlayGamesRestoreStatus.Unavailable);
            }
        }

        private static PlayGamesRestoreResult Answer(PlayGamesRestoreStatus status) => status switch
        {
            PlayGamesRestoreStatus.NotLinked => new PlayGamesRestoreResult(status, 404),
            PlayGamesRestoreStatus.Refused => new PlayGamesRestoreResult(status, 401),
            PlayGamesRestoreStatus.RateLimited => new PlayGamesRestoreResult(status, 429),
            _ => new PlayGamesRestoreResult(status, 503),
        };
    }
}
