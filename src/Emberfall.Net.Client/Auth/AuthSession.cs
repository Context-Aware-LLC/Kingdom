using System;

namespace Emberfall.Net.Client.Auth
{
    /// <summary>Why a sign-in call did not produce a session. Used to decide whether the stored
    /// session survives: only <see cref="Rejected"/> means the credential itself is dead.</summary>
    public enum AuthFailure
    {
        None = 0,

        /// <summary>No Supabase project is configured on this build, so there is nothing to talk to.
        /// The honest guest state, not an error to surface.</summary>
        NotConfigured,

        /// <summary>The request never reached Supabase, or the reply never came back. Transient —
        /// the stored session is still presumed good.</summary>
        Network,

        /// <summary>Supabase answered and said no (4xx). For a refresh this means the refresh token
        /// is revoked or expired, and the stored session must be cleared.</summary>
        Rejected,

        /// <summary>A 2xx whose body we could not read as a session. Treated as a failed call, never
        /// as a partial sign-in.</summary>
        Malformed,

        /// <summary>Supabase has never seen that email address, on a call that deliberately refused to
        /// create one for it (<see cref="SupabaseAuthBackend.BeginEmailCodeAsync"/> with
        /// <c>createUser: false</c>).
        ///
        /// <para><b>This is only ever produced by the RETURNING branch, and never by a refresh.</b>
        /// It is a distinct member rather than a flavour of <see cref="Rejected"/> because the two
        /// need opposite responses: Rejected means a credential is dead and the stored session must
        /// go, while this means the player typed the wrong address and holds nothing at all yet.
        /// <see cref="AuthTokenProvider"/>'s session-clearing check keys on Rejected alone, so this
        /// member cannot cost anyone their session.</para>
        ///
        /// <para><b>It is an account-enumeration oracle, knowingly (Sam, 2026-09-01).</b> Anyone can
        /// ask this form whether an address has an Emberfall account. That is the accepted price of
        /// the alternative, which is worse: with <c>create_user: true</c> a returning player who
        /// mistypes their address gets a real code mailed to the typo, redeems it, and lands in a
        /// brand-new empty identity — losing their collection, silently, with the game reporting
        /// success. Note that the PASSWORD path deliberately does NOT distinguish
        /// (<see cref="SupabaseAuthBackend.SignInWithPasswordAsync"/>); that asymmetry is not an
        /// oversight, it is that a password form has no equivalent failure to trade against.</para></summary>
        NoSuchAccount,
    }

    /// <summary>
    /// One Supabase session as this device holds it: the JWT we put on the wire, the refresh token
    /// that renews it, and when it stops being accepted.
    ///
    /// <para><b>The empty session is the guest, and it is a first-class state.</b>
    /// <see cref="None"/> is what a player who has never signed in has, what a signed-out player has,
    /// and what a player whose refresh was rejected falls back to. Every one of those must reach a
    /// match — <c>AuthToken = ""</c> is the pre-EF-074b behaviour the server still supports.</para>
    ///
    /// <para><b>Nothing here is verified locally.</b> <see cref="UserId"/> and <see cref="Email"/> are
    /// read out of the JWT payload without checking its signature, because the client has no business
    /// holding a key that could check one. They are for showing the player which account they are in.
    /// The check that counts is <c>SupabaseTokenVerifier</c> on the server, against the project JWKS.
    /// Never gate anything of value on these two fields.</para>
    /// </summary>
    public sealed record AuthSession
    {
        /// <summary>The signed-out state. Reference-comparable, but prefer <see cref="IsPresent"/>.</summary>
        public static readonly AuthSession None = new AuthSession();

        /// <summary>The Supabase JWT. This is the value that goes into <c>HelloMsg.AuthToken</c>.</summary>
        public string AccessToken { get; init; } = "";

        /// <summary>Exchanged for a fresh access token. Longer-lived and single-use: Supabase rotates
        /// it on every refresh, so the reply's refresh token must replace this one.</summary>
        public string RefreshToken { get; init; } = "";

