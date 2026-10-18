using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Client.Achievements;
using UnityEngine;
#if UNITY_ANDROID
using GooglePlayGames;
using GooglePlayGames.BasicApi;
#endif

namespace Kingdom.Session
{
    /// <summary>
    /// Google Play Games sign-in, and the copy of the player's achievements kept there. Android device
    /// builds only; a no-op on every other platform and in the Editor.
    ///
    /// <para><b>The server's Ledger is the record and this is a copy of it.</b> The server decides
    /// what was earned (<c>GET /achievements</c>); the client only unlocks those in Play Games. A
    /// modified client can unlock anything there, so nothing in the game may read an achievement
    /// back from Play Games.</para>
    ///
    /// <para><b>Play Games is not the player's account.</b> Signing in here gives the player their
    /// achievements; their Bad Ideas account is still the Supabase sign-in, and nothing links the two
    /// yet.</para>
    ///
    /// <para><b>The plugin's own calls, never Unity's Social API.</b> Unity 6000.6 marks
    /// <c>Social</c>, <c>ISocialPlatform</c>, <c>IAchievement</c> and <c>IAchievementDescription</c>
    /// deprecated; <c>PlayGamesPlatform</c>'s own methods use none of them. Do not call
    /// <c>PlayGamesPlatform.Activate()</c>: all it adds is installing the plugin as
    /// <c>Social.Active</c>.</para>
    /// </summary>
    public static class PlayGamesServices
    {
        /// <summary>When to read after a match ends: the server earns achievements on a queue behind
        /// the match (<c>AchievementSink</c>), so the first read waits a moment and the second
        /// catches a slow one.</summary>
        private static readonly int[] AfterMatchDelaysMs = { 1500, 4000 };

        private static readonly HashSet<string> Unlocked = new HashSet<string>(StringComparer.Ordinal);
        private static Task<bool> _signIn;
        private static HttpAchievementGateway _gateway;

        private static bool IsSupported =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        /// Play Games signs the player in by itself when its SDK starts, and the SDK starts on the
        /// first use of <c>PlayGamesPlatform.Instance</c>. Touching it at launch is what makes that
        /// sign-in automatic, which Play's quality checklist requires.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void SignInAtLaunch()
        {
            if (IsSupported) _ = SignInAsync();
        }

        /// <summary>
        /// The result of the automatic sign-in, asked once per launch. False means the player is not
        /// signed in to Play Games, which is an ordinary state rather than an error: the game works
        /// the same without it.
        /// </summary>
        public static Task<bool> SignInAsync()
        {
            if (!IsSupported) return Task.FromResult(false);
            return _signIn ??= Authenticate();
        }

        /// <summary>
        /// Unlock in Play Games everything the server says this player has earned. Never throws, and
        /// is meant to be fired and forgotten after each sign-in and each match: nothing on screen
        /// waits on it. An achievement already unlocked there is skipped for the rest of the launch.
        /// </summary>
        public static async Task SyncAchievementsAsync(string accessToken)
        {
            if (!IsSupported) return;

            try
            {
                if (!await SignInAsync()) return;

                _gateway ??= new HttpAchievementGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(15));
                AchievementSnapshot snapshot = await _gateway.FetchAsync(accessToken);
                if (snapshot.Status != AchievementFetchStatus.Known)
                {
                    Debug.LogWarning($"[PlayGames] achievements not read ({snapshot.Status}: {snapshot.Detail}).");
                    return;
                }

                foreach (string playGamesId in snapshot.PlayGamesIdsToUnlock(Unlocked))
                {
                    // Claimed before the call so a second sync running alongside this one skips it;
                    // given back if Play Games refuses, so the next sync tries again.
                    Unlocked.Add(playGamesId);
                    Unlock(playGamesId, ok =>
                    {
                        if (ok) return;
                        Unlocked.Remove(playGamesId);
                        Debug.LogWarning($"[PlayGames] achievement {playGamesId} was not unlocked.");
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayGames] achievements not synced ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>
        /// The sync for a match that just ended, so its achievements unlock under the result screen.
        /// A finished match goes to deck selection, not the menu, so the menu's sync alone would
        /// hold them back until the player left. Never throws.
        /// </summary>
        public static async Task SyncAchievementsAfterMatchAsync()
        {
            if (!IsSupported || !AuthGateway.IsSignedIn) return;

            try
            {
                foreach (int delayMs in AfterMatchDelaysMs)
                {
                    await Task.Delay(delayMs);
                    await SyncAchievementsAsync(await AuthGateway.TokenForConnectAsync());
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayGames] match achievements not synced ({ex.GetType().Name}: {ex.Message}).");
            }
        }

#if UNITY_ANDROID
        private static Task<bool> Authenticate()
        {
            var signedIn = new TaskCompletionSource<bool>();
            PlayGamesPlatform.Instance.Authenticate(status =>
            {
                if (status != SignInStatus.Success)
                    Debug.Log($"[PlayGames] not signed in ({status}); achievements wait for the next launch.");
                signedIn.TrySetResult(status == SignInStatus.Success);
            });
            return signedIn.Task;
        }

        private static void Unlock(string playGamesId, Action<bool> done)
            => PlayGamesPlatform.Instance.UnlockAchievement(playGamesId, done);
#else
        private static Task<bool> Authenticate() => Task.FromResult(false);

        private static void Unlock(string playGamesId, Action<bool> done) => done(false);
#endif
    }
}
