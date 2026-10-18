using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Client.Achievements;
using Kingdom.Net.Client.Auth;
using Kingdom.Net.Client.Social;
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
    /// achievements; their Bad Ideas account is still the Supabase sign-in. The server records the
    /// player's Play Games id against that account (<see cref="LinkAsync"/>) so their Play Games
    /// friends can find them (#423), and so a new device signed in to Play Games can restore the
    /// account without the sign-in screen (<see cref="TryRestoreAsync"/>, #425).</para>
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
        private static bool _linked;

        /// <summary>Play Games exists on this build: an Android device. Screens hide their Play
        /// Games buttons everywhere else.</summary>
        public static bool IsSupported =>
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

        /// <summary>
        /// The achievements button: sign in by hand when the automatic sign-in failed (Play's
        /// quality checklist asks for a way back in), copy the server's achievements, then open
        /// Play Games' own achievements screen. False when the player is still not signed in.
        /// </summary>
        public static async Task<bool> ShowAchievementsAsync()
        {
            if (!IsSupported) return false;
            if (!await SignInAsync())
            {
                if (!await ManualSignIn()) return false;
                _signIn = Task.FromResult(true);
            }

            try
            {
                if (AuthGateway.IsSignedIn)
                {
                    string token = await AuthGateway.TokenForConnectAsync();
                    await SyncAchievementsAsync(token);
                    _ = LinkAsync(token);
                }
            }
            catch (Exception ex)
            {
                // The Play Games screen still opens, showing what is already unlocked.
                Debug.LogWarning($"[PlayGames] achievements not synced before showing ({ex.GetType().Name}: {ex.Message}).");
            }
            ShowAchievementsUi();
            return true;
        }

        /// <summary>
        /// Tell the server this player's Play Games id, so their Play Games friends can find them
        /// (#423). Once per launch, after Play Games signs in. Never throws; nothing waits on it.
        /// </summary>
        public static async Task LinkAsync(string accessToken)
        {
            if (!IsSupported || _linked) return;
            try
            {
                if (!await SignInAsync()) return;
                string code = await ServerAuthCode();
                if (string.IsNullOrEmpty(code)) return;
                PlayGamesFriendsStatus status = await FriendsGateway.LinkPlayGamesAsync(accessToken, code);
                _linked = status == PlayGamesFriendsStatus.Ok;
                if (!_linked) Debug.Log($"[PlayGames] not linked ({status}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayGames] link failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Link again on the next <see cref="LinkAsync"/>: the account changed.</summary>
        public static void ForgetLink() => _linked = false;

        /// <summary>How long each step of a restore may take before the sign-in screen shows
        /// instead: Play Games' own sign-in, then its auth code. The server call has its own.</summary>
        private const int RestoreStepMs = 8000;

        private static PlayGamesRestoreGateway _restoreGateway;

        /// <summary>
        /// Seamless restore (#425): sign in to the account this phone's Play Games profile is linked
        /// to, with no sign-in screen. True once a session is stored; false, with nothing changed, on
        /// every other outcome, and then the screen shows as before. Never throws.
        ///
        /// <para><b>Only on a device where no account has ever signed in.</b> A sign-out leaves the
        /// account on the device claimed (<see cref="DeviceAccounts.HasClaimedAccount"/>), so the
        /// player who signed out to change accounts is not signed straight back in; a new phone or a
        /// reinstall has none.</para>
        /// </summary>
        public static async Task<bool> TryRestoreAsync(AuthTokenProvider auth)
        {
            if (!IsSupported || auth == null || !auth.IsConfigured || auth.IsSignedIn) return false;
            if (DeviceAccounts.HasClaimedAccount) return false;

            try
            {
                if (!await Within(SignInAsync(), false)) return false;
                string code = await Within(ServerAuthCode(), null);
                if (string.IsNullOrEmpty(code)) return false;

                _restoreGateway ??= new PlayGamesRestoreGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(10));
                PlayGamesRestore restore = await _restoreGateway.RestoreAsync(code);
                if (restore.Status != PlayGamesRestoreStatus.Restored)
                {
                    Debug.Log($"[PlayGames] no account restored ({restore.Status}).");
                    return false;
                }

                AuthResult result = await auth.SignInWithTokenHashAsync(restore.TokenHash, restore.TokenType);
                if (!result.Ok) Debug.LogWarning($"[PlayGames] Supabase refused the restore ({result.Failure}: {result.Detail}).");
                return result.Ok;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayGames] restore failed ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        private static async Task<T> Within<T>(Task<T> step, T onTimeout)
            => await Task.WhenAny(step, Task.Delay(RestoreStepMs)) == step ? await step : onTimeout;

        /// <summary>
        /// "Find Friends From Play Games": sign in by hand if need be, ask the player to share their
        /// Play Games friends if they have not, then have the server list the ones who play. The
        /// consent is Google's own sheet, shown once; a refusal answers FriendsHidden.
        /// </summary>
        public static async Task<PlayGamesFriendsResult> FindFriendsAsync(string accessToken)
        {
            if (!IsSupported) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unavailable);
            try
            {
                if (!await SignInAsync())
                {
                    if (!await ManualSignIn()) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unauthenticated);
                    _signIn = Task.FromResult(true);
                }
                if (!await FriendsShared()) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.FriendsHidden);

                string code = await ServerAuthCode();
                if (string.IsNullOrEmpty(code)) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.InvalidCode);
                PlayGamesFriendsResult result = await FriendsGateway.FindPlayGamesFriendsAsync(accessToken, code);
                if (result.Status == PlayGamesFriendsStatus.Ok) _linked = true;
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayGames] find friends failed ({ex.GetType().Name}: {ex.Message}).");
                return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Offline);
            }
        }

        /// <summary>Open a Play Games player's profile, the icon beside a Play Games friend.</summary>
        public static void ShowProfile(string playGamesId, string theirName, string myName)
        {
            if (!IsSupported || string.IsNullOrEmpty(playGamesId)) return;
            ShowProfileUi(playGamesId, theirName ?? "", myName ?? "");
        }

