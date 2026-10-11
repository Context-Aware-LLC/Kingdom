using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Collection;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The game's one <see cref="HttpRosterGateway"/>, and the only place a server answer is allowed
    /// to replace this device's decks.
    ///
    /// <para><b>What this makes true.</b> Card ownership already survived a reinstall — the
    /// entitlement ledger is keyed on the authenticated <c>player_id</c> and
    /// <see cref="CollectionGateway"/> reads it down. Decks did not: they live only in
    /// <c>decks.json</c> under the local scope, and the OS deletes that with the app. Signing back
    /// into the right identity therefore restored a player's cards and left them with nothing to
    /// play them with. This is the other half of that wire.</para>
    ///
    /// <para>Sync replaces the cache with the server's answer, including an empty deck list.
    /// Deck creation and roster-dialog edits await confirmation before caching or navigating.
    /// Other edits and deletes use the Push methods and report failures to the log.</para>
    ///
    /// <para><b>Composed on first touch, like <see cref="AuthGateway"/> and
    /// <see cref="CollectionGateway"/>, and invalidated with them</b> — the write lands in
    /// <c>PlayerAccount.Local</c>, which is scoped to the signed-in local account, so a stale gateway
    /// after an account switch would write one player's roster into another's folder.</para>
    /// </summary>
    public static class RosterGateway
    {
        /// <summary>The match server's origin. Tracks <see cref="CollectionGateway.BaseUrl"/> so a
        /// test or an editor tool that repoints one repoints both — two roster halves talking to two
        /// different servers is not a state worth being able to express.</summary>
        public static string BaseUrl
        {
            get => CollectionGateway.BaseUrl;
            set => CollectionGateway.BaseUrl = value;
        }

        /// <summary>How long a sign-in will wait for the roster before going on without it. Same
        /// budget as the collection, and the same reasoning: the player is already authenticated, so
        /// the cost of giving up is a stale cache and one more sync later.</summary>
        private const int TimeoutSeconds = 15;

        private static HttpRosterGateway _gateway;
        private static string _gatewayBaseUrl;

        private static HttpRosterGateway Gateway
        {
            get
            {
                // Rebuilt when BaseUrl moves, because it is settable and a cached gateway would keep
                // talking to the old host with nothing to show for it.
                if (_gateway == null || !string.Equals(_gatewayBaseUrl, BaseUrl, StringComparison.Ordinal))
                {
                    _gatewayBaseUrl = BaseUrl;
                    _gateway = new HttpRosterGateway(BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));
                }
                return _gateway;
            }
        }

        /// <summary>Drop the cached gateway. Called from <see cref="DeviceAccounts.Invalidate"/>
        /// alongside <see cref="AuthGateway.Invalidate"/> and <see cref="CollectionGateway.Invalidate"/>.</summary>
        public static void Invalidate()
        {
            _gateway = null;
            _gatewayBaseUrl = null;
        }

        // ── Read down ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pull this identity's decks down and replace the local copy. Returns what happened, for a
        /// log line; callers do not need to branch on it.
        ///
        /// <para><b>Never throws, and never leaves a half-written roster.</b> A sign-in must not fail
        /// because a roster did not arrive — the player is authenticated either way, and the screens
        /// degrade to whatever the device already had, which is the state they were in before this
        /// existed.</para>
        /// </summary>
        public static async Task<RosterSyncStatus> SyncAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                RosterSnapshot snapshot = await Gateway.FetchAsync(accessToken, cancellationToken);

                if (!snapshot.ShouldReplaceLocal)
                {
                    Debug.LogWarning($"[Roster] not synced ({snapshot.Status}): {snapshot.Detail}. " +
                                     "Keeping whatever this device already had.");
                    return snapshot.Status;
                }

                PlayerAccount.Local.ReplaceRoster(snapshot.Decks);
                Debug.Log($"[Roster] synced {snapshot.Decks.Count} deck(s) for the signed-in account.");
                return snapshot.Status;
            }
            catch (Exception ex)
            {
                // The gateway is written not to throw, and the sign-in path must not be the place
                // that finds out it was wrong about that.
                Debug.LogWarning($"[Roster] sync threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "keeping the local roster.");
                return RosterSyncStatus.Offline;
            }
        }

        /// <summary>Wait for the server before caching a new deck.</summary>
        public static Task<RosterWriteStatus> ConfirmNewDeckAsync(
            string accessToken, DeckList deck, IDeckStore cache) =>
            Gateway.SaveDeckAndCacheAsync(accessToken, deck, cache);

        /// <summary>Wait for the server before caching an edit, including cosmetic ownership checks.</summary>
        public static Task<RosterWriteStatus> ConfirmDeckEditAsync(
            string accessToken, DeckList deck, IDeckStore cache) =>
            Gateway.SaveDeckAndCacheAsync(accessToken, deck, cache);

        // ── Push up ─────────────────────────────────────────────────────────────────────

        /// <summary>Tell the server about a deck this device just saved — an upsert, and never
        /// legality-gated: a draft is a savable deck, so it is a pushable one.</summary>
        public static Task<RosterWriteStatus> PushDeckSavedAsync(
            string accessToken, DeckList deck, CancellationToken cancellationToken = default)
            => PushAsync("save deck " + deck?.Id,
                         () => Gateway.SaveDeckAsync(accessToken, deck, cancellationToken));

        /// <summary>Tell the server about a deck this device just deleted.</summary>
        public static Task<RosterWriteStatus> PushDeckDeletedAsync(
            string accessToken, string deckId, CancellationToken cancellationToken = default)
            => PushAsync("delete deck " + deckId,
                         () => Gateway.DeleteDeckAsync(accessToken, deckId, cancellationToken));

        /// <summary>
        /// One push, with its failure turned into a log line rather than an exception.
        ///
        /// <para><b>NotFound is not a failure on a delete</b> — the row was already gone, so the two
        /// records agree, which is the outcome the call wanted. Everything else that is not Ok means
        /// the device is ahead of the server and the next sync will overwrite the difference.</para>
        /// </summary>
        private static async Task<RosterWriteStatus> PushAsync(
            string what, Func<Task<RosterWriteStatus>> call)
        {
            try
            {
                RosterWriteStatus status = await call();
                if (status != RosterWriteStatus.Ok && status != RosterWriteStatus.NotFound)
                    Debug.LogWarning($"[Roster] could not {what} on the server ({status}). " +
                                     "This device is ahead of the server until the next sync.");
                return status;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Roster] {what} threw ({ex.GetType().Name}: {ex.Message}).");
                return RosterWriteStatus.Offline;
            }
        }

        /// <summary>
        /// The signed-in access token, or the empty string. Every push needs it and no caller should
        /// have to know where it lives — an empty one makes the gateway answer Unauthenticated
        /// without reaching the network, which is the right answer for a device that is not signed in.
        ///
        /// <para>Guarded because it is read from ordinary UI handlers: composing the provider touches
        /// the settings asset and player prefs, and a throw there must not take down a deck creation
        /// that has already been saved locally.</para>
        /// </summary>
        public static string AccessToken
        {
            get
            {
                try { return AuthGateway.Provider.Current.AccessToken ?? ""; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Roster] no access token available ({ex.GetType().Name}); " +
                                     "the local write stands and the server will not hear about it.");
                    return "";
                }
            }
        }
    }
}
