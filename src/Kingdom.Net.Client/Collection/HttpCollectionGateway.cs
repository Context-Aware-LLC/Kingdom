using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Collection
{
    /// <summary>
    /// What one <c>GET /collection</c> came back with. <b>Five outcomes, and the failures must not be
    /// collapsed into "no cards".</b>
    ///
    /// <para>Only <see cref="Synced"/> may ever write the cache, and only when
    /// <see cref="CollectionSnapshot.ShouldReplaceCache"/> says so. Every other outcome — offline, a
    /// rejected token, a server with no economy — has to leave whatever the device already had exactly
    /// as it is, because none of them is evidence about what the player owns.</para>
    /// </summary>
    public enum CollectionSyncStatus
    {
        /// <summary>The ledger was read. Either <see cref="CollectionSnapshot.Record"/> is the
        /// authoritative ownership record — <b>including when it lists nothing</b>, which means this
        /// player genuinely owns nothing — or <see cref="CollectionSnapshot.CardsUnchanged"/> says the
        /// record the device sent is still the truth.</summary>
        Synced,

        /// <summary>The request never completed. Transient, says nothing about ownership.</summary>
        Offline,

        /// <summary>401 — no token, or one the server refused.</summary>
        Unauthenticated,

        /// <summary>503 — the server has no account store or no ledger. A supported dev configuration,
        /// not an error the player can act on.</summary>
        Unavailable,

        /// <summary>A 2xx whose body could not be read. Treated as a failed fetch, never as a partial
        /// collection: half a collection cached as if it were whole is worse than none.</summary>
        Malformed,
    }

    /// <summary>The player's currencies, as of the same read as the cards.</summary>
    public readonly struct WalletSnapshot
    {
        public long Gold { get; }
        public long Gems { get; }
        public long Shards { get; }

        public WalletSnapshot(long gold, long gems, long shards)
        {
            Gold = gold;
            Gems = gems;
            Shards = shards;
        }
    }

    /// <summary>One fetch's result. <see cref="Record"/> is non-null only for
    /// <see cref="CollectionSyncStatus.Synced"/> when the server sent the cards.</summary>
    public sealed class CollectionSnapshot
    {
        public CollectionSyncStatus Status { get; }

        /// <summary>The ownership record the server sent, its digest verified against the body.
        /// Non-null <b>only</b> for <see cref="CollectionSyncStatus.Synced"/> with
        /// <see cref="CardsUnchanged"/> false. Null never means "owns nothing" — an empty list is a
        /// non-null record.</summary>
        public OwnershipRecord? Record { get; }

        /// <summary>The server confirmed the digest this device sent still matches, and so sent no
        /// cards. The device keeps the record it has.</summary>
        public bool CardsUnchanged { get; }

        /// <summary>
        /// Unopened packs this player holds, by pack line. Present on
        /// <see cref="CollectionSyncStatus.Synced"/> and empty on every failure, which is a statement
        /// about nothing rather than about an empty shelf.
        ///
        /// <para><b>This is the first-run routing input.</b> The gift routes on a starter balance
        /// above zero rather than on a local marker, which is what makes granted packs
        /// unstrandable: a player who opens two and quits lands back on the remaining three. See
        /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c> §4 slice 2.</para>
        /// </summary>
        public IReadOnlyDictionary<string, long> Packs { get; }

        /// <summary>Null means the server did not report cosmetic ownership. Never unlock backs
        /// from the gameplay-card ownership word, including for a superuser.</summary>
        public IReadOnlyDictionary<int, long>? CardBacks { get; }
        public bool OwnsCardBack(int id) => CardBacks != null && CardBacks.TryGetValue(id, out long count) && count > 0;

        public WalletSnapshot Wallet { get; }

        /// <summary>Diagnostic text for a log line. Never shown to a player.</summary>
        public string Detail { get; }

        private static readonly IReadOnlyDictionary<string, long> NoPacks =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private CollectionSnapshot(CollectionSyncStatus status, OwnershipRecord? record,
                                   bool cardsUnchanged, WalletSnapshot wallet, bool hasAuthoritativeWallet, string detail,
                                   IReadOnlyDictionary<string, long>? packs = null,
                                   IReadOnlyDictionary<int, long>? cardBacks = null)
        {
            Status = status;
            Record = record;
            CardsUnchanged = cardsUnchanged;
            Wallet = wallet;
            HasAuthoritativeWallet = hasAuthoritativeWallet;
            Detail = detail ?? "";
            Packs = packs ?? NoPacks;
            CardBacks = cardBacks;
        }

        public static CollectionSnapshot Synced(OwnershipRecord record, WalletSnapshot wallet,
                                                IReadOnlyDictionary<string, long>? packs = null,
                                                IReadOnlyDictionary<int, long>? cardBacks = null,
                                                bool hasAuthoritativeWallet = true)
            => new CollectionSnapshot(CollectionSyncStatus.Synced, record, false, wallet,
                hasAuthoritativeWallet, "", packs, cardBacks);

        /// <summary>The server's digest matched the device's: no cards were sent, no record rides
        /// along, and the cache must stay as it is.</summary>
        public static CollectionSnapshot SyncedUnchanged(WalletSnapshot wallet,
                                                         IReadOnlyDictionary<string, long>? packs = null,
                                                         IReadOnlyDictionary<int, long>? cardBacks = null,
                                                         bool hasAuthoritativeWallet = true)
            => new CollectionSnapshot(CollectionSyncStatus.Synced, null, true, wallet,
                hasAuthoritativeWallet, "", packs, cardBacks);

        public static CollectionSnapshot Failed(CollectionSyncStatus status, string detail)
            => new CollectionSnapshot(status, null, false, default, false, detail);

        /// <summary>How many unopened packs of one line this player holds; 0 when the line is absent.
        /// One accessor rather than a lookup at each call site, because the routing decision in
        /// <c>MainMenuController.BeginFirstRun</c> is "> 0" and a missing key is not a different
        /// answer from a zero balance.</summary>
        public long PackCount(string packId)
            => packId is not null && Packs.TryGetValue(packId, out long count) ? count : 0;

        /// <summary>True only when this result carries a record to write to the local cache. One
        /// property rather than a status comparison at each call site, because the wrong comparison
        /// here is exactly the bug that empties somebody's collection. False for
        /// <see cref="CardsUnchanged"/>: the device already holds that record.</summary>
        public bool ShouldReplaceCache => Status == CollectionSyncStatus.Synced && !CardsUnchanged;

        /// <summary>Whether this answer carries the endpoint's authoritative pack shelf. Pack
        /// authority is independent of wallet completeness so one partial projection does not force
        /// a second collection read or strand a valid starter-pack route.</summary>
        public bool HasAuthoritativePacks => Status == CollectionSyncStatus.Synced;

        /// <summary>
        /// Whether this answer carries a complete wallet projected from the entitlement ledger.
        /// Missing or malformed wallet fields leave this false even when ownership and packs remain
        /// usable; failure snapshots use a default-valued wallet only as inert storage.
        /// </summary>
        public bool HasAuthoritativeWallet { get; }
    }

    /// <summary>
    /// Reads <c>GET /collection</c> off the match server — the client half of the ledger read-down
    /// (spec <c>docs/dev-docs/specs/card-ownership.md</c>, slice 3).
    ///
    /// <para><b>What this makes true.</b> A player who uninstalls, reinstalls and signs back in has
    /// always reached the right identity — the entitlement ledger is keyed on the authenticated
    /// <c>player_id</c> and never lost a row. What was missing was any path from that ledger to the
    /// device: the collection screen read local disk, found nothing, and rendered the full pool. This
    /// is that path.</para>
    ///
    /// <para><b>It never throws and it never partially succeeds.</b> Every failure is a status the
    /// caller can branch on, because the caller's decision — overwrite the local record or leave it
    /// alone — is not recoverable if it goes the wrong way. A thrown exception on the sign-in path
    /// would also turn "the network is bad" into "the game did not start", which is the same rule
    /// <see cref="IAuthHttp"/> already states.</para>
    ///
    /// <para><b>Over <see cref="IAuthHttp"/> rather than <c>HttpClient</c>,</b> for the reason that
    /// seam exists at all: Unity supplies <c>UnityWebRequestAuthHttp</c>, which is the one transport
    /// that works on every platform we ship to, while the logic above it stays in the dotnet suite
    /// where the CI gate is.</para>
    /// </summary>
    public sealed class HttpCollectionGateway
    {
        /// <summary>Request header carrying the bare 64-hex digest of the device's ownership record.</summary>
        public const string DigestHeader = "Kingdom-Ownership-Digest";

        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        /// <param name="baseUrl">The match server's origin, e.g. <c>https://emberfall.fly.dev</c>.
        /// A trailing slash is tolerated.</param>
        public HttpCollectionGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        /// <summary>
        /// Fetch this identity's collection.
        ///
        /// <para><b>An empty <paramref name="accessToken"/> is Unauthenticated, not a request.</b>
        /// Sending the call anyway would ask the server to decide something the client already knows:
        /// with no session there is no account to read a collection for.</para>
        ///
        /// <para><paramref name="deviceDigest"/> is the digest of the ownership record the device
        /// holds, or null when it holds none. Sent as <c>Kingdom-Ownership-Digest</c> so the server
        /// can answer <c>cardsUnchanged</c> instead of resending the cards.</para>
        /// </summary>
        public async Task<CollectionSnapshot> FetchAsync(
            string accessToken, string? deviceDigest, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unavailable,
                    "No collection endpoint is configured.");
            if (string.IsNullOrEmpty(accessToken))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unauthenticated,
                    "Not signed in.");

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            if (!string.IsNullOrEmpty(deviceDigest))
                headers.Add(new KeyValuePair<string, string>(DigestHeader, deviceDigest!));

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/collection", headers, cancellationToken)
                .ConfigureAwait(false);

            // A Fly resume can leave the first authenticated read waiting on a cold dependency
            // until Unity's request timer fires. The server may finish warming immediately after
            // that abort; retry this idempotent GET once before declaring the collection offline.
            // Other transport failures (including no route) should not double the wait.
            if (response.IsTransportFailure && !cancellationToken.IsCancellationRequested &&
                (response.Body.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 response.Body.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                response = await _http
                    .GetAsync(_baseUrl + "/collection", headers, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (response.IsTransportFailure)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Offline, response.Body);

            if (response.StatusCode == 401)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unauthenticated, response.Body);

            // 503 is the ONLY status that means "this box has no economy" — the server chooses it
            // deliberately, for a configuration the player cannot act on. Every other failure is a
            // fault, and calling one Unavailable told Sam's client "this server has no pack economy"
            // while a 500 was losing him his onboarding gift (2026-09-04). Offline is the honest
            // answer: it says nothing about ownership, so it cannot overwrite the cache, and it reads
            // to the player as "try again", which is exactly what a 500 deserves.
            if (response.StatusCode == 503)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unavailable,
                    "Server returned 503");
            if (!response.IsSuccess)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Offline,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        /// <summary>
        /// Read the reply.
        ///
        /// <para><b>The ownership word, the rule and the digest are the authority, never the card
        /// count.</b> An absent or empty <c>cards</c> object is a legal answer meaning "owns
        /// nothing", and it is indistinguishable from a malformed body by shape alone — so a body
        /// without a known rule and a well-formed digest is Malformed rather than an empty
        /// collection.</para>
        ///
        /// <para><b>The digest is recomputed from the cards actually received</b> and must equal the
        /// one the server named. A proxy that rewrote the list must not get it cached under a digest
        /// the next request would then vouch for.</para>
        /// </summary>
        private static CollectionSnapshot Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "unreadable body");

            if (!AuthJson.TryString(fields, "ownership", out string ownership))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "no ownership field");

            if (!ownership.Equals("known", StringComparison.OrdinalIgnoreCase))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unavailable, ownership);

            bool hasAuthoritativeWallet = TryReadWallet(body, out WalletSnapshot wallet);

            // A body that answered with ownership but carries no `packs` member is MALFORMED, not a
            // player with an empty shelf — the two would route the first run differently, and the
            // gift is the thing that would go missing.
            if (!TryReadPacks(body, out IReadOnlyDictionary<string, long> packs))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "no packs field");

            if (!AuthJson.TryString(fields, "rule", out string rule))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "no rule field");
            bool isList = rule.Equals("list", StringComparison.Ordinal);
            bool isAllAtLimit = rule.Equals("all-at-limit", StringComparison.Ordinal);
            if (!isList && !isAllAtLimit)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "unknown rule " + rule);

            if (!AuthJson.TryString(fields, "digest", out string digest) ||
                !OwnershipDigest.IsWellFormed(digest))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "bad digest");

            if (!AuthJson.TryString(fields, "cardsUnchanged", out string unchangedText) ||
                !(unchangedText == "true" || unchangedText == "false"))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "no cardsUnchanged field");

            IReadOnlyDictionary<int, long>? cardBacks = null;
            if (AuthJson.TryReadNestedObject(body, "cardBacks", out var backRows))
            {
                var backs = new Dictionary<int, long>();
                foreach (var row in backRows)
                    if (int.TryParse(row.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                        && id > 0 && long.TryParse(row.Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long count) && count > 0)
                        backs[id] = count;
                cardBacks = backs;
            }

            if (unchangedText == "true")
                return CollectionSnapshot.SyncedUnchanged(wallet, packs, cardBacks, hasAuthoritativeWallet);

            if (isAllAtLimit)
            {
                if (!string.Equals(digest, OwnershipRecord.AllAtLimit.Digest, StringComparison.Ordinal))
                    return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed,
                        "all-at-limit digest mismatch");
                return CollectionSnapshot.Synced(OwnershipRecord.AllAtLimit, wallet, packs, cardBacks,
                    hasAuthoritativeWallet);
            }

            var cards = new OwnedCardCounts();
            if (AuthJson.TryReadNestedObject(body, "cards", out var rows))
            {
                foreach (KeyValuePair<string, string> row in rows)
                {
                    // Skipped, never thrown on — the same posture LedgerOwnedCounts takes server-side.
                    // A row written under a future item shape must not cost the player the rest of
                    // their collection.
                    if (!int.TryParse(row.Key, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                      out int cardId))
                        continue;
                    if (!long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                       out long copies))
                        continue;
                    if (copies <= 0) continue;

                    cards.Set(cardId, copies > int.MaxValue ? int.MaxValue : (int)copies);
                }
            }

            OwnershipRecord record = OwnershipRecord.List(cards);
            if (!string.Equals(digest, record.Digest, StringComparison.Ordinal))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "list digest mismatch");

            return CollectionSnapshot.Synced(record, wallet, packs, cardBacks, hasAuthoritativeWallet);
        }

        /// <summary>
        /// The <c>packs</c> object: pack line → unopened balance. False only when the member is
        /// absent or is not an object at all — which is the Malformed case, not an empty shelf.
        ///
        /// <para>An empty object is a legal, meaningful answer: this player has no unopened packs.
        /// Individual rows that do not parse are skipped rather than failing the read, the same
        /// posture the card rows take — a line written under a future item shape must not cost the
        /// player the packs they can actually open.</para>
        /// </summary>
        private static bool TryReadPacks(string body, out IReadOnlyDictionary<string, long> packs)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            packs = map;

            if (!AuthJson.TryReadNestedObject(body, "packs", out var rows)) return false;

            foreach (KeyValuePair<string, string> row in rows)
            {
                if (!long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                   out long count))
                    continue;
                if (count <= 0) continue;
                map[row.Key] = count;
            }

            return true;
        }

        /// <summary>
        /// Read the complete wallet projection. Missing or malformed fields make the wallet
        /// non-authoritative without discarding an otherwise valid ownership or pack answer.
        /// </summary>
        private static bool TryReadWallet(string body, out WalletSnapshot wallet)
        {
            wallet = default;
            if (!AuthJson.TryReadNestedObject(body, "wallet", out var w))
                return false;

            if (!AuthJson.TryInt64(w, "gold", out long gold) ||
                !AuthJson.TryInt64(w, "gems", out long gems) ||
                !AuthJson.TryInt64(w, "shards", out long shards))
                return false;

            wallet = new WalletSnapshot(gold, gems, shards);
            return true;
        }
    }
}