#if UNITY_ANDROID
        private static Task<bool> ManualSignIn()
        {
            var done = new TaskCompletionSource<bool>();
            PlayGamesPlatform.Instance.ManuallyAuthenticate(status => done.TrySetResult(status == SignInStatus.Success));
            return done.Task;
        }

        private static void ShowAchievementsUi() => PlayGamesPlatform.Instance.ShowAchievementsUI(null);

        private static Task<string> ServerAuthCode()
        {
            var done = new TaskCompletionSource<string>();
            PlayGamesPlatform.Instance.RequestServerSideAccess(false, code => done.TrySetResult(code));
            return done.Task;
        }

        private static Task<bool> FriendsShared()
        {
            var done = new TaskCompletionSource<bool>();
            PlayGamesPlatform.Instance.GetFriendsListVisibility(true, visibility =>
            {
                if (visibility == FriendsListVisibilityStatus.Visible) { done.TrySetResult(true); return; }
                if (visibility != FriendsListVisibilityStatus.ResolutionRequired) { done.TrySetResult(false); return; }
                PlayGamesPlatform.Instance.AskForLoadFriendsResolution(ui => done.TrySetResult(ui == UIStatus.Valid));
            });
            return done.Task;
        }

        private static void ShowProfileUi(string playGamesId, string theirName, string myName)
            => PlayGamesPlatform.Instance.ShowCompareProfileWithAlternativeNameHintsUI(
                playGamesId, theirName, myName, null);

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
        private static Task<bool> ManualSignIn() => Task.FromResult(false);
        private static void ShowAchievementsUi() { }
        private static Task<string> ServerAuthCode() => Task.FromResult<string>(null);
        private static Task<bool> FriendsShared() => Task.FromResult(false);
        private static void ShowProfileUi(string playGamesId, string theirName, string myName) { }

        private static Task<bool> Authenticate() => Task.FromResult(false);

        private static void Unlock(string playGamesId, Action<bool> done) => done(false);
#endif
    }
}
