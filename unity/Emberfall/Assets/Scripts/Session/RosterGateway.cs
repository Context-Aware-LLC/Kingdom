using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Collection;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// The game's one <see cref="HttpRosterGateway"/>, and the only place a server answer is allowed
    /// to replace this device's characters and decks.
    ///
    /// <para><b>What this makes true.</b> Card ownership already survived a reinstall — the
    /// entitlement ledger is keyed on the authenticated <c>player_id</c> and
    /// <see cref="CollectionGateway"/> reads it down. The roster did not: characters and decks live
    /// only in <c>characters.json</c> / <c>decks.json</c> under the local scope, and the OS deletes
    /// those with the app. Signing back into the right identity therefore restored a player's cards
    /// and left them with no characters to play them with. This is the other half of that wire.</para>
    ///
    /// <para><b>Both directions, and they are not symmetrical.</b> <see cref="SyncAsync"/> replaces
    /// the local files from the server and is the only caller of
    /// <see cref="PlayerAccount.ReplaceRoster"/>. The Push* methods are fire-and-report: the local
    /// write has already happened and is what the player sees, so a failed push is a log line and a
    /// device whose copy is ahead of the server — never a refused edit and never a rolled-back save.
    /// The next successful sync is what reconciles them, and it reconciles by REPLACING, so a push
    /// that never landed loses that edit. That is the accepted cost of having one record instead of
    /// two, and it is why every push is reported rather than swallowed.</para>
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
        /// Pull this identity's characters and decks down and replace the local copy. Returns what
        /// happened, for a log line; callers do not need to branch on it.
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

                if (await AdoptLocalRosterAsync(accessToken, snapshot, cancellationToken))
                    return RosterSyncStatus.Known;

                PlayerAccount.Local.ReplaceRoster(snapshot.Characters, snapshot.Decks);
                Debug.Log($"[Roster] synced {snapshot.Characters.Count} character(s) and " +
                          $"{snapshot.Decks.Count} deck(s) for the signed-in account.");
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

        /// <summary>
        /// The one-time adoption of a roster that predates the wire, and the ONLY place a local
        /// character is treated as more authoritative than the server's answer.
        ///
        /// <para><b>Why it exists.</b> Every device that ran this game before the roster routes landed
        /// has characters and decks in its local scope that the server has never seen. The
        /// server-authoritative rule would delete them on the first sign-in — silently, permanently,
        /// and for data that is by construction the only copy in existence. Pushing them up instead
        /// costs one round trip per row and loses nothing.</para>
        ///
        /// <para><b>Why it is narrow.</b> It fires only when the server answered <c>known</c> with
        /// NO characters at all while this device has some. Once anything of this player's is on the
        /// server, the server is the record and a local row the server does not have is a deletion,
        /// not an orphan.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER — this method must be gated or deleted the moment a second
        /// device can hold one account's roster.</b> With two devices, "server empty, device
        /// non-empty" stops meaning "never uploaded" and starts meaning "deleted on the other
        /// device", and this would resurrect it — the same defect <c>DELETE /characters/{id}</c> was
        /// added to fix, one level up. The gate to use is <c>onboarded</c> from <c>GET /session</c>:
        /// adopt only when it is false. That flag is already on the wire and the client does not read
        /// it yet, so this comment is the thing that connects them. Whoever makes the client block on
        /// <c>/session</c> discharges this.</para>
        ///
        /// <returns>True if the local roster was adopted and must NOT be replaced.</returns>
        /// </summary>
        private static async Task<bool> AdoptLocalRosterAsync(
            string accessToken, RosterSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot.Characters.Count > 0) return false;

            IReadOnlyList<Character> localCharacters = PlayerAccount.Local.Roster();
            if (localCharacters.Count == 0) return false;

            IReadOnlyList<DeckList> localDecks = PlayerAccount.Local.Decks();
            Debug.Log($"[Roster] the server has no characters for this account and this device has " +
                      $"{localCharacters.Count}. Uploading them rather than deleting them.");

            bool complete = true;
            foreach (Character character in localCharacters)
            {
                RosterWriteStatus status = await Gateway
                    .CreateCharacterAsync(accessToken, character, cancellationToken);
                if (status != RosterWriteStatus.Ok)
                {
                    complete = false;
                    Debug.LogWarning($"[Roster] could not upload character {character.Id}: {status}.");
                }
            }

            foreach (DeckList deck in localDecks)
            {
                RosterWriteStatus status = await Gateway
                    .SaveDeckAsync(accessToken, deck, cancellationToken);
                if (status != RosterWriteStatus.Ok)
                {
                    complete = false;
                    Debug.LogWarning($"[Roster] could not upload deck {deck.Id}: {status}.");
                }
            }

            // Either way the local roster stands: a partial upload must not be followed by a replace
            // that deletes the rows which failed to land. The next sync tries again.
            if (!complete)
                Debug.LogWarning("[Roster] the upload was incomplete; keeping the local roster and " +
                                 "leaving the server behind until the next sign-in.");

            return true;
        }

        // ── Push up ─────────────────────────────────────────────────────────────────────

        /// <summary>Tell the server about a character this device just created. Call AFTER the local
        /// write, never instead of it.</summary>
        public static Task<RosterWriteStatus> PushCharacterCreatedAsync(
            string accessToken, Character character, CancellationToken cancellationToken = default)
            => PushAsync("create character " + character?.Id,
                         () => Gateway.CreateCharacterAsync(accessToken, character, cancellationToken));

        /// <summary>Tell the server about a character this device just deleted. The server cascades
        /// to that character's decks, so do NOT also push a delete for each of them.</summary>
        public static Task<RosterWriteStatus> PushCharacterDeletedAsync(
            string accessToken, string characterId, CancellationToken cancellationToken = default)
            => PushAsync("delete character " + characterId,
                         () => Gateway.DeleteCharacterAsync(accessToken, characterId, cancellationToken));

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
        /// the settings asset and player prefs, and a throw there must not take down a character
        /// creation that has already been saved locally.</para>
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
