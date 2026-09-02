using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Auth;

namespace Emberfall.Net.Client.Collection
{
    /// <summary>
    /// What one <c>GET /collection</c> came back with. <b>Six outcomes, and three of them must not be
    /// collapsed into "no cards".</b>
    ///
    /// <para>The distinction this enum exists to carry is the one from <see cref="IOwnedCardStore"/>:
    /// an <i>absent</i> ownership record means "unknown, do not constrain" and shows the full pool,
    /// while an <i>empty</i> one asserts the player owns nothing and turns every deck into a draft.
    /// Only <see cref="Synced"/> may ever write the cache. Every other outcome — offline, a rejected
    /// token, a server with no economy — has to leave whatever the device already had exactly as it
    /// is, because none of them is evidence about what the player owns.</para>
    /// </summary>
    public enum CollectionSyncStatus
    {
        /// <summary>The ledger was read. <see cref="CollectionSnapshot.Cards"/> is authoritative —
        /// <b>including when it is empty</b>, which means this player genuinely owns nothing.</summary>
        Synced,

        /// <summary>A superuser: never meant to be constrained, and there is no list to cache. The
        /// device must be left with no record, which is what produces the full pool.</summary>
        Unconstrained,

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

    /// <summary>One fetch's result. <see cref="Cards"/> is non-null only for
    /// <see cref="CollectionSyncStatus.Synced"/>.</summary>
    public sealed class CollectionSnapshot
    {
        public CollectionSyncStatus Status { get; }

        /// <summary>Non-null <b>only</b> for <see cref="CollectionSyncStatus.Synced"/>. Null here is
        /// the same statement the local record's absence makes: ownership is unknown, do not
        /// constrain — never "owns nothing".</summary>
        public OwnedCardCounts? Cards { get; }

        public WalletSnapshot Wallet { get; }

        /// <summary>Diagnostic text for a log line. Never shown to a player.</summary>
        public string Detail { get; }

        private CollectionSnapshot(CollectionSyncStatus status, OwnedCardCounts? cards,
                                   WalletSnapshot wallet, string detail)
        {
            Status = status;
            Cards = cards;
            Wallet = wallet;
            Detail = detail ?? "";
        }

        public static CollectionSnapshot Synced(OwnedCardCounts cards, WalletSnapshot wallet)
            => new CollectionSnapshot(CollectionSyncStatus.Synced, cards, wallet, "");

        public static CollectionSnapshot Unconstrained(WalletSnapshot wallet)
            => new CollectionSnapshot(CollectionSyncStatus.Unconstrained, null, wallet, "");

        public static CollectionSnapshot Failed(CollectionSyncStatus status, string detail)
            => new CollectionSnapshot(status, null, default, detail);

        /// <summary>True only when this result may be written to the local cache. One property rather
        /// than a status comparison at each call site, because the wrong comparison here is exactly
        /// the bug that empties somebody's collection.</summary>
        public bool ShouldReplaceCache => Status == CollectionSyncStatus.Synced;
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
        /// Sending the call anyway would ask the server to decide something the client already knows,
        /// and a guest has no collection to read by construction.</para>
        /// </summary>
        public async Task<CollectionSnapshot> FetchAsync(
            string accessToken, CancellationToken cancellationToken = default)
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

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/collection", headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Offline, response.Body);

            if (response.StatusCode == 401)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unauthenticated, response.Body);
            if (!response.IsSuccess)
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unavailable,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        /// <summary>
        /// Read the reply.
        ///
        /// <para><b>The ownership word is the authority, never the card count.</b> An absent or empty
        /// <c>cards</c> object is a legal Synced answer meaning "owns nothing", and it is
        /// indistinguishable from a malformed body by shape alone — so the branch is taken on the
        /// string the server chose, and a body without one is Malformed rather than an empty
        /// collection.</para>
        /// </summary>
        private static CollectionSnapshot Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "unreadable body");

            if (!AuthJson.TryString(fields, "ownership", out string ownership))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Malformed, "no ownership field");

            WalletSnapshot wallet = ReadWallet(body);

            if (ownership.Equals("unconstrained", StringComparison.OrdinalIgnoreCase))
                return CollectionSnapshot.Unconstrained(wallet);

            if (!ownership.Equals("known", StringComparison.OrdinalIgnoreCase))
                return CollectionSnapshot.Failed(CollectionSyncStatus.Unavailable, ownership);

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

            return CollectionSnapshot.Synced(cards, wallet);
        }

        private static WalletSnapshot ReadWallet(string body)
        {
            if (!AuthJson.TryReadNestedObject(body, "wallet", out var w))
                return default;

            AuthJson.TryInt64(w, "gold", out long gold);
            AuthJson.TryInt64(w, "gems", out long gems);
            AuthJson.TryInt64(w, "shards", out long shards);
            return new WalletSnapshot(gold, gems, shards);
        }
    }
}
