using System;
using Kingdom.Net.Client.Auth;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// Keeps the signed-in session across launches, in <c>PlayerPrefs</c> beside the device token and
    /// the active-account pointer.
    ///
    /// <para><b>Scoped to the local account, because <c>PlayerPrefs</c> is not.</b> Prefs are keyed on
    /// companyName/productName, so two seats launched from one machine share the store —
    /// <see cref="DeviceAccounts"/> exists because of exactly that. An unscoped key here would put
    /// both seats on one Supabase session, which is the same defect that once had both seats writing
    /// into one account.</para>
    ///
    /// <para><b>Never throws.</b> Every path degrades to <see cref="AuthSession.None"/>, i.e. to
    /// signed out. A player whose stored token is unreadable must still reach the main menu, where the
    /// sign-in gate asks them to sign in again; signed out is never a way into a match.</para>
    ///
    /// <para><b>This is a bearer credential in plaintext prefs.</b> Anything with read access to the
    /// prefs store can play as that account until the token expires. That is the same exposure the
    /// device token already has (VULN-003), and it stops being acceptable at the moment value attaches
    /// to an account — which is the security contract's latent item 2, not this ticket.</para>
    /// </summary>
    public sealed class PlayerPrefsAuthSessionStore : IAuthSessionStore
    {
        private const string SessionPrefix = "kingdom.auth.session.";
        private const string PendingPrefix = "kingdom.auth.pending.";
        private const string FormerSessionPrefix = "emberfall.auth.session.";
        private const string FormerPendingPrefix = "emberfall.auth.pending.";

        /// <summary>Field separator. A unit separator cannot occur in a JWT, a base64url verifier, an
        /// email or a decimal — all of which are the only things written here.</summary>
        private const char Separator = (char)0x1F;

        private readonly string _scope;

        /// <param name="scope">The local account this session belongs to. Defaults to the signed-in
        /// one; a corrupt registry falls back to a fixed scope rather than failing.</param>
        public PlayerPrefsAuthSessionStore(string scope = null)
        {
            _scope = string.IsNullOrEmpty(scope) ? ActiveScope() : scope;
        }

        private static string ActiveScope()
        {
            try { return DeviceAccounts.Active.Id; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Auth] account registry unreadable ({ex.GetType().Name}); " +
                                 "storing the sign-in against the device scope.");
                return "device";
            }
        }

        private string SessionKey => SessionPrefix + _scope;
        private string PendingKey => PendingPrefix + _scope;
        private string FormerSessionKey => FormerSessionPrefix + _scope;
        private string FormerPendingKey => FormerPendingPrefix + _scope;

        public AuthSession Load()
        {
            string raw = RenamedPrefs.GetString(SessionKey, FormerSessionKey);
            if (string.IsNullOrEmpty(raw)) return AuthSession.None;

            string[] parts = raw.Split(Separator);
            if (parts.Length < 5) return AuthSession.None;
            if (!long.TryParse(parts[2], out long expiresAtMs)) return AuthSession.None;

            return new AuthSession
            {
                AccessToken = parts[0],
                RefreshToken = parts[1],
                ExpiresAtMs = expiresAtMs,
                UserId = parts[3],
                Email = parts[4],
                // Not stored: recomputed from the token, so it cannot drift from it or be dropped.
                // Leaving it out of the session read back here asked every returning player for a
                // new name; see AuthSession.DisplayNameIn.
                DisplayName = AuthSession.DisplayNameIn(parts[0]),
            };
        }

        public void Save(AuthSession session)
        {
            if (session == null || !session.IsPresent) { Clear(); return; }

            PlayerPrefs.SetString(SessionKey, string.Join(Separator.ToString(),
                session.AccessToken,
                session.RefreshToken,
                session.ExpiresAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                session.UserId,
                session.Email));
            PlayerPrefs.Save();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(SessionKey);
            PlayerPrefs.DeleteKey(FormerSessionKey);
            PlayerPrefs.Save();
        }

        public PendingSignIn LoadPending()
        {
            string raw = RenamedPrefs.GetString(PendingKey, FormerPendingKey);
            if (string.IsNullOrEmpty(raw)) return PendingSignIn.None;

            string[] parts = raw.Split(Separator);
            if (parts.Length < 3) return PendingSignIn.None;

            return new PendingSignIn { Method = parts[0], AuthorizeUrl = parts[1], CodeVerifier = parts[2] };
        }

        public void SavePending(PendingSignIn pending)
        {
            if (pending == null || !pending.IsStarted) { ClearPending(); return; }

            PlayerPrefs.SetString(PendingKey, string.Join(Separator.ToString(),
                pending.Method, pending.AuthorizeUrl, pending.CodeVerifier));
            PlayerPrefs.Save();
        }

        public void ClearPending()
        {
            PlayerPrefs.DeleteKey(PendingKey);
            PlayerPrefs.DeleteKey(FormerPendingKey);
            PlayerPrefs.Save();
        }
    }
}
