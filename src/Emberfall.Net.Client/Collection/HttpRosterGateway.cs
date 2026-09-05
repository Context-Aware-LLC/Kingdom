using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Auth;

namespace Emberfall.Net.Client.Collection
{
    /// <summary>
    /// What one <c>GET /roster</c> came back with. <b>Only <see cref="Known"/> carries a roster, and
    /// only <see cref="Known"/> may replace what is on the device.</b>
    ///
    /// <para>This is the same distinction <see cref="CollectionSyncStatus"/> exists to carry, and it
    /// is sharper here: an empty deck list is the shape of a brand new player, so a failed read
    /// that decoded as "no decks" would walk a player who owns forty cards through deck creation
    /// a second time. The server refuses to make that mistake possible — it OMITS the
    /// <c>decks</c> field entirely on anything but <c>known</c> — and this enum is the client
    /// half of the same guarantee.</para>
    /// </summary>
    public enum RosterSyncStatus
    {
        /// <summary>The roster was read. <see cref="RosterSnapshot.Decks"/> is authoritative —
        /// <b>including when it is empty</b>, which means this player genuinely has no decks
        /// yet.</summary>
        Known,

        /// <summary>The request never completed. Transient, says nothing about the roster.</summary>
        Offline,

        /// <summary>401 — no token, or one the server refused.</summary>
        Unauthenticated,

        /// <summary>503 — the server has no account store or no player data store. A supported dev
        /// configuration, not an error the player can act on.</summary>
        Unavailable,

        /// <summary>A 2xx whose body could not be read, or a <c>known</c> whose roster would not
        /// parse. Treated as a failed fetch, never as a partial roster: half a roster written over
        /// the local one destroys the half that did not arrive.</summary>
        Malformed,
    }

    /// <summary>What one roster WRITE came back with — a character create or delete, a deck upsert or
    /// delete. Deliberately one enum for all four, because every call site treats them the same way:
    /// the local write already happened, and this says whether the server agrees.</summary>
    public enum RosterWriteStatus
    {
        /// <summary>The server stored (or removed) it. 2xx.</summary>
        Ok,

        /// <summary>404 on a delete — the server had no such row. Not an error at a call site that
        /// just deleted the local copy: the two records now agree, which is the goal.</summary>
        NotFound,

        /// <summary>The request never completed. The local write stands and the server does not know
        /// about it yet.</summary>
        Offline,

        /// <summary>401 — no token, or one the server refused.</summary>
        Unauthenticated,

        /// <summary>503 — the server cannot store player data right now.</summary>
        Unavailable,

        /// <summary>400 — an id the server will not accept, or a body it could not read. A CLIENT
        /// bug, and the one status here that will not fix itself by retrying.</summary>
        Rejected,

        /// <summary>409 — the id already belongs to a different account. Also a client bug, and the
        /// one that must never be reported as a successful save.</summary>
        Conflict,
    }

    /// <summary>One <c>GET /roster</c> result. The two lists are non-null only for
    /// <see cref="RosterSyncStatus.Known"/>.</summary>
    public sealed class RosterSnapshot
    {
        public RosterSyncStatus Status { get; }

        /// <summary>Non-null <b>only</b> for <see cref="RosterSyncStatus.Known"/>. Null is "I could
        /// not find out", never "this player has no decks".</summary>
        public IReadOnlyList<DeckList>? Decks { get; }

        /// <summary>Diagnostic text for a log line. Never shown to a player.</summary>
        public string Detail { get; }

        private RosterSnapshot(RosterSyncStatus status, IReadOnlyList<DeckList>? decks, string detail)
        {
            Status = status;
            Decks = decks;
            Detail = detail ?? "";
        }

        public static RosterSnapshot Known(IReadOnlyList<DeckList> decks)
            => new RosterSnapshot(RosterSyncStatus.Known, decks, "");

        public static RosterSnapshot Failed(RosterSyncStatus status, string detail)
            => new RosterSnapshot(status, null, detail);

        /// <summary>True only when this result may replace the device's roster. One property rather
        /// than a status comparison at each call site, because the wrong comparison here is the bug
        /// that deletes somebody's decks.</summary>
        public bool ShouldReplaceLocal => Status == RosterSyncStatus.Known;
    }

