using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Auth;

namespace Emberfall.Net.Client.Tests.Auth
{
    /// <summary>A clock the test moves by hand — the auth layer's refresh decisions are all
    /// time-relative, and a test that waited for a real minute would not be run.</summary>
    internal sealed class FakeClock : IClientClock
    {
        public long NowMs { get; set; }

        public FakeClock(long startMs = 1_000_000) => NowMs = startMs;

        public void Advance(long ms) => NowMs += ms;
    }

    /// <summary>
    /// A scripted <see cref="IAuthHttp"/>. Every request is recorded so a test can assert what went
    /// out (PKCE challenge present, refresh token sent) as well as what came back.
    /// </summary>
    internal sealed class FakeAuthHttp : IAuthHttp
    {
        private readonly Queue<AuthHttpResponse> _replies = new Queue<AuthHttpResponse>();

        public List<(string Url, string Body)> Requests { get; } = new List<(string, string)>();

        public FakeAuthHttp Reply(int status, string body)
        {
            _replies.Enqueue(AuthHttpResponse.Completed(status, body));
            return this;
        }

        public FakeAuthHttp ReplyTransportFailure(string detail = "no route to host")
        {
            _replies.Enqueue(AuthHttpResponse.TransportFailure(detail));
            return this;
        }

        public Task<AuthHttpResponse> PostJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => Send(url, jsonBody);

        public Task<AuthHttpResponse> PutJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
            => Send(url, jsonBody);

        private Task<AuthHttpResponse> Send(string url, string jsonBody)
        {
            Requests.Add((url, jsonBody));
            if (_replies.Count == 0)
                throw new InvalidOperationException("FakeAuthHttp ran out of scripted replies for " + url);
            return Task.FromResult(_replies.Dequeue());
        }
    }

    /// <summary>An <see cref="IAuthBackend"/> that answers from a script, for the provider tests that
    /// care about session lifecycle rather than about HTTP.</summary>
    internal sealed class FakeAuthBackend : IAuthBackend
    {
        private readonly Queue<AuthResult> _refreshResults = new Queue<AuthResult>();

        public bool IsConfigured { get; set; } = true;
        public int RefreshCalls { get; private set; }
        public string? LastRefreshToken { get; private set; }
        public AuthResult CompleteResult { get; set; } = AuthResult.Failed(AuthFailure.Rejected);
        public SignInStart MagicLinkResult { get; set; }
            = new SignInStart(AuthResult.Accepted(), new PendingSignIn { Method = "email", CodeVerifier = "verifier" });
        public SignInStart OAuthResult { get; set; }
            = new SignInStart(AuthResult.Accepted(),
                new PendingSignIn { Method = "google", AuthorizeUrl = "https://p/authorize", CodeVerifier = "verifier" });

        public FakeAuthBackend OnRefresh(AuthResult result)
        {
            _refreshResults.Enqueue(result);
            return this;
        }

        public AuthResult EmailCodeSendResult { get; set; } = AuthResult.Accepted();
        public AuthResult VerifyResult { get; set; } = AuthResult.Failed(AuthFailure.Rejected, "wrong code");
        public int VerifyCalls { get; private set; }
        public string? LastVerifiedEmail { get; private set; }
        public string? LastVerifiedCode { get; private set; }

        public Task<SignInStart> BeginMagicLinkAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(MagicLinkResult);

        public Task<AuthResult> BeginEmailCodeAsync(
            string email, bool createUser, CancellationToken cancellationToken = default)
            => Task.FromResult(EmailCodeSendResult);

        public Task<AuthResult> VerifyEmailCodeAsync(
            string email, string code, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            LastVerifiedEmail = email;
            LastVerifiedCode = code;
            return Task.FromResult(VerifyResult);
        }

        public SignInStart BeginOAuth(string provider) => OAuthResult;

