using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Social;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The game's one <see cref="HttpFriendsGateway"/> (#409). Composed on first touch like
    /// <see cref="CollectionGateway"/>, against the same host, and dropped with it on an account
    /// switch.
    ///
    /// <para>Nothing here is cached and nothing is written to disk: every call answers the player's
    /// whole friends graph as the server holds it, and the screen redraws from that.</para>
    /// </summary>
    public static class FriendsGateway
    {
        private const int TimeoutSeconds = 15;

        private static HttpFriendsGateway _gateway;

        private static HttpFriendsGateway Gateway =>
            _gateway ??= new HttpFriendsGateway(CollectionGateway.BaseUrl,
                                                new UnityWebRequestAuthHttp(TimeoutSeconds));

        /// <summary>Drop the cached gateway. Called from <see cref="DeviceAccounts.Invalidate"/>.</summary>
        public static void Invalidate()
        {
            _gateway = null;
            _playGames = null;
            // Another account on this device has to claim the Play Games id for itself.
            PlayGamesServices.ForgetLink();
        }

        public static Task<FriendsSnapshot> FetchAsync(string accessToken, CancellationToken ct = default)
            => Guard(g => g.FetchAsync(accessToken, ct));

        public static Task<FriendsSnapshot> RequestAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.RequestAsync(accessToken, code, ct));

        public static Task<FriendsSnapshot> AcceptAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.AcceptAsync(accessToken, code, ct));

        /// <summary>Refuse a request, or withdraw one this player sent.</summary>
        public static Task<FriendsSnapshot> DeclineAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.DeclineAsync(accessToken, code, ct));

        public static Task<FriendsSnapshot> RemoveAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.RemoveAsync(accessToken, code, ct));

        /// <summary>Block a code. Ends any friendship or request with it, both ways.</summary>
        public static Task<FriendsSnapshot> BlockAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.BlockAsync(accessToken, code, ct));

        public static Task<FriendsSnapshot> UnblockAsync(string accessToken, string code, CancellationToken ct = default)
            => Guard(g => g.UnblockAsync(accessToken, code, ct));

        private static HttpPlayGamesFriendsGateway _playGames;

        private static HttpPlayGamesFriendsGateway PlayGames =>
            _playGames ??= new HttpPlayGamesFriendsGateway(CollectionGateway.BaseUrl,
                                                           new UnityWebRequestAuthHttp(TimeoutSeconds));

        /// <summary>Make this player findable by their Play Games friends (#423). Takes a server
        /// auth code from <see cref="PlayGamesServices"/>, never an id.</summary>
        public static async Task<PlayGamesFriendsStatus> LinkPlayGamesAsync(string accessToken, string authCode)
        {
            try { return await PlayGames.LinkAsync(accessToken, authCode); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Friends] Play Games link threw ({ex.GetType().Name}: {ex.Message}).");
                return PlayGamesFriendsStatus.Offline;
            }
        }

        /// <summary>This player's Play Games friends who play Bad Ideas (#423).</summary>
        public static async Task<PlayGamesFriendsResult> FindPlayGamesFriendsAsync(string accessToken, string authCode)
        {
            try { return await PlayGames.FindAsync(accessToken, authCode); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Friends] Play Games friends threw ({ex.GetType().Name}: {ex.Message}).");
                return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Offline);
            }
        }

        /// <summary>The gateway is written not to throw, and a screen must not be the place that
        /// finds out it was wrong about that.</summary>
        private static async Task<FriendsSnapshot> Guard(Func<HttpFriendsGateway, Task<FriendsSnapshot>> call)
        {
            try
            {
                return await call(Gateway);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Friends] call threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "reporting it as offline.");
                return FriendsSnapshot.Failed(FriendsCallStatus.Offline);
            }
        }
    }
}
