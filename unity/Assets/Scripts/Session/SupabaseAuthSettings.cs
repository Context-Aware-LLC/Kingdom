using Kingdom.Net.Client.Auth;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// Which Supabase project this build signs into, as a project asset.
    ///
    /// <para><b>Create it at <c>Assets/Resources/SupabaseAuthSettings.asset</c></b> (Create ▸ Kingdom
    /// ▸ Supabase Auth Settings) once the project exists. The name and folder are load-bearing:
    /// <see cref="AuthGateway"/> finds it with <c>Resources.Load</c> and nothing else looks for it.</para>
    ///
    /// <para><b>Every release build carries it.</b> Its absence is a dev-only state: no project, so
    /// connects carry no sign-in token and only a tooling server that sets
    /// <c>ServerOptions.AllowGuestMatches</c> will seat them. A player never reaches that path. It is
    /// still <c>Resources.Load</c> returning null rather than a required reference, because a missing
    /// config must not be able to fail a boot.</para>
    ///
    /// <para><b>The anon key belongs in the build.</b> It identifies the project and grants nothing on
    /// its own; every Supabase site ships it to browsers. The service key is the one that can mint
    /// tokens, and it must never be in this asset, in this repo, or in a build.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Kingdom/Supabase Auth Settings", fileName = "SupabaseAuthSettings")]
    public sealed class SupabaseAuthSettings : ScriptableObject
    {
        [Tooltip("https://<ref>.supabase.co — the SAME project the server reads from " +
                 "KINGDOM_SUPABASE_URL. A mismatch makes every token fail as the wrong issuer.")]
        [SerializeField] private string projectUrl = "";

        [Tooltip("The project's anon/publishable key. Public by design. Never the service key.")]
        [SerializeField] private string anonKey = "";

        [Tooltip("Deep link Supabase returns the player to, e.g. emberfall://auth. Must also be " +
                 "listed in the project's allowed redirect URLs or the redirect is dropped.")]
        [SerializeField] private string redirectUrl = "emberfall://auth";

        [Tooltip("Google OAuth WEB client id that Android's native Google sign-in requests its token " +
                 "for. It must sit in the same Google Cloud project as the app's Android OAuth client " +
                 "(package + signing SHA-1), and in the Supabase Google provider's Client IDs. Empty " +
                 "turns native sign-in off; Google sign-in then uses the browser.")]
        [SerializeField] private string googleNativeClientId = "";

        public SupabaseAuthConfig ToConfig() => new SupabaseAuthConfig
        {
            ProjectUrl = projectUrl == null ? "" : projectUrl.Trim(),
            AnonKey = anonKey == null ? "" : anonKey.Trim(),
            RedirectUrl = redirectUrl == null ? "" : redirectUrl.Trim(),
            GoogleNativeClientId = googleNativeClientId == null ? "" : googleNativeClientId.Trim(),
        };
    }
}
