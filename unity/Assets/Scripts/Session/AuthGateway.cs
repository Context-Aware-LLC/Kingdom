using System;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The game's one <see cref="AuthTokenProvider"/>, composed from the project settings asset, the
    /// prefs-backed store, and the <c>UnityWebRequest</c> transport (EF-074e / NOR-231).
    ///
    /// <para><b>This is a capability, not a policy.</b> Touching it signs nobody in and shows nothing.
    /// It answers "what token, if any, goes on this connect", and it exposes the calls a sign-in
    /// surface needs. The policy — registration on first launch, unskippable, six-digit code (Sam,
    /// 2026-08-08) — lives in <c>SignInGateController</c>, and it is the only thing that opens a
    /// sign-in. What happens to a device's existing collection at sign-in is still open: that is
    /// EF-074d and it is unbuilt.</para>
    ///
    /// <para><b>A release build is always configured.</b> Every shipped build carries the
    /// <see cref="SupabaseAuthSettings"/> asset, and <c>NetworkedGameBootstrap</c> refuses to connect
    /// a configured build without a token — so a player always presents one. An unconfigured build is a dev-only state: <c>HelloMsg.AuthToken</c> stays empty, and
    /// only a tooling server that sets <c>ServerOptions.AllowTokenlessMatches</c> will seat it. Unconfigured
    /// also holds the first-launch gate open, because a gate in front of a sign-in that cannot
    /// succeed would simply stop the game booting. See <c>SignInGateController.IsRequired</c>.</para>
    ///
    /// <para><b>Rebuilt on an account switch.</b> The store is scoped to the signed-in local account,
    /// so <see cref="DeviceAccounts.SwitchTo"/>'s scene reload has to see a fresh provider or seat two
    /// would carry seat one's Supabase session. <see cref="Invalidate"/> is called from
    /// <see cref="DeviceAccounts.Invalidate"/>.</para>
    /// </summary>
    public static class AuthGateway
    {
        /// <summary>Resources path of the settings asset. Its absence means "no project configured",
        /// which is a legal state — see the type remarks.</summary>
        private const string SettingsResourcePath = "SupabaseAuthSettings";

        private static AuthTokenProvider _provider;
        private static SupabaseAuthConfig _config = SupabaseAuthConfig.Unconfigured;

        /// <summary>The provider, built on first touch. Never null.</summary>
        public static AuthTokenProvider Provider
        {
            get
            {
                if (_provider == null) _provider = Build();
                return _provider;
            }
        }

        /// <summary>True when this build has a Supabase project to sign into at all.</summary>
        public static bool IsConfigured => Provider.IsConfigured;

        /// <summary>True when this device holds a session. Says nothing about the server accepting it.</summary>
        public static bool IsSignedIn => Provider.IsSignedIn;

        /// <summary>The signed-in address, or empty. Display only — read out of the JWT without
        /// verifying it, which only the server can do.</summary>
        public static string SignedInEmail => Provider.Current.Email;

        /// <summary>
        /// The player's display name: the one held against their identity, read from the session's
        /// token. The only copy the client has (Sam, 2026-10-18) — the device used to keep its own in
        /// <c>profile.json</c>, a leftover from before accounts. The match server reads the same value
        /// off the verified token, so what this shows and what the opponent sees cannot differ.
        /// </summary>
        public static string DisplayName => Provider.Current.DisplayName ?? "";

        /// <summary>The name, or <paramref name="fallback"/> when the identity holds none.</summary>
        public static string DisplayNameOr(string fallback)
            => string.IsNullOrEmpty(DisplayName) ? fallback : DisplayName;

        /// <summary>A signed-in player whose identity has no name yet: the first-run name step is owed.
        /// False on an unconfigured build, which has no identity to hold one.</summary>
        public static bool NeedsDisplayName => IsConfigured && IsSignedIn && string.IsNullOrEmpty(DisplayName);

        /// <summary>The Google client Android's native sign-in requests its token for; empty when
        /// this build signs in to Google through the browser only.</summary>
        public static string GoogleNativeClientId
        {
            get
            {
                _ = Provider;   // loads the settings asset on first touch
                return _config.GoogleNativeClientId;
            }
        }

        /// <summary>Drop the cached provider so the next read rebuilds it against the current local
        /// account. Called by <see cref="DeviceAccounts.Invalidate"/>.</summary>
        public static void Invalidate() => _provider = null;

        /// <summary>
        /// The value for <c>ClientOptions.AuthToken</c> on this connect, refreshing first if needed.
        /// Returns the empty string for every failure rather than throwing. Empty is not a way into a
        /// match: on a configured build the caller treats it as a failed sign-in and stops the connect
        /// (<c>NetworkedGameBootstrap</c>).
        /// </summary>
        public static async Task<string> TokenForConnectAsync()
        {
            try
            {
                return await Provider.TokenForConnectAsync();
            }
            catch (Exception ex)
            {
                // The provider is written not to throw, and the connect path must not be the place
                // that finds out it was wrong about that.
                Debug.LogWarning($"[Auth] could not produce a token ({ex.GetType().Name}: {ex.Message}); " +
                                 "connecting without one.");
                return "";
            }
        }

        private static AuthTokenProvider Build()
        {
            SupabaseAuthConfig config = LoadConfig();
            _config = config;
            var backend = new SupabaseAuthBackend(config, new UnityWebRequestAuthHttp());

            return new AuthTokenProvider(
                backend,
                new PlayerPrefsAuthSessionStore(),
                log: message => Debug.Log(message));
        }

        private static SupabaseAuthConfig LoadConfig()
        {
            SupabaseAuthSettings settings;
            try { settings = Resources.Load<SupabaseAuthSettings>(SettingsResourcePath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Auth] {SettingsResourcePath} could not be loaded " +
                                 $"({ex.GetType().Name}); sign-in is unavailable this session.");
                return SupabaseAuthConfig.Unconfigured;
            }

            if (settings == null) return SupabaseAuthConfig.Unconfigured;

            SupabaseAuthConfig config = settings.ToConfig();
            if (!config.IsConfigured)
                Debug.LogWarning($"[Auth] {SettingsResourcePath} exists but has no project URL or anon " +
                                 "key; sign-in is unavailable this session.");
            return config;
        }
    }
}