        /// <summary>When <see cref="AccessToken"/> stops being accepted, on <see cref="IClientClock"/>'s
        /// scale. Derived from the reply's <c>expires_in</c> against our own clock rather than its
        /// <c>expires_at</c>, so a device whose clock is wrong refreshes early or late by the same
        /// amount it is wrong — instead of never.</summary>
        public long ExpiresAtMs { get; init; }

        /// <summary>The Supabase user id (<c>sub</c>). Display only — see the type remarks.</summary>
        public string UserId { get; init; } = "";

        /// <summary>The address the player signed in with. Display only — see the type remarks.</summary>
        public string Email { get; init; } = "";

        /// <summary>
        /// The display name Supabase holds for this user (<c>user_metadata.display_name</c>), or empty
        /// when it holds none.
        ///
        /// <para><b>This is the only piece of the player that survives a reinstall today.</b> The
        /// roster, the decks and the profile are files under
        /// <c>persistentDataPath</c> and go with the app; the entitlement ledger is server-side and
        /// keyed on the authenticated subject. The name sat in neither camp — which is why a returning
        /// player was asked to choose one again, on a screen that calls it permanent. Carrying it in
        /// the token puts it on the same side of the line as the ledger, at the cost of one nested
        /// read and no extra request.</para>
        ///
        /// <para><b>Display only, like <see cref="UserId"/> and <see cref="Email"/> — and here it
        /// matters more.</b> The claim is unverified client-side, and the name a player presents in a
        /// match is re-checked server-side on arrival regardless (VULN-002, <c>MatchGate</c>). Nothing
        /// downstream may trust this string; it is a better default than an empty field, and that is
        /// all it is.</para>
        ///
        /// <para>Empty is ordinary, not a failure: every account created before this was written has
        /// no metadata name, and the caller's answer is to ask for one exactly as it always did.</para>
        /// </summary>
        public string DisplayName { get; init; } = "";

        /// <summary>True when this device believes it is signed in. Says nothing about whether the
        /// server will accept the token.</summary>
        public bool IsPresent => AccessToken.Length > 0;

        /// <summary>
        /// True when the token is inside <paramref name="skewMs"/> of expiry, or already past it.
        ///
        /// <para>The skew is why this is not <c>ExpiresAtMs &lt;= nowMs</c>: a token that passes here
        /// and expires during the handshake gets the socket closed, and the player sees a failed
        /// connect rather than a sign-in problem.</para>
        /// </summary>
        public bool NeedsRefresh(long nowMs, long skewMs)
            => IsPresent && ExpiresAtMs - skewMs <= nowMs;
    }

    /// <summary>
    /// The outcome of one sign-in, exchange or refresh call. A failure always carries
    /// <see cref="AuthSession.None"/> — there is no partially signed-in result.
    /// </summary>
    public readonly struct AuthResult
    {
        public bool Ok { get; }
        public AuthFailure Failure { get; }

        /// <summary>Supabase's own words when it had any, for logs and for a message on screen.
        /// Never contains a token: only the error fields of an error body are read into it.</summary>
        public string Detail { get; }

        public AuthSession Session { get; }

        private AuthResult(bool ok, AuthFailure failure, string detail, AuthSession session)
        {
            Ok = ok;
            Failure = failure;
            Detail = detail ?? "";
            Session = session ?? AuthSession.None;
        }

        /// <summary>A call that succeeded and produced a session (verify, exchange, refresh).</summary>
        public static AuthResult Succeeded(AuthSession session)
            => new AuthResult(true, AuthFailure.None, "", session);

        /// <summary>A call that succeeded and produces no session by design — sending a magic link
        /// is complete when the mail is away; the session arrives when the player opens it.</summary>
        public static AuthResult Accepted()
            => new AuthResult(true, AuthFailure.None, "", AuthSession.None);

        public static AuthResult Failed(AuthFailure failure, string detail = "")
            => new AuthResult(false, failure, detail, AuthSession.None);
    }
}
