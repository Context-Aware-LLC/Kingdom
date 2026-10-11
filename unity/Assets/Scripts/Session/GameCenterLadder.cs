using System;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Kingdom.Session
{
    /// <summary>
    /// Copies the signed-in player's ladder rating onto the Game Center leaderboard. iOS only; a
    /// no-op on every other platform and in the Editor.
    ///
    /// <para><b>The server's ladder is the record and this is a copy of it.</b> GameKit only takes a
    /// score from the device, so a modified client can post any number to the Game Center board —
    /// which is why nothing in the game may ever read a rating back from it. <c>GET /rating</c> and
    /// <c>GET /leaderboard</c> are the only sources.</para>
    ///
    /// <para><b>Sign-in is silent.</b> <c>KobiGameCenter.mm</c> never presents Game Center's
    /// sign-in sheet, so a player who is not already signed in on the device is simply not
    /// reported. Presenting it is a decision about the first thing a new player sees, not a
    /// missing feature.</para>
    /// </summary>
    public static class GameCenterLadder
    {
        /// <summary>The Leaderboard ID in App Store Connect. It is permanent there once saved, so
        /// this string can never change either — a new ID is a new, empty leaderboard.</summary>
        public const string LeaderboardId = "com.contextaware.kobi.ladder.rating";

        private static bool IsSupported =>
#if UNITY_IOS && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        /// Sign in to Game Center and report the player's current rating. Never throws, and is
        /// meant to be fired and forgotten: nothing on screen waits on it.
        ///
        /// <para>A player with no rated match behind them is not reported. The server answers them
        /// with the ladder default, and posting that would put every install on the board at the
        /// same number.</para>
        /// </summary>
        public static async Task SyncAsync(string accessToken)
        {
            if (!IsSupported) return;

            try
            {
                Authenticate();

                OwnRatingResponse own =
                    await LeaderboardApi.FetchOwnRatingAsync(CollectionGateway.BaseUrl, accessToken);
                if (own == null || !own.IsRanked) return;

                Submit(own.rating);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GameCenter] rating not reported ({ex.GetType().Name}: {ex.Message}).");
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void KobiGameCenter_Authenticate();

        [DllImport("__Internal")]
        private static extern void KobiGameCenter_SubmitScore(long score, string leaderboardId);

        private static void Authenticate() => KobiGameCenter_Authenticate();
        private static void Submit(int rating) => KobiGameCenter_SubmitScore(rating, LeaderboardId);
#else
        private static void Authenticate() { }
        private static void Submit(int rating) { }
#endif
    }
}
