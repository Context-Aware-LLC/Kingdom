using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// Android's own Google sign-in sheet (Credential Manager), in place of the browser. It hands
    /// back a Google ID token, which <c>AuthTokenProvider.SignInWithIdTokenAsync</c> trades with
    /// Supabase for the same account the browser sign-in reaches. Android device builds only.
    ///
    /// <para><b>The nonce.</b> The sheet is given the SHA-256 of a fresh random value and Supabase is
    /// given the value itself, so a token minted for some other request cannot be replayed into this
    /// one.</para>
    ///
    /// <para><b>Release builds shrink Java code (R8).</b> The bridge class and its callback are found
    /// by name from here, which R8 cannot see, so <c>KobiGoogleSignIn.java</c> marks both
    /// <c>@Keep</c>. Rename either and the strings below must follow.</para>
    /// </summary>
    public static class NativeGoogleSignIn
    {
        public enum Outcome
        {
            SignedIn,

            /// <summary>The player closed the sheet. Not an error, and not a reason to open the
            /// browser instead.</summary>
            Cancelled,

            /// <summary>Anything else: no Google account, Play services missing, or the Google
            /// Cloud set-up not matching this build. The browser sign-in still works.</summary>
            Unavailable,
        }

        public readonly struct Result
        {
            public Result(Outcome outcome, string idToken, string rawNonce, string detail)
            {
                Outcome = outcome;
                IdToken = idToken ?? "";
                RawNonce = rawNonce ?? "";
                Detail = detail ?? "";
            }

            public Outcome Outcome { get; }
            public string IdToken { get; }

            /// <summary>The value whose SHA-256 is inside <see cref="IdToken"/>; Supabase wants this one.</summary>
            public string RawNonce { get; }

            public string Detail { get; }
        }

        private const string BridgeClass = "com.contextaware.kobi.auth.KobiGoogleSignIn";

        public static bool IsSupported =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        /// Show the sheet and wait for the player. Never throws; every way it can go wrong comes back
        /// as <see cref="Outcome.Unavailable"/> with the reason in <see cref="Result.Detail"/>.
        /// </summary>
        /// <param name="webClientId">The Google web client the token is requested for — its audience.</param>
        public static Task<Result> SignInAsync(string webClientId)
        {
            if (!IsSupported || string.IsNullOrEmpty(webClientId))
                return Task.FromResult(new Result(Outcome.Unavailable, "", "", "not available in this build"));

            string rawNonce = NewNonce();
            var done = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            Start(webClientId, rawNonce, done);
            return done.Task;
        }

        private static string NewNonce()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Hex(bytes);
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

#if UNITY_ANDROID
        /// <summary>
        /// The callback in flight. Held here so nothing can collect the proxy while Java still owes it
        /// a call; one sign-in at a time is all the screen allows.
        /// </summary>
        private static Callback _inFlight;

        private static void Start(string webClientId, string rawNonce, TaskCompletionSource<Result> done)
        {
            try
            {
                _inFlight = new Callback(done, rawNonce);
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var bridge = new AndroidJavaClass(BridgeClass))
                {
                    bridge.CallStatic("signIn", activity, webClientId, Sha256Hex(rawNonce), _inFlight);
                }
            }
            catch (Exception ex)
            {
                _inFlight = null;
                done.TrySetResult(new Result(Outcome.Unavailable, "", "", ex.GetType().Name + ": " + ex.Message));
            }
        }

        /// <summary>
        /// Java calls this on its own thread. It only completes the task: the caller awaits it from
        /// the main thread, so its continuation runs there.
        /// </summary>
        private sealed class Callback : AndroidJavaProxy
        {
            private readonly TaskCompletionSource<Result> _done;
            private readonly string _rawNonce;

            public Callback(TaskCompletionSource<Result> done, string rawNonce)
                : base(BridgeClass + "$Callback")
            {
                _done = done;
                _rawNonce = rawNonce;
            }

            // Names and signatures match KobiGoogleSignIn.Callback in the Java bridge.
            public void onToken(string idToken)
            {
                _inFlight = null;
                _done.TrySetResult(string.IsNullOrEmpty(idToken)
                    ? new Result(Outcome.Unavailable, "", "", "the sheet returned an empty token")
                    : new Result(Outcome.SignedIn, idToken, _rawNonce, ""));
            }

            public void onError(string kind, string detail)
            {
                _inFlight = null;
                _done.TrySetResult(new Result(
                    kind == "cancelled" ? Outcome.Cancelled : Outcome.Unavailable, "", "", detail));
            }
        }
#else
        private static void Start(string webClientId, string rawNonce, TaskCompletionSource<Result> done)
            => done.TrySetResult(new Result(Outcome.Unavailable, "", "", "not available in this build"));
#endif
    }
}
