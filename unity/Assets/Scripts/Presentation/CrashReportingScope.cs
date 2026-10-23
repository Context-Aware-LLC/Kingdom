using System;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Session;
using Sentry;
using SentrySdk = Sentry.Unity.SentrySdk;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The funnel's keys on every Sentry event (pre-publication item 1): <c>session_id</c>,
    /// <c>device</c>, <c>build</c> from boot; <c>account_id</c> while signed in; <c>match_id</c> while a
    /// match scene is up.
    ///
    /// <para><b>Set on the scope, never per event</b>, because the scope is what the SDK syncs down to
    /// the Android Java/NDK layer — a native crash carries only what the scope held when it died.</para>
    ///
    /// <para><b>Does nothing when the SDK is off</b> (empty DSN, or the Editor): every entry point checks
    /// <see cref="SentrySdk.IsEnabled"/> first, so an unconfigured build never touches the account
    /// registry or the auth store on this class's behalf.</para>
    ///
    /// <para><b>Self-bootstrapping</b>, like <c>DevicePerfCapture</c>: no scene or prefab references it.
    /// The account watch is a hidden object that compares the signed-in user id once a second, which
    /// covers sign-in, sign-out, a refresh and an account switch without a hook in any of them.</para>
    /// </summary>
    public static class CrashReportingScope
    {
        public const string SessionTag = "session_id";
        /// <summary>NOT <c>device</c>: Sentry reserves that tag for the device model and overwrote ours
        /// with "Pixel 5a" on the first real crash (2026-10-23). The funnel's key is still called
        /// <c>device</c>; the webhook reads this tag into it.</summary>
        public const string DeviceTag = "device_hash";
        public const string BuildTag = "build";
        /// <summary>The Supabase user id (<c>sub</c>) — the identity the server keys an account on, via
        /// <c>credential_hash = sha256(sub)</c>.</summary>
        public const string AccountTag = "account_id";
        public const string MatchTag = "match_id";

        /// <summary>
        /// The device hash for a device token: lowercase hex SHA-256 of its UTF-8 bytes, first 16
        /// characters. Never the token itself — the token is a bearer credential on hello (VULN-003).
        /// The server can derive the same value from the token it already holds to join on it.
        /// </summary>
        public static string DeviceHash(string deviceToken)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(deviceToken ?? ""));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(digest[i].ToString("x2"));
                return hex.ToString();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!SentrySdk.IsEnabled) return;

            string device = DeviceHash(DeviceIdentity.Token);
            string session = FunnelSession.Id;
            string build = BuildStamp.ReleaseId;
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag(SessionTag, session);
                scope.SetTag(DeviceTag, device);
                scope.SetTag(BuildTag, build);
                // The device hash doubles as the user id so "users affected" counts devices. The IP is
                // pinned to a non-address so the server never infers the real one from the connection.
                scope.User = new SentryUser { Id = device, IpAddress = "0.0.0.0" };
            });

            var go = new GameObject("~CrashReportingAccountWatch");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AccountWatch>();
        }

        /// <summary>Tag every event with this match until <see cref="ClearMatch"/>. Main thread.</summary>
        public static void SetMatch(string matchId)
        {
            if (!SentrySdk.IsEnabled || string.IsNullOrEmpty(matchId)) return;
            SentrySdk.ConfigureScope(scope => scope.SetTag(MatchTag, matchId));
        }

        /// <summary>The match scene is going away. Main thread.</summary>
        public static void ClearMatch()
        {
            if (!SentrySdk.IsEnabled) return;
            SentrySdk.ConfigureScope(scope => scope.UnsetTag(MatchTag));
        }

        private sealed class AccountWatch : MonoBehaviour
        {
            private const float IntervalSeconds = 1f;
            private string _last = "";

            private void Start() => InvokeRepeating(nameof(Poll), 0f, IntervalSeconds);

            private void Poll()
            {
                string userId;
                try
                {
                    var provider = AuthGateway.Provider;
                    userId = provider.IsSignedIn ? provider.Current.UserId ?? "" : "";
                }
                catch (Exception)
                {
                    // AuthGateway is written not to throw; if it does, the tag simply stays as it was.
                    return;
                }

                if (userId == _last) return;
                _last = userId;
                SentrySdk.ConfigureScope(scope =>
                {
                    if (userId.Length > 0) scope.SetTag(AccountTag, userId);
                    else scope.UnsetTag(AccountTag);
                });
            }
        }
    }
}
