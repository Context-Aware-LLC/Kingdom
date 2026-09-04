using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Auth;
using Emberfall.Net.Client.Shop;

namespace Emberfall.Net.Client.Collection
{
    /// <summary>
    /// What one <c>POST /packs/open</c> came back with. Same vocabulary as
    /// <see cref="RosterWriteStatus"/> where the meanings coincide, because a caller that has learned
    /// one of these enums should not have to learn a second dialect of it.
    /// </summary>
    public enum PackOpenStatus
    {
        /// <summary>The server opened a pack. <see cref="PackOpenResult.Receipt"/> is what was in
        /// it.</summary>
        Opened,

        /// <summary>The server had already opened this one — a retried tap, or a duplicated request.
        /// The receipt carries the SAME cards, and nothing was spent twice. A caller shows them
        /// exactly as it would an <see cref="Opened"/>; the two are separate only so a log line can
        /// tell them apart.</summary>
        AlreadyOpened,

        /// <summary>409 — the player holds no unopened pack on this line. Reachable honestly: another
        /// device opened the last one, or this client's shelf is stale.</summary>
        NoneOwned,

        /// <summary>400 — a pack line this server does not know. A CLIENT bug; retrying cannot fix
        /// it.</summary>
        UnknownPack,

        /// <summary>The request never completed. Says nothing about whether a pack was opened — see
        /// the gateway remarks, this is the one status a caller must not treat as "nothing
        /// happened".</summary>
        Offline,

        /// <summary>401 — no token, or one the server refused.</summary>
        Unauthenticated,

        /// <summary>503 — this server has no economy, or no pack seed secret, so no pack can be
        /// opened on it at all. A supported dev configuration.</summary>
        Unavailable,

        /// <summary>A 2xx whose body could not be read, or one that answered <c>opened</c> with no
        /// awards. Never rendered as an empty pack: a reveal of nothing is worse than an error, and
        /// the cards may well have been granted.</summary>
        Malformed,
    }

    /// <summary>One open's result. <see cref="Receipt"/> is non-null only for
    /// <see cref="PackOpenStatus.Opened"/> and <see cref="PackOpenStatus.AlreadyOpened"/>.</summary>
    public sealed class PackOpenResult
    {
        public PackOpenStatus Status { get; }

        /// <summary>The server-authored reveal. Non-null only on the two success statuses.</summary>
        public PackOpenReceipt? Receipt { get; }

        /// <summary>Diagnostic text for a log line. Never shown to a player.</summary>
        public string Detail { get; }

        private PackOpenResult(PackOpenStatus status, PackOpenReceipt? receipt, string detail)
        {
            Status = status;
            Receipt = receipt;
            Detail = detail ?? "";
        }

        public static PackOpenResult Success(PackOpenStatus status, PackOpenReceipt receipt)
            => new PackOpenResult(status, receipt, "");

        public static PackOpenResult Failed(PackOpenStatus status, string detail)
            => new PackOpenResult(status, null, detail);

        /// <summary>True when there is a reveal to play. One property rather than a two-way status
        /// comparison at each call site.</summary>
        public bool HasReveal => Receipt is not null;
    }

