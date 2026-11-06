using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Collection;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The game's one <see cref="HttpPackGateway"/> — the verb half of pack opening, shaped like
    /// <see cref="RosterGateway"/> and <see cref="CollectionGateway"/> so a screen that has learned one
    /// of them has learned this.
    ///
    /// <para><b>The shelf is read through <see cref="CollectionGateway.FetchAsync"/>, not from here.</b>
    /// A pack balance arrives on <c>GET /collection</c> alongside the cards and the wallet, so there is
    /// one route and one owner of the transport for it. <see cref="ReadShelfAsync"/> is a named
    /// forward rather than a second gateway, because a second <c>HttpCollectionGateway</c> would be a
    /// second thing to invalidate on an account switch and only the first is wired to
    /// <see cref="DeviceAccounts.Invalidate"/>.</para>
    ///
    /// <para><b><see cref="PackOpenStatus.Offline"/> is not "nothing happened", and this is the layer
    /// that must not paper over it.</b> The server commits the open before it writes the response, so
    /// a dropped reply leaves a pack genuinely spent. <b>Never retry an open.</b> Re-read the shelf:
    /// a retry derives the next instance id and spends a second pack. There is deliberately no
    /// convenience wrapper here that would make retrying easy.</para>
    ///
    /// <para><b>Nothing here rolls anything</b> (ADR-0004). A receipt is server-authored; the client
    /// asks and presents.</para>
    /// </summary>
    public static class PackGateway
    {
        /// <summary>The match server's origin. Tracks <see cref="CollectionGateway.BaseUrl"/> for the
        /// same reason <see cref="RosterGateway.BaseUrl"/> does — reading a shelf off one server and
        /// opening a pack against another is not a state worth being able to express.</summary>
        public static string BaseUrl
        {
            get => CollectionGateway.BaseUrl;
            set => CollectionGateway.BaseUrl = value;
        }

        /// <summary>How long an open will wait. Longer than the sign-in syncs' 15s on purpose: those
        /// can be given up on and retried at no cost, and this one cannot — a timeout here is a pack
        /// that may already be spent, so the budget buys the answer rather than the speed.</summary>
        private const int TimeoutSeconds = 30;

        private static HttpPackGateway _gateway;
        private static string _gatewayBaseUrl;

        private static HttpPackGateway Gateway
        {
            get
            {
                // Rebuilt when BaseUrl moves, because it is settable and a cached gateway would keep
                // talking to the old host with nothing to show for it.
                if (_gateway == null || !string.Equals(_gatewayBaseUrl, BaseUrl, StringComparison.Ordinal))
                {
                    _gatewayBaseUrl = BaseUrl;
                    _gateway = new HttpPackGateway(BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));
                }
                return _gateway;
            }
        }

        /// <summary>Drop the cached gateway. Called from <see cref="DeviceAccounts.Invalidate"/>
        /// alongside the other three.</summary>
        public static void Invalidate()
        {
            _gateway = null;
            _gatewayBaseUrl = null;
        }

        /// <summary>
        /// What this identity holds, unopened, by pack line — plus the cards and wallet that come with
        /// it, which a caller is free to ignore.
        ///
        /// <para>Read live every time. There is no cache: a pack balance changes on another device the
        /// moment that device opens one, and a stale shelf offers a pack that is already gone.</para>
        /// </summary>
        public static Task<CollectionSnapshot> ReadShelfAsync(
            string accessToken, CancellationToken cancellationToken = default)
            => CollectionGateway.FetchAsync(accessToken, deviceDigest: null, cancellationToken);

        /// <summary>
        /// Open one pack on the named line.
        ///
        /// <para><b>The caller names a pack LINE, never an instance.</b> The server mints the
        /// idempotency anchor; a client that could choose it could replay one entitlement into
        /// unbounded opens. See <c>PackOpenEndpoint</c>.</para>
        ///
        /// <para>Never throws. A failure is a <see cref="PackOpenStatus"/>, because this runs on the
        /// first-run path and a thrown exception there is a game that did not start.</para>
        /// </summary>
        public static async Task<PackOpenResult> OpenAsync(
            string packId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                return await Gateway.OpenAsync(packId, accessToken, cancellationToken);
            }
            catch (Exception ex)
            {
                // The gateway is written not to throw, and this is not the place to find out it was
                // wrong. Offline rather than Malformed: a throw says nothing about whether the server
                // committed, which is exactly what Offline means here.
                Debug.LogWarning($"[Packs] open threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "treating it as a lost response — re-read the shelf, do not retry.");
                return PackOpenResult.Failed(PackOpenStatus.Offline,
                                             $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Open a bounded stack in one request. Like <see cref="OpenAsync"/>, a lost reply
        /// is never retried because the server may already have committed every receipt.</summary>
        public static async Task<PackOpenBatchResult> OpenManyAsync(
            string packId, int count, string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await Gateway.OpenManyAsync(packId, count, accessToken, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Packs] batch open threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "treating it as a lost response — re-read the shelf, do not retry.");
                return PackOpenBatchResult.Failed(PackOpenStatus.Offline,
                                                  $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
