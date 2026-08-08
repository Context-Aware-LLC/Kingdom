using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Client.Auth
{
    /// <summary>
    /// Which Supabase project this build signs into.
    ///
    /// <para><b>The anon key is public and belongs in the client.</b> It identifies the project and
    /// carries no authority on its own — every Supabase deployment ships it to browsers. The key that
    /// must never appear here is the service key, which can mint tokens; the server refuses symmetric
    /// algorithms partly so that a leaked one still cannot forge a seat.</para>
    ///
    /// <para><b>Unconfigured is a legal state, not a failure.</b> A build with no project set plays as
    /// a guest exactly as it did before EF-074, which is the state every build is in until the
    /// project exists.</para>
    /// </summary>
    public sealed record SupabaseAuthConfig
    {
        /// <summary>The project URL, e.g. <c>https://&lt;ref&gt;.supabase.co</c>. The server reads the
        /// same value from <c>EMBERFALL_SUPABASE_URL</c> and derives its JWKS endpoint from it, so the
        /// two ends must name the same project or every token is rejected as the wrong issuer.</summary>
        public string ProjectUrl { get; init; } = "";

        /// <summary>The project's anon (publishable) key.</summary>
        public string AnonKey { get; init; } = "";

        /// <summary>Where Supabase sends the player back after a magic link or an OAuth consent — the
        /// game's deep link. Must also be listed in the project's allowed redirect URLs, or Supabase
        /// drops the redirect and the player lands nowhere.</summary>
        public string RedirectUrl { get; init; } = "";

        public bool IsConfigured => ProjectUrl.Length > 0 && AnonKey.Length > 0;

        public static readonly SupabaseAuthConfig Unconfigured = new SupabaseAuthConfig();
    }

    /// <summary>
    /// A sign-in that has been started and is waiting for the player to come back — from their inbox,
    /// or from a provider's consent screen.
    ///
    /// <para><b>Hold the verifier, not just the URL.</b> PKCE only proves anything if the code that
    /// comes back is redeemed by whoever started the exchange. Losing this record means the returning
    /// code cannot be redeemed and the player has to start again.</para>
    /// </summary>
    public sealed record PendingSignIn
    {
        /// <summary>Nothing was started. <see cref="IsStarted"/> is false.</summary>
        public static readonly PendingSignIn None = new PendingSignIn();

        /// <summary>Provider name for OAuth (<c>google</c>, <c>apple</c>, …), or <c>email</c> for a
        /// magic link. Diagnostic only.</summary>
        public string Method { get; init; } = "";

        /// <summary>The page to open in the player's browser. Empty for a magic link — there the link
        /// is in the mail, and nothing opens here.</summary>
        public string AuthorizeUrl { get; init; } = "";

        /// <summary>The PKCE verifier this sign-in must be completed with. A secret for its lifetime;
        /// do not log it.</summary>
        public string CodeVerifier { get; init; } = "";

        public bool IsStarted => CodeVerifier.Length > 0;
    }

    /// <summary>
    /// What starting a sign-in produced: whether the request went through, and the pending record the
    /// caller must hold onto until the player comes back. The two travel together because a started
    /// sign-in that loses its verifier cannot be finished.
    /// </summary>
    public readonly struct SignInStart
    {
        public AuthResult Result { get; }
        public PendingSignIn Pending { get; }

        public SignInStart(AuthResult result, PendingSignIn pending)
        {
            Result = result;
            Pending = pending ?? PendingSignIn.None;
        }
    }

    /// <summary>
    /// The Supabase Auth calls, as a seam. <see cref="AuthTokenProvider"/> is written against this so
    /// its refresh and guest-fallback behaviour can be tested without a network.
    /// </summary>
    public interface IAuthBackend
    {
        /// <summary>True when there is a project to talk to.</summary>
        bool IsConfigured { get; }

        /// <summary>Mail the player a sign-in link. Succeeds when the mail is away; the session
        /// arrives later, when they open it and the link returns a code to
        /// <see cref="SupabaseAuthConfig.RedirectUrl"/>.</summary>
        Task<SignInStart> BeginMagicLinkAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>Mail the player an eight-digit code. Succeeds when the mail is away; nothing is held
        /// on this device, so the code can be finished from a fresh launch.</summary>
        Task<AuthResult> BeginEmailCodeAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>Trade a mailed eight-digit code for a session.</summary>
        Task<AuthResult> VerifyEmailCodeAsync(string email, string code, CancellationToken cancellationToken = default);

        /// <summary>Build the provider's consent URL. Nothing is sent here — the caller opens the URL
        /// and waits for the redirect.</summary>
        SignInStart BeginOAuth(string provider);

        /// <summary>Redeem the code that came back on the redirect for a session.</summary>
        Task<AuthResult> CompleteAsync(PendingSignIn pending, string authCode, CancellationToken cancellationToken = default);

        /// <summary>Trade a refresh token for a fresh session.</summary>
        Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Supabase Auth over <see cref="IAuthHttp"/>, using PKCE for both sign-in methods.
    ///
    /// <para><b>One flow, two entrances.</b> Magic link and OAuth differ only in how the player gets
    /// back to us: a link in the mail, or a provider redirect. Both return an auth code to the game's
    /// redirect URL, and both are finished by the same <c>token?grant_type=pkce</c> exchange — which
    /// is why the ticket's "one JWT whichever method they used" holds on this end too, not just the
    /// server's.</para>
    ///
    /// <para><b>Nothing here decides where sign-in appears.</b> This type sends requests when it is
    /// asked to; it opens no window, shows nothing, and starts nothing on its own.</para>
    /// </summary>
    public sealed class SupabaseAuthBackend : IAuthBackend
    {
        private readonly SupabaseAuthConfig _config;
        private readonly IAuthHttp _http;
        private readonly IClientClock _clock;
        private readonly Func<string> _verifierFactory;
        private readonly string _baseUrl;

        public SupabaseAuthBackend(
            SupabaseAuthConfig config,
            IAuthHttp http,
            IClientClock? clock = null,
            Func<string>? verifierFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clock = clock ?? new SystemClientClock();
            _verifierFactory = verifierFactory ?? Pkce.NewVerifier;
            _baseUrl = _config.ProjectUrl.TrimEnd('/') + "/auth/v1";
        }

        public bool IsConfigured => _config.IsConfigured;

        public async Task<SignInStart> BeginMagicLinkAsync(string email, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return Nothing(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (string.IsNullOrWhiteSpace(email))
                return Nothing(AuthFailure.Rejected, "Enter an email address.");

            string verifier = _verifierFactory();
            string body = AuthJson.ObjectWithRaw(
                "\"create_user\":true",
                AuthJson.Field("email", email.Trim()),
                AuthJson.Field("code_challenge", Pkce.Challenge(verifier)),
                AuthJson.Field("code_challenge_method", "s256"));

            var response = await _http.PostJsonAsync(
                Url("otp", ("redirect_to", _config.RedirectUrl)), body, Headers(), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess) return new SignInStart(Interpret(response), PendingSignIn.None);

            return new SignInStart(
                AuthResult.Accepted(),
                new PendingSignIn { Method = "email", AuthorizeUrl = "", CodeVerifier = verifier });
        }

        /// <summary>
        /// Ask Supabase to mail an eight-digit code (Sam, 2026-08-08).
        ///
        /// <para><b>No PKCE here, and that is the point.</b> The verifier exists to bind a code coming
        /// back over a redirect to the device that started the sign-in. A code the player reads with
        /// their own eyes and types into this overlay never travels a redirect, so there is nothing to
        /// bind and nothing to keep. The practical consequence is the one that made Sam pick it: no
        /// deep link has to be registered on any platform, and a player who force-quits the game between
        /// the mail arriving and typing it in can still finish, because this device is holding nothing
        /// that could be lost.</para>
        ///
        /// <para>Same <c>otp</c> endpoint as the magic link — the mail Supabase sends carries both a
        /// link and a code, and which one the player sees is the project's email template, not this
        /// call.</para>
        /// </summary>
        public async Task<AuthResult> BeginEmailCodeAsync(string email, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured) return AuthResult.Failed(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (string.IsNullOrWhiteSpace(email))
                return AuthResult.Failed(AuthFailure.Rejected, "Enter an email address.");

            string body = AuthJson.ObjectWithRaw(
                "\"create_user\":true",
                AuthJson.Field("email", email.Trim()));

            var response = await _http.PostJsonAsync(Url("otp"), body, Headers(), cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccess ? AuthResult.Accepted() : Interpret(response);
        }

        /// <summary>
        /// Redeem a mailed eight-digit code for a session.
        ///
        /// <para><b>The address must match the one the code was mailed to.</b> Supabase keys the code to
        /// the address, so a player who edits the email field after asking for a code gets a refusal
        /// that reads like a wrong code. The overlay keeps the address it sent to rather than re-reading
        /// the field, which is why that field is locked once a code is on its way.</para>
        /// </summary>
        public async Task<AuthResult> VerifyEmailCodeAsync(
            string email, string code, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured) return AuthResult.Failed(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (string.IsNullOrWhiteSpace(email))
                return AuthResult.Failed(AuthFailure.Rejected, "Enter an email address.");

            string digits = EmailCode.Normalize(code);
            if (digits.Length != EmailCode.Length)
                return AuthResult.Failed(AuthFailure.Rejected, "Enter the eight-digit code from the email.");

            string body = AuthJson.Object(
                AuthJson.Field("type", "email"),
                AuthJson.Field("email", email.Trim()),
                AuthJson.Field("token", digits));

            var response = await _http.PostJsonAsync(Url("verify"), body, Headers(), cancellationToken)
                .ConfigureAwait(false);

            return ReadSession(response);
        }

        public SignInStart BeginOAuth(string provider)
        {
            if (!IsConfigured)
                return Nothing(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (string.IsNullOrWhiteSpace(provider))
                return Nothing(AuthFailure.Rejected, "No provider was named.");

            string verifier = _verifierFactory();
            string url = Url("authorize",
                ("provider", provider),
                ("redirect_to", _config.RedirectUrl),
                ("code_challenge", Pkce.Challenge(verifier)),
                ("code_challenge_method", "s256"));

            return new SignInStart(
                AuthResult.Accepted(),
                new PendingSignIn { Method = provider, AuthorizeUrl = url, CodeVerifier = verifier });
        }

        public async Task<AuthResult> CompleteAsync(
            PendingSignIn pending, string authCode, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured) return AuthResult.Failed(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (pending == null || !pending.IsStarted)
                return AuthResult.Failed(AuthFailure.Rejected, "This sign-in was not started on this device.");
            if (string.IsNullOrEmpty(authCode))
                return AuthResult.Failed(AuthFailure.Rejected, "The sign-in link returned no code.");

            string body = AuthJson.Object(
                AuthJson.Field("auth_code", authCode),
                AuthJson.Field("code_verifier", pending.CodeVerifier));

            var response = await _http.PostJsonAsync(
                Url("token", ("grant_type", "pkce")), body, Headers(), cancellationToken)
                .ConfigureAwait(false);

            return ReadSession(response);
        }

        public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured) return AuthResult.Failed(AuthFailure.NotConfigured, "No Supabase project is configured.");
            if (string.IsNullOrEmpty(refreshToken))
                return AuthResult.Failed(AuthFailure.Rejected, "No refresh token is stored.");

            string body = AuthJson.Object(AuthJson.Field("refresh_token", refreshToken));

            var response = await _http.PostJsonAsync(
                Url("token", ("grant_type", "refresh_token")), body, Headers(), cancellationToken)
                .ConfigureAwait(false);

            return ReadSession(response);
        }

        /// <summary>
        /// Turn a 2xx token reply into a session. Anything missing an access token, a refresh token or
        /// a lifetime is <see cref="AuthFailure.Malformed"/> rather than a half-session — a client
        /// that stores a session with no refresh token has signed the player in for exactly one hour
        /// and then silently drops them back to guest.
        /// </summary>
        private AuthResult ReadSession(AuthHttpResponse response)
        {
            if (!response.IsSuccess) return Interpret(response);

            if (!AuthJson.TryReadTopLevel(response.Body, out var fields))
                return AuthResult.Failed(AuthFailure.Malformed, "The sign-in reply was not a JSON object.");

            if (!AuthJson.TryString(fields, "access_token", out string accessToken))
                return AuthResult.Failed(AuthFailure.Malformed, "The sign-in reply carried no access token.");
            if (!AuthJson.TryString(fields, "refresh_token", out string refreshToken))
                return AuthResult.Failed(AuthFailure.Malformed, "The sign-in reply carried no refresh token.");
            if (!AuthJson.TryInt64(fields, "expires_in", out long expiresInSeconds) || expiresInSeconds <= 0)
                return AuthResult.Failed(AuthFailure.Malformed, "The sign-in reply carried no lifetime.");

            string userId = "";
            string email = "";
            if (AuthJson.TryReadJwtPayload(accessToken, out var claims))
            {
                AuthJson.TryString(claims, "sub", out userId);
                AuthJson.TryString(claims, "email", out email);
            }

            // Expiry is dated against OUR clock, from the reply's expires_in — never from its
            // expires_at. The refresh decision is made by this same clock, so keying the deadline to
            // Supabase's instead means a device whose clock is off by an hour either refreshes
            // constantly or never refreshes at all.
            return AuthResult.Succeeded(new AuthSession
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAtMs = _clock.NowMs + expiresInSeconds * 1000L,
                UserId = userId,
                Email = email,
            });
        }

        /// <summary>
        /// Classify a non-2xx. The split that matters is 4xx from everything else: a 4xx on refresh
        /// means the refresh token is dead and the stored session must go, while a 5xx or a dead
        /// socket means try again later with the same one.
        /// </summary>
        private static AuthResult Interpret(AuthHttpResponse response)
        {
            if (response.IsTransportFailure)
                return AuthResult.Failed(AuthFailure.Network, response.Body);

            string detail = Describe(response.Body);
            if (response.StatusCode >= 400 && response.StatusCode < 500)
                return AuthResult.Failed(AuthFailure.Rejected, detail);

            return AuthResult.Failed(
                AuthFailure.Network,
                detail.Length > 0
                    ? detail
                    : "Supabase returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>Pull Supabase's own message out of an error body, reading ONLY the known error
        /// fields so a token can never end up in a log line.</summary>
        private static string Describe(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields)) return "";
            if (AuthJson.TryString(fields, "error_description", out string described)) return described;
            if (AuthJson.TryString(fields, "msg", out string msg)) return msg;
            if (AuthJson.TryString(fields, "message", out string message)) return message;
            if (AuthJson.TryString(fields, "error", out string error)) return error;
            return "";
        }

        private static SignInStart Nothing(AuthFailure failure, string detail)
            => new SignInStart(AuthResult.Failed(failure, detail), PendingSignIn.None);

        private IReadOnlyList<KeyValuePair<string, string>> Headers()
            => new[]
            {
                new KeyValuePair<string, string>("apikey", _config.AnonKey),
                new KeyValuePair<string, string>("Authorization", "Bearer " + _config.AnonKey),
                new KeyValuePair<string, string>("Content-Type", "application/json"),
            };

        private string Url(string path, params (string Name, string Value)[] query)
        {
            var sb = new StringBuilder(_baseUrl).Append('/').Append(path);
            bool first = true;
            foreach (var (name, value) in query)
            {
                if (string.IsNullOrEmpty(value)) continue;
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The shape of the eight-digit code Supabase mails, in one place so the field, the button's enabled
    /// state and the request all agree on what "a complete code" is.
    ///
    /// <para><b>Normalising is not cosmetic.</b> A code copied out of a mail client arrives with a
    /// trailing space, a non-breaking space, or split as "123 456" often enough that rejecting those
    /// reads to the player as the code being wrong — and their next move is to request another one,
    /// which invalidates the one they actually have.</para>
    /// </summary>
    public static class EmailCode
    {
        /// <summary>How many digits Supabase's email OTP carries. Confirmed 8 against the live
        /// project on 2026-08-08 (Sam) — Supabase's actual OTP length, not the 6 EF-074e assumed.</summary>
        public const int Length = 8;

        /// <summary>Keep the digits, drop everything else, and stop at <see cref="Length"/>.</summary>
        public static string Normalize(string? code)
        {
            if (string.IsNullOrEmpty(code)) return "";

            var sb = new StringBuilder(Length);
            foreach (char c in code)
            {
                if (c < '0' || c > '9') continue;
                sb.Append(c);
                if (sb.Length == Length) break;
            }
            return sb.ToString();
        }

        /// <summary>True when <paramref name="code"/> holds a full code once normalised.</summary>
        public static bool IsComplete(string? code) => Normalize(code).Length == Length;
    }

    /// <summary>
    /// PKCE, as RFC 7636 defines it: a random verifier this device keeps, and the SHA-256 of it that
    /// goes out with the request. It is what stops an auth code intercepted on the way back — off a
    /// deep link another app registered, out of a browser history — from being redeemable by anyone
    /// but the client that started the sign-in.
    /// </summary>
    internal static class Pkce
    {
        /// <summary>A fresh 32-byte verifier, base64url with no padding (43 characters, inside the
        /// spec's 43–128).</summary>
        public static string NewVerifier()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create())
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