    /// <summary>
    /// Opens packs the player already owns — the client half of slice 0
    /// (<c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>).
    ///
    /// <para><b>The request names a pack LINE and nothing else.</b> There is no instance id, no card
    /// count and no odds on the wire, by design: the server mints the idempotency anchor and decides
    /// what shape a given entitlement opens as. A client that could name its own instance id could
    /// replay one pack into unbounded opens; a client that could name the shape could spend a retail
    /// pack at the starter's boosted odds. See <c>PackOpenEndpoint</c> for both.</para>
    ///
    /// <para><b><see cref="PackOpenStatus.Offline"/> does not mean the pack was not opened.</b> The
    /// open is committed server-side before the response is written, so a dropped reply leaves a pack
    /// genuinely spent and a client that saw nothing. <b>The recovery is to re-read
    /// <c>GET /collection</c>, never to retry the open</b> — a retry derives the next instance id and
    /// spends a second pack. A caller that treats this as "try again" turns one lost response into
    /// one lost pack.</para>
    ///
    /// <para><b>It never throws.</b> Same contract as <see cref="IAuthHttp"/> and every other gateway
    /// here: a failure is a status, because this runs on the first-run path where a thrown exception
    /// is a game that did not start.</para>
    /// </summary>
    public sealed class HttpPackGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        /// <param name="baseUrl">The match server's origin. A trailing slash is tolerated.</param>
        public HttpPackGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        /// <summary>Open one pack on the named line.</summary>
        public async Task<PackOpenResult> OpenAsync(
            string packId, string accessToken, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return PackOpenResult.Failed(PackOpenStatus.Unavailable,
                    "No pack endpoint is configured.");
            if (string.IsNullOrEmpty(accessToken))
                return PackOpenResult.Failed(PackOpenStatus.Unauthenticated, "Not signed in.");
            if (string.IsNullOrWhiteSpace(packId))
                return PackOpenResult.Failed(PackOpenStatus.UnknownPack, "No pack id.");

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

            string body = AuthJson.Object(AuthJson.Field("packId", packId));

            AuthHttpResponse response = await _http
                .PostJsonAsync(_baseUrl + "/packs/open", body, headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return PackOpenResult.Failed(PackOpenStatus.Offline, response.Body);

            if (response.StatusCode == 401)
                return PackOpenResult.Failed(PackOpenStatus.Unauthenticated, response.Body);
            if (response.StatusCode == 400)
                return PackOpenResult.Failed(PackOpenStatus.UnknownPack, response.Body);
            if (response.StatusCode == 409)
                return Parse409(response.Body);
            // 503 is the server saying it has no pack economy at all — a configuration, not a fault.
            // Any other failure is a fault, and it must NOT be reported as "packs cannot be opened on
            // this server": the open may well have committed before the response died, which is
            // precisely what Offline means here. Same correction as HttpCollectionGateway's.
            if (response.StatusCode == 503)
                return PackOpenResult.Failed(PackOpenStatus.Unavailable, "Server returned 503");
            if (!response.IsSuccess)
                return PackOpenResult.Failed(PackOpenStatus.Offline,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        /// <summary>
        /// A 409 is two different server states — <c>noneowned</c>, an ordinary player state, and
        /// <c>conflict</c>, a ledger defect — and <b>one client outcome</b>: there is no pack to
        /// reveal and no retry that would produce one. So they share a status and differ in the
        /// detail, which is what a bug report is read out of. Giving the defect its own client status
        /// would ask every caller to branch on something it cannot do anything about; folding it into
        /// the same DETAIL as "you have no packs" would hide it entirely.
        /// </summary>
        private static PackOpenResult Parse409(string body)
        {
            bool conflict =
                AuthJson.TryReadTopLevel(body, out var fields) &&
                AuthJson.TryString(fields, "status", out string status) &&
                status.Equals("conflict", StringComparison.OrdinalIgnoreCase);

            return PackOpenResult.Failed(PackOpenStatus.NoneOwned,
                conflict ? "ledger conflict: " + body : body);
        }

        /// <summary>
        /// Read the receipt.
        ///
        /// <para><b>The status word is the authority, and an empty award list is never a pack.</b> A
        /// 200 that decodes to zero cards would animate an empty reveal over an entitlement that has
        /// already been spent, which is the one outcome a player cannot recover from or understand.
        /// It is <see cref="PackOpenStatus.Malformed"/>, and the caller's answer to that is to re-read
        /// the collection.</para>
        /// </summary>
        private static PackOpenResult Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields))
                return PackOpenResult.Failed(PackOpenStatus.Malformed, "unreadable body");

            if (!AuthJson.TryString(fields, "status", out string status))
                return PackOpenResult.Failed(PackOpenStatus.Malformed, "no status field");

            PackOpenStatus opened;
            if (status.Equals("opened", StringComparison.OrdinalIgnoreCase))
                opened = PackOpenStatus.Opened;
            else if (status.Equals("alreadyopened", StringComparison.OrdinalIgnoreCase))
                opened = PackOpenStatus.AlreadyOpened;
            else
                return PackOpenResult.Failed(PackOpenStatus.Malformed, status);

            if (!AuthJson.TryString(fields, "packId", out string packId) ||
                !AuthJson.TryString(fields, "packInstanceId", out string packInstanceId))
                return PackOpenResult.Failed(PackOpenStatus.Malformed, "no pack identity");

            if (!AuthJson.TryReadObjectArray(body, "awards", out var rows))
                return PackOpenResult.Failed(PackOpenStatus.Malformed, "no awards array");

            var awards = new List<PackCardAward>(rows.Count);
            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                if (!AuthJson.TryInt64(row, "cardId", out long cardId) || cardId <= 0 ||
                    cardId > int.MaxValue)
                    return PackOpenResult.Failed(PackOpenStatus.Malformed, "bad award card id");

                AuthJson.TryString(row, "rarity", out string rarityText);
                AuthJson.TryString(row, "upgrade", out string upgradeText);

                awards.Add(new PackCardAward((int)cardId, ParseRarity(rarityText),
                                             ParseUpgrade(upgradeText)));
            }

            if (awards.Count == 0)
                return PackOpenResult.Failed(PackOpenStatus.Malformed, "empty pack");

            return PackOpenResult.Success(opened,
                new PackOpenReceipt(packId, packInstanceId, awards));
        }

        /// <summary>
        /// A rarity the server named. An unrecognised word becomes <see cref="Rarity.COMMON"/> rather
        /// than failing the whole open — the CARD is the entitlement and it is already granted; the
        /// rarity only decides which colour the reveal flashes, and losing a pack over a spelling is
        /// the worse trade.
        /// </summary>
        private static Rarity ParseRarity(string text)
            => Enum.TryParse(text, ignoreCase: true, out Rarity rarity) ? rarity : Rarity.COMMON;

        /// <summary>Which guarantee, if any, the server says fired. Reveal chrome only, and always
        /// <see cref="PackUpgradeReason.None"/> on a replay — the ledger does not record it.</summary>
        private static PackUpgradeReason ParseUpgrade(string text)
            => Enum.TryParse(text, ignoreCase: true, out PackUpgradeReason upgrade)
                ? upgrade
                : PackUpgradeReason.None;
    }
}