    /// <summary>
    /// Decks over HTTP — the client half of the login handshake's roster routes
    /// (spec <c>docs/dev-docs/specs/login-handshake.md</c>).
    ///
    /// <para><b>What this makes true.</b> Card ownership already survived a reinstall: the
    /// entitlement ledger is keyed on the authenticated <c>player_id</c> and
    /// <see cref="HttpCollectionGateway"/> reads it down. The roster did not — decks
    /// lived only in <c>decks.json</c> under the local scope, which the OS
    /// deletes with the app. This is the wire that fixes that, in both directions: <see cref="FetchAsync"/>
    /// reads the server's copy down, and the four write calls keep it current.</para>
    ///
    /// <para><b>The server is the record; the local files are a cache.</b> There is no offline mode,
    /// so there is no merge, no tombstone and no last-write-wins — the client writes locally and
    /// pushes, and a fetch replaces. That only holds because a fetch that failed is never mistaken
    /// for a fetch that found nothing, which is what <see cref="RosterSyncStatus"/> is for.</para>
    ///
    /// <para><b>It never throws and it never partially succeeds</b>, the same contract as
    /// <see cref="HttpCollectionGateway"/> and for the same reason: these calls sit on the sign-in
    /// path, and a thrown exception there turns "the network is bad" into "the game did not
    /// start".</para>
    /// </summary>
    public sealed class HttpRosterGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        /// <param name="baseUrl">The match server's origin, e.g. <c>https://emberfall.fly.dev</c>.
        /// A trailing slash is tolerated.</param>
        public HttpRosterGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        // ── Read ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch this identity's decks.
        ///
        /// <para><b>An empty <paramref name="accessToken"/> is Unauthenticated, not a request</b> —
        /// the same rule as <see cref="HttpCollectionGateway.FetchAsync"/>.</para>
        /// </summary>
        public async Task<RosterSnapshot> FetchAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return RosterSnapshot.Failed(RosterSyncStatus.Unavailable, "No roster endpoint is configured.");
            if (string.IsNullOrEmpty(accessToken))
                return RosterSnapshot.Failed(RosterSyncStatus.Unauthenticated, "Not signed in.");

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/roster", Headers(accessToken), cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return RosterSnapshot.Failed(RosterSyncStatus.Offline, response.Body);
            if (response.StatusCode == 401)
                return RosterSnapshot.Failed(RosterSyncStatus.Unauthenticated, response.Body);
            if (!response.IsSuccess)
                return RosterSnapshot.Failed(RosterSyncStatus.Unavailable,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        /// <summary>
        /// Read the reply.
        ///
        /// <para><b>The status word is the authority, never the field's presence.</b> The server
        /// omits <c>decks</c> on anything but <c>known</c> precisely so an absent field cannot be
        /// misread as an empty one — but a client that branched on presence would re-introduce the
        /// confusion the omission exists to prevent, because a <c>known</c> roster with genuinely
        /// no decks is a legal answer that looks identical.
        /// So: branch on the word, and then require the fields.</para>
        /// </summary>
        private static RosterSnapshot Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields))
                return RosterSnapshot.Failed(RosterSyncStatus.Malformed, "unreadable body");

            if (!AuthJson.TryString(fields, "status", out string status))
                return RosterSnapshot.Failed(RosterSyncStatus.Malformed, "no status field");

            if (status.Equals("unauthenticated", StringComparison.OrdinalIgnoreCase))
                return RosterSnapshot.Failed(RosterSyncStatus.Unauthenticated, status);
            if (!status.Equals("known", StringComparison.OrdinalIgnoreCase))
                return RosterSnapshot.Failed(RosterSyncStatus.Unavailable, status);

            // Under "known" the field is promised. A body claiming known without it is MALFORMED,
            // not an empty roster — treating it as empty is the exact failure the whole handshake
            // exists to prevent, arriving through a shape rather than through a status.
            if (!AuthJson.TryString(fields, "decks", out string decksJson))
                return RosterSnapshot.Failed(RosterSyncStatus.Malformed,
                    "status was known but the decks field was absent");

            try
            {
                IReadOnlyList<DeckList> decks = CollectionJson.ReadDecks(decksJson);
                return RosterSnapshot.Known(decks);
            }
            catch (CollectionFormatException ex)
            {
                // Half a roster is worse than none: the local copy is the only other one, and it is
                // about to be replaced by whatever this returns.
                return RosterSnapshot.Failed(RosterSyncStatus.Malformed, ex.Message);
            }
        }

        // ── Writes ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>PUT /decks/{id}</c> — an upsert.
        ///
        /// <para><b>An illegal deck is still sent.</b> Drafts are a legitimate named state and
        /// validation is a queue-time concern; a client that withheld a deck it judged illegal would
        /// silently stop backing up the player's work in progress.</para>
        /// </summary>
        public async Task<RosterWriteStatus> SaveDeckAsync(
            string accessToken, DeckList deck, CancellationToken cancellationToken = default)
        {
            if (deck is null) throw new ArgumentNullException(nameof(deck));

            if (!IsConfigured) return RosterWriteStatus.Unavailable;
            if (string.IsNullOrEmpty(accessToken)) return RosterWriteStatus.Unauthenticated;
            if (string.IsNullOrWhiteSpace(deck.Id)) return RosterWriteStatus.Rejected;

            // The route id is the server's single authority for which deck is written, and the body
            // is the same shape the store keeps — CollectionJson on both sides of the wire, so
            // nothing about a deck has to be re-specified here. WriteDecks emits an ARRAY; the
            // endpoint reads element 0.
            string body = CollectionJson.WriteDecks(new[] { deck });

            AuthHttpResponse response = await _http
                .PutJsonAsync(_baseUrl + "/decks/" + Uri.EscapeDataString(deck.Id), body,
                              Headers(accessToken), cancellationToken)
                .ConfigureAwait(false);

            return Classify(response);
        }

        /// <summary><c>DELETE /decks/{id}</c>.</summary>
        public async Task<RosterWriteStatus> DeleteDeckAsync(
            string accessToken, string deckId, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured) return RosterWriteStatus.Unavailable;
            if (string.IsNullOrEmpty(accessToken)) return RosterWriteStatus.Unauthenticated;
            if (string.IsNullOrWhiteSpace(deckId)) return RosterWriteStatus.Rejected;

            AuthHttpResponse response = await _http
                .DeleteAsync(_baseUrl + "/decks/" + Uri.EscapeDataString(deckId),
                             Headers(accessToken), cancellationToken)
                .ConfigureAwait(false);

            return Classify(response);
        }

        // ── Shared ──────────────────────────────────────────────────────────────────────

        /// <summary>Commit a new deck to the server before exposing it in the device cache.
        /// Retry with the same deck id when a response is lost; PUT is an upsert.</summary>
        public async Task<RosterWriteStatus> SaveDeckAndCacheAsync(
            string accessToken, DeckList deck, IDeckStore cache,
            CancellationToken cancellationToken = default)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            RosterWriteStatus status = await SaveDeckAsync(accessToken, deck, cancellationToken)
                .ConfigureAwait(false);
            if (status == RosterWriteStatus.Ok) cache.Save(deck);
            return status;
        }

        /// <summary>
        /// One HTTP answer to one write status.
        ///
        /// <para><b>On the status CODE, not the body's status word.</b> The two agree by
        /// construction — every one of these routes derives its code from the same enum it
        /// serialises — so reading the body would add a second decoder and a second way to disagree,
        /// for nothing. The code is also the only thing available when a proxy answers instead of
        /// the server.</para>
        /// </summary>
        private static RosterWriteStatus Classify(AuthHttpResponse response)
        {
            if (response.IsTransportFailure) return RosterWriteStatus.Offline;
            if (response.IsSuccess) return RosterWriteStatus.Ok;

            switch (response.StatusCode)
            {
                case 400: return RosterWriteStatus.Rejected;
                case 401: return RosterWriteStatus.Unauthenticated;
                case 404: return RosterWriteStatus.NotFound;
                case 409: return RosterWriteStatus.Conflict;
                default: return RosterWriteStatus.Unavailable;
            }
        }

        private static List<KeyValuePair<string, string>> Headers(string accessToken)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
    }
}
