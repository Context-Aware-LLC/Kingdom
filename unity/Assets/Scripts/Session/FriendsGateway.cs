using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client.Social;
using UnityEngine;

namespace Emberfall.Session
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
        public static void Invalidate() => _gateway = null;

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