        public Task<AuthResult> CompleteAsync(PendingSignIn pending, string authCode, CancellationToken cancellationToken = default)
            => Task.FromResult(pending != null && pending.IsStarted
                ? CompleteResult
                : AuthResult.Failed(AuthFailure.Rejected, "not started here"));

        public Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            LastRefreshToken = refreshToken;
            if (_refreshResults.Count == 0)
                throw new InvalidOperationException("FakeAuthBackend ran out of scripted refresh results.");
            return Task.FromResult(_refreshResults.Dequeue());
        }

        public AuthResult SetPasswordResult { get; set; } = AuthResult.Accepted();
        public string? LastPasswordAccessToken { get; private set; }
        public string? LastPassword { get; private set; }

        public Task<AuthResult> SetPasswordAsync(string accessToken, string password, CancellationToken cancellationToken = default)
        {
            LastPasswordAccessToken = accessToken;
            LastPassword = password;
            return Task.FromResult(SetPasswordResult);
        }

        public AuthResult PasswordSignInResult { get; set; } = AuthResult.Failed(AuthFailure.Rejected, "Invalid login credentials");
        public int PasswordSignInCalls { get; private set; }
        public string? LastPasswordSignInEmail { get; private set; }
        public string? LastPasswordSignInPassword { get; private set; }

        public Task<AuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            PasswordSignInCalls++;
            LastPasswordSignInEmail = email;
            LastPasswordSignInPassword = password;
            return Task.FromResult(PasswordSignInResult);
        }

        public AuthResult SetDisplayNameResult { get; set; } = AuthResult.Accepted();
        public string? LastDisplayNameAccessToken { get; private set; }
        public string? LastDisplayName { get; private set; }

        public Task<AuthResult> SetDisplayNameAsync(string accessToken, string displayName, CancellationToken cancellationToken = default)
        {
            LastDisplayNameAccessToken = accessToken;
            LastDisplayName = displayName;
            return Task.FromResult(SetDisplayNameResult);
        }
    }

    internal static class Jwt
    {
        /// <summary>An unsigned token whose payload is the given JSON. The client never verifies a
        /// signature — that is the server's job against the project JWKS — so a real one would prove
        /// nothing extra here.</summary>
        public static string WithPayload(string payloadJson)
            => Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") + "." + Base64Url(payloadJson) + ".not-a-signature";

        public static string ForUser(string sub, string email)
            => WithPayload("{\"sub\":\"" + sub + "\",\"email\":\"" + email + "\",\"aud\":\"authenticated\"}");

        /// <summary>A token carrying the shape a real Supabase JWT has: <c>user_metadata</c> is a
        /// nested object sitting between the scalar claims, so a reader that only walks the top level
        /// has to step over it and a reader that wants the name has to descend into it.</summary>
        public static string ForUserNamed(string sub, string email, string displayName)
            => WithPayload("{\"sub\":\"" + sub + "\",\"email\":\"" + email + "\"," +
                           "\"app_metadata\":{\"provider\":\"email\"}," +
                           "\"user_metadata\":{\"email_verified\":true,\"display_name\":\"" + displayName + "\"}," +
                           "\"aud\":\"authenticated\"}");

        private static string Base64Url(string text)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static class TokenReply
    {
        /// <summary>A Supabase token reply, including the nested <c>user</c> object the real one
        /// carries — the parser has to walk past it rather than into it.</summary>
        public static string Body(string accessToken, string refreshToken, int expiresInSeconds)
            => "{\"access_token\":\"" + accessToken + "\"," +
               "\"token_type\":\"bearer\"," +
               "\"expires_in\":" + expiresInSeconds + "," +
               "\"expires_at\":1893456000," +
               "\"refresh_token\":\"" + refreshToken + "\"," +
               "\"user\":{\"id\":\"nested-id\",\"email\":\"nested@example.com\"," +
               "\"confidence\":0.5,\"app_metadata\":{\"provider\":\"email\"},\"identities\":[{\"id\":\"other\"}]}}";
    }
}
