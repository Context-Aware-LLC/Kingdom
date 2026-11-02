using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Client.Auth
{
    /// <summary>
    /// Owns the signed-in session for the life of the app: loads it, keeps it fresh, hands the connect
    /// path a token, and gives it up when Supabase says the credential is dead.
    ///
    /// <para><b>This never throws on the connect path.</b> <see cref="TokenForConnectAsync"/> returns
    /// the empty string for every unhappy case — no project configured, not signed in, refresh
    /// refused, network down. Empty is not a way into a match: a configured client refuses to connect
    /// without a token and shows Retry. Only tooling connects tokenless,
    /// to a server that sets <c>ServerOptions.AllowTokenlessMatches</c>. The server has no silent
    /// downgrade — it closes the socket on a bad token — so sending a token we already know is dead
    /// would turn a sign-in problem into a failed match.</para>
    ///
    /// <para><b>Capability, not policy.</b> Constructing this signs nobody in and shows nothing. It
    /// starts a sign-in only when something calls <see cref="BeginMagicLinkAsync"/> or
    /// <see cref="BeginOAuth"/>. Where and whether that happens is a product decision this type
    /// deliberately does not make.</para>
    /// </summary>
    public sealed class AuthTokenProvider
    {
        /// <summary>Refresh this far ahead of expiry. A token that passes the check and then expires
        /// during the handshake gets the socket closed, and the player sees a failed connect rather
        /// than a sign-in problem — so the margin has to cover a slow handshake, not just the call.</summary>
        public const long DefaultRefreshSkewMs = 60_000;

        private readonly IAuthBackend _backend;
        private readonly IAuthSessionStore _store;
        private readonly IClientClock _clock;
        private readonly long _refreshSkewMs;
        private readonly Action<string>? _log;

        // One refresh at a time. Two screens asking for a token at once would otherwise both spend the
        // refresh token, and Supabase rotates it — the second call redeems a token the first already
        // replaced and comes back 4xx, which reads exactly like a revoked credential and would sign
        // the player out.
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private AuthSession? _cached;

        public AuthTokenProvider(
            IAuthBackend backend,
            IAuthSessionStore store,
            IClientClock? clock = null,
            long refreshSkewMs = DefaultRefreshSkewMs,
            Action<string>? log = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? new SystemClientClock();
            _refreshSkewMs = refreshSkewMs;
            _log = log;
        }

        /// <summary>The session this device holds, read through once and then cached. Never null.</summary>
        public AuthSession Current
        {
            get
            {
                if (_cached == null) _cached = _store.Load() ?? AuthSession.None;
                return _cached;
            }
        }

        /// <summary>True when a session is stored. Says nothing about the server accepting it.</summary>
        public bool IsSignedIn => Current.IsPresent;

        /// <summary>True when this build has a project to sign into at all.</summary>
        public bool IsConfigured => _backend.IsConfigured;

        /// <summary>Raised after the stored session changes — signed in, refreshed, or signed out.
        /// Fired on whatever thread did the work; a Unity caller must marshal.</summary>
        public event Action<AuthSession>? SessionChanged;

        /// <summary>
        /// The value for <c>ClientOptions.AuthToken</c> on this connect, refreshing first if the token
        /// is at or near expiry.
        ///
        /// <para>Returns the empty string rather than throwing or blocking the boot in every failure
        /// case. See the type remarks for why that is the correct answer and not a swallowed error.</para>
        /// </summary>
        public async Task<string> TokenForConnectAsync(CancellationToken cancellationToken = default)
        {
            if (!_backend.IsConfigured) return "";

            AuthSession session = Current;
            if (!session.IsPresent) return "";
            if (!session.NeedsRefresh(_clock.NowMs, _refreshSkewMs)) return session.AccessToken;

            AuthResult refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.Ok) return refreshed.Session.AccessToken;

            // The refresh failed. If the token has not actually expired yet — we were only inside the
            // safety margin — it is still a token the server will accept, and sending it beats
            // failing a signed-in player's connect over one bad request. If it HAS expired, the server
            // would close the socket, so empty is returned and the caller fails the connect legibly.
            AuthSession after = Current;
            if (after.IsPresent && after.ExpiresAtMs > _clock.NowMs)
            {
                _log?.Invoke("[Auth] refresh failed but the current token has not expired; connecting with it.");
                return after.AccessToken;
            }

            _log?.Invoke("[Auth] no usable token for this connect.");
            return "";
        }

        /// <summary>
        /// Trade the stored refresh token for a fresh session.
        ///
        /// <para><b>A 4xx clears the session and a network failure does not.</b> Supabase answering
        /// "no" means the refresh token is revoked or spent and will never work again, so keeping it
        /// would show the player as signed in forever while every connect failed for want of a token.
        /// A request that never arrived says nothing about the credential, so it survives.</para>
        /// </summary>
        public Task<AuthResult> RefreshAsync(CancellationToken cancellationToken = default)
            => RefreshAsync(force: false, cancellationToken);

        /// <param name="force">Refresh even though the token is still good — for the one case where
        /// the token is not stale by age but by CONTENT (see <see cref="SetDisplayNameAsync"/>).</param>
        private async Task<AuthResult> RefreshAsync(bool force, CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = Current;
                if (!session.IsPresent)
                    return AuthResult.Failed(AuthFailure.Rejected, "Not signed in.");

                // Another caller may have refreshed while this one waited on the lock.
                if (!force && !session.NeedsRefresh(_clock.NowMs, _refreshSkewMs))
                    return AuthResult.Succeeded(session);

                // No ConfigureAwait(false): Adopt() below is a Unity PlayerPrefs write — see the note
                // on Adopt for why this await must keep the caller's synchronization context.
                AuthResult result = await _backend.RefreshAsync(session.RefreshToken, cancellationToken);

                if (result.Ok)
                {
                    Adopt(result.Session);
                    return result;
                }

                if (result.Failure == AuthFailure.Rejected)
                {
                    _log?.Invoke("[Auth] Supabase refused the refresh token; signing out. " + result.Detail);
                    SignOut();
                }
                else
                {
                    _log?.Invoke("[Auth] refresh did not complete (" + result.Failure + "); keeping the stored session.");
                }

                return result;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>Mail a sign-in link and remember the PKCE verifier the returning code needs.</summary>
        public async Task<AuthResult> BeginMagicLinkAsync(string email, CancellationToken cancellationToken = default)
        {
            // No ConfigureAwait(false): SavePending below is a Unity PlayerPrefs write, which Unity
            // requires to run on the thread that called in here. Capturing the caller's context (the
            // default) is what guarantees that, not an oversight — see the identical note on Adopt.
            SignInStart start = await _backend.BeginMagicLinkAsync(email, cancellationToken);
            if (start.Result.Ok) _store.SavePending(start.Pending);
            return start.Result;
        }

        /// <summary>Mail the player a six-digit code. Nothing is stored — see
        /// <see cref="SupabaseAuthBackend.BeginEmailCodeAsync"/> for why this flow keeps no local
        /// state, and what that buys.</summary>
        public Task<AuthResult> BeginEmailCodeAsync(
            string email, bool createUser, CancellationToken cancellationToken = default)
            => _backend.BeginEmailCodeAsync(email, createUser, cancellationToken);

        /// <summary>
        /// Finish a sign-in with the mailed six-digit code. On success the session is stored and
        /// <see cref="SessionChanged"/> fires.
        ///
        /// <para><b>A refusal here does not burn the code.</b> Unlike the redirect flow — where the
        /// auth code is single-use and a failed exchange has to drop the pending record — a mistyped
        /// digit leaves the mailed code exactly as valid as it was. Clearing anything on failure would
        /// force a player who fat-fingered one digit to go back to their inbox.</para>
        /// </summary>
        public async Task<AuthResult> VerifyEmailCodeAsync(
            string email, string code, CancellationToken cancellationToken = default)
        {
            // No ConfigureAwait(false): see the note on Adopt() — its PlayerPrefs write needs this
            // continuation to resume on the same thread that called in here (Unity's main thread for
            // every real caller), not wherever the backend's HTTP task happened to complete.
            AuthResult result = await _backend.VerifyEmailCodeAsync(email, code, cancellationToken);
            if (result.Ok) Adopt(result.Session);
            return result;
        }

        /// <summary>
        /// Build a provider's consent URL and remember its verifier. Returns the pending record; the
        /// caller opens <see cref="PendingSignIn.AuthorizeUrl"/> itself, because how a URL gets in
        /// front of the player is a platform question and this type has no view.
        /// </summary>
        public PendingSignIn BeginOAuth(string provider)
        {
            SignInStart start = _backend.BeginOAuth(provider);
            if (start.Result.Ok) _store.SavePending(start.Pending);
            return start.Pending;
        }

        /// <summary>The sign-in waiting to be finished, across a restart. Empty when there is none.</summary>
        public PendingSignIn Pending => _store.LoadPending() ?? PendingSignIn.None;

        /// <summary>
        /// Finish the sign-in with the code the redirect carried. On success the session is stored and
        /// the pending record is dropped; on failure the pending record is dropped too, because an
        /// auth code is single-use and retrying with the same verifier cannot work.
        /// </summary>
        public async Task<AuthResult> CompleteSignInAsync(string authCode, CancellationToken cancellationToken = default)
        {
            PendingSignIn pending = Pending;
            // No ConfigureAwait(false): ClearPending and a successful Adopt below are both Unity
            // PlayerPrefs writes — see the note on Adopt.
            AuthResult result = await _backend.CompleteAsync(pending, authCode, cancellationToken);

            _store.ClearPending();
            if (result.Ok) Adopt(result.Session);
            return result;
        }

        /// <summary>
        /// Sign in with an ID token the device's own sign-in produced (Google's native sheet on
        /// Android). On success the session is stored and <see cref="SessionChanged"/> fires, exactly
        /// as for the browser. A refusal leaves any stored session alone.
        /// </summary>
        public async Task<AuthResult> SignInWithIdTokenAsync(
            string provider, string idToken, string? rawNonce, CancellationToken cancellationToken = default)
        {
            // No ConfigureAwait(false): Adopt writes PlayerPrefs and needs this continuation on the
            // calling thread — see the note on Adopt.
            AuthResult result = await _backend.SignInWithIdTokenAsync(provider, idToken, rawNonce, cancellationToken);
            if (result.Ok) Adopt(result.Session);
            return result;
        }

        /// <summary>
        /// Sign in with a token hash the game server minted for the account a Play Games profile is
        /// linked to (#425). Stored and announced like every other sign-in; a refusal leaves any
        /// stored session alone.
        /// </summary>
        public async Task<AuthResult> SignInWithTokenHashAsync(
            string tokenHash, string type, CancellationToken cancellationToken = default)
        {
            // No ConfigureAwait(false): see the note on Adopt.
            AuthResult result = await _backend.SignInWithTokenHashAsync(tokenHash, type, cancellationToken);
            if (result.Ok) Adopt(result.Session);
            return result;
        }

        /// <summary>
        /// Set a password on the signed-in user (NOR-246), so a later sign-in on this or another
        /// device can trade it for a session instead of going through email again.
        ///
        /// <para>Requires a live session — there is no local fallback, unlike
        /// <see cref="TokenForConnectAsync"/>: setting a password is something the player asked for
        /// right now, not a background refresh, so a failure has to reach them rather than be
        /// swallowed into an empty result.</para>
        /// </summary>
        public async Task<AuthResult> SetPasswordAsync(string password, CancellationToken cancellationToken = default)
        {
            AuthSession session = Current;
            if (!session.IsPresent) return AuthResult.Failed(AuthFailure.Rejected, "Not signed in.");

            return await _backend.SetPasswordAsync(session.AccessToken, password, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Sign in with an email and password — the returning player's path. On success the session is
        /// stored and <see cref="SessionChanged"/> fires, exactly as for a mailed code.
        ///
        /// <para>A refusal leaves any stored session alone: this is a sign-in attempt, not a refresh,
        /// and a mistyped password must not sign the player out of the account they are already in.</para>
        /// </summary>
        public async Task<AuthResult> SignInWithPasswordAsync(
            string email, string password, CancellationToken cancellationToken = default)
        {
            // No ConfigureAwait(false): Adopt writes PlayerPrefs and needs this continuation on the
            // calling thread — see the note on Adopt.
            AuthResult result = await _backend.SignInWithPasswordAsync(email, password, cancellationToken);
            if (result.Ok) Adopt(result.Session);
            return result;
        }

        /// <summary>
        /// Store the display name against the identity, so a returning player on a wiped device gets
        /// it back instead of being asked to choose a "permanent" name a second time.
        ///
        /// <para><b>A successful write is followed by a forced token refresh, and it has to be.</b>
        /// Supabase stamps <c>user_metadata</c> into a JWT at mint time, so the token this device is
        /// holding was minted BEFORE the name existed and carries none. The match server reads the
        /// name a player's friends see out of that token (#409), so without a fresh one a brand-new
        /// player is shown to friends by their code until the token happens to expire. The session
        /// is updated from the refreshed token, never written by hand, so it cannot disagree with
        /// its own token about what the server said.</para>
        ///
        /// <para>The result is the WRITE's. A refresh that does not complete is logged and the old
        /// token stands — the name is stored and will ride in the next token whenever it comes.</para>
        /// </summary>
        public async Task<AuthResult> SetDisplayNameAsync(string displayName, CancellationToken cancellationToken = default)
        {
            AuthSession session = Current;
            if (!session.IsPresent) return AuthResult.Failed(AuthFailure.Rejected, "Not signed in.");

            // No ConfigureAwait(false): the refresh below ends in Adopt(), a Unity PlayerPrefs write
            // — see the note on Adopt for why these awaits keep the caller's synchronization context.
            AuthResult written = await _backend.SetDisplayNameAsync(session.AccessToken, displayName, cancellationToken);
            if (!written.Ok) return written;

            AuthResult refreshed = await RefreshAsync(force: true, cancellationToken);
            if (!refreshed.Ok)
                _log?.Invoke("[Auth] the display name was stored but the token refresh that carries it did not complete (" +
                             refreshed.Failure + "); friends see this player's code until the next refresh.");
            return written;
        }

        /// <summary>Forget the session on this device. Does not revoke it server-side — that is a
        /// separate call nothing needs yet, and the token expires on its own.</summary>
        public void SignOut()
        {
            _store.Clear();
            _store.ClearPending();
            _cached = AuthSession.None;
            SessionChanged?.Invoke(AuthSession.None);
        }

        /// <summary>
        /// <see cref="IAuthSessionStore.Save"/> is Unity's <c>PlayerPrefs</c> on every real
        /// implementation, and Unity throws if that runs off the thread that called in. This type is
        /// otherwise platform-agnostic, so the guarantee has to come from the caller: every await that
        /// leads here keeps the default synchronization-context capture (no <c>ConfigureAwait(false)</c>)
        /// so the continuation resumes on whichever thread invoked the outer call, not wherever the
        /// backend's HTTP task happened to complete. Cost a session on 2026-08-09 — stripping that
        /// capture threw <c>PlayerPrefs.SetString "can only be called from the main thread"</c> deep
        /// inside a successful sign-in, after Supabase had already accepted the code.
        /// </summary>
        private void Adopt(AuthSession session)
        {
            _cached = session ?? AuthSession.None;
            _store.Save(_cached);
            SessionChanged?.Invoke(_cached);
        }
    }
}
