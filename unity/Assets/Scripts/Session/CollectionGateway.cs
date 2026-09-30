using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Collection;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// The game's one <see cref="HttpCollectionGateway"/>, and the only place a server answer is
    /// allowed to overwrite this device's ownership record.
    ///
    /// <para><b>What this makes true.</b> The entitlement ledger has been the authority on what a
    /// player owns since 2026-09-01, keyed on the authenticated <c>player_id</c>, and nothing on the
    /// client could read it — <c>CollectionController</c> and <c>CharacterRosterController</c> both go
    /// to local disk. So a player who uninstalled, reinstalled and signed back into the right identity
    /// landed on an empty local scope, which renders as <i>owns everything</i>. Their cards were never
    /// lost; there was no wire from the ledger to the screen. This is the client end of that wire
    /// (spec <c>docs/dev-docs/specs/card-ownership.md</c>, slice 3).</para>
    ///
    /// <para><b><c>owned-cards.json</c> is a CACHE now, not the record.</b> The record is the ledger.
    /// The three states the file can be in are unchanged and still mean what they always did —
    /// absent is "unknown, do not constrain", present-and-empty is "owns nothing" — which is why
    /// <see cref="SyncAsync"/> writes on exactly one outcome and leaves the file untouched on every
    /// other. Writing an empty record because the network was down is the one bug in this file worth
    /// guarding: it turns every deck on the device into a draft and looks like a server decision.</para>
    ///
    /// <para><b>Composed on first touch, like <see cref="AuthGateway"/>, and invalidated with it.</b>
    /// The write lands in <c>PlayerAccount.Local</c>, which is scoped to the signed-in local account,
    /// so a stale gateway after an account switch would write one player's collection into another's
    /// folder.</para>
    /// </summary>
    public static class CollectionGateway
    {
        /// <summary>
        /// The match server's origin. Same host as <c>MainMenuController.leaderboardUrl</c>,
        /// <c>NetworkedGameBootstrap.serverUrl</c> and <c>B3TransportVerify.Live</c> — a fourth copy
        /// rather than a shared constant because those three are scene-serialized defaults and this
        /// one is not, so unifying them is a change to what a built scene contains, not a refactor.
        ///
        /// <para>Settable so an editor tool or a test can point at a local server; there is
        /// deliberately no UI for it.</para>
        /// </summary>
        public static string BaseUrl { get; set; } = "https://emberfall.fly.dev";

        /// <summary>How long a sign-in will wait for the collection before going on without it. The
        /// player is already authenticated at this point, so the cost of giving up is a stale cache
        /// and one more sync later — never a failed sign-in.</summary>
        private const int TimeoutSeconds = 15;

        private static HttpCollectionGateway _gateway;

        private static HttpCollectionGateway Gateway =>
            _gateway ??= new HttpCollectionGateway(BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));

        /// <summary>Drop the cached gateway. Called from <see cref="DeviceAccounts.Invalidate"/>
        /// alongside <see cref="AuthGateway.Invalidate"/>.</summary>
        public static void Invalidate() => _gateway = null;

        /// <summary>
        /// Pull this identity's collection down and cache it. Returns what happened, for a log line;
        /// callers do not need to branch on it.
        ///
        /// <para><b>Never throws, and never leaves a half-written record.</b> A sign-in must not fail
        /// because a collection did not arrive — the player is authenticated either way, and the
        /// screens degrade to whatever the device already had, which is the state they were in before
        /// this existed.</para>
        /// </summary>
        public static async Task<CollectionSyncStatus> SyncAsync(
            string accessToken, CancellationToken cancellationToken = default)
            => (await SyncSnapshotAsync(accessToken, cancellationToken)).Status;

        /// <summary>
        /// Pull this identity's collection down, apply the same guarded ownership-cache update as
        /// <see cref="SyncAsync"/>, and return the complete server snapshot to a caller that also
        /// needs live wallet or pack balances. One request therefore has one identity check and one
        /// authoritative answer instead of each main-menu feature fetching <c>/collection</c> again.
        /// </summary>
        public static async Task<CollectionSnapshot> SyncSnapshotAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            PlayerAccount account = PlayerAccount.Local;
            CollectionSnapshot snapshot = await FetchAsync(accessToken, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(account, PlayerAccount.Local))
                return CollectionSnapshot.Failed(
                    CollectionSyncStatus.Unauthenticated,
                    "the active account changed while its collection was loading");

            if (!snapshot.ShouldReplaceCache)
            {
                // Deliberately not a warning for Unconstrained: a superuser having no cached list
                // is the correct outcome, not a degraded one.
                if (snapshot.Status != CollectionSyncStatus.Unconstrained)
                    Debug.LogWarning($"[Collection] not synced ({snapshot.Status}): {snapshot.Detail}. " +
                                     "Keeping whatever this device already had.");
                return snapshot;
            }

            account.SaveOwnedCards(snapshot.Cards);
            Debug.Log($"[Collection] synced {snapshot.Cards.RecordedCards} card row(s) " +
                      $"for the signed-in account.");
            return snapshot;
        }

        /// <summary>
        /// Read this identity's collection and hand back the whole answer, <b>writing nothing</b>.
        ///
        /// <para><b>Why a read that does not cache.</b> <see cref="SyncAsync"/> is about the ownership
        /// record and deliberately discards everything else the route returns. The wallet and the pack
        /// balances are not ownership and have no local record to be stale against — they are read
        /// live, at the moment a screen needs them, by whoever needs them. <c>PackVaultController</c>
        /// is the first such caller (spec
        /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, slice 1).</para>
        ///
        /// <para><b>This is the only place the collection gateway is constructed</b>, which is why the
        /// read lives here rather than in <c>PackGateway</c>: two owners of one route would be two
        /// caches to invalidate on an account switch, and only one of them would be wired to
        /// <see cref="DeviceAccounts.Invalidate"/>.</para>
        ///
        /// <para>Never throws — same contract as <see cref="SyncAsync"/>, and now the single place
        /// that keeps it.</para>
        /// </summary>
        public static async Task<CollectionSnapshot> FetchAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                return await Gateway.FetchAsync(accessToken, cancellationToken);
            }
            catch (Exception ex)
            {
                // The gateway is written not to throw, and the sign-in path must not be the place that
                // finds out it was wrong about that.
                Debug.LogWarning($"[Collection] fetch threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "reporting it as offline.");
                return CollectionSnapshot.Failed(CollectionSyncStatus.Offline,
                                                 $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
