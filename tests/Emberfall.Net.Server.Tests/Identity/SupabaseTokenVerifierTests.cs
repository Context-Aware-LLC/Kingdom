using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Identity
{
    /// <summary>
    /// EF-074a — Supabase JWT verification (NOR-227).
    ///
    /// Tests self-mint tokens with a local RSA key served via a fake JWKS endpoint.
    /// No network, no Supabase project required.
    ///
    /// DONE-WHEN: dotnet test filtered to this class passes and stdout is pasted into the issue.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SupabaseTokenVerifierTests
    {
        private const string ProjectUrl = "https://test.supabase.co";
        private const string Issuer = "https://test.supabase.co/auth/v1";
        private const string JwksUrl = "https://test.supabase.co/auth/v1/.well-known/jwks.json";

        private RSA _rsa = null!;
        private RsaSecurityKey _key = null!;
        private JsonWebTokenHandler _handler = null!;
        private SupabaseTokenVerifier _verifier = null!;

        [SetUp]
        public void SetUp()
        {
            _rsa = RSA.Create(2048);
            _key = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
            _handler = new JsonWebTokenHandler();

            var rsaParams = _rsa.ExportParameters(includePrivateParameters: false);
            string n = Base64UrlEncode(rsaParams.Modulus!);
            string e = Base64UrlEncode(rsaParams.Exponent!);
            // JWKS with one RSA public key.
            string jwksJson = $@"{{""keys"":[{{""kty"":""RSA"",""kid"":""{_key.KeyId}"",""use"":""sig"",""alg"":""RS256"",""n"":""{n}"",""e"":""{e}""}}]}}";

            var fakeHttp = new HttpClient(new FakeJwksHandler(JwksUrl, jwksJson));
            _verifier = new SupabaseTokenVerifier(ProjectUrl, fakeHttp);
        }

        [TearDown]
        public void TearDown()
        {
            _verifier.Dispose();
            _rsa.Dispose();
        }

        // ── Happy path ───────────────────────────────────────────────────────────

        [Test]
        public async Task Valid_Token_Returns_Verified_Identity()
        {
            string token = Mint();
            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Identity!.Subject, Is.EqualTo("user-abc"));
            Assert.That(result.Identity.Email, Is.EqualTo("player@example.com"));
            Assert.That(result.Identity.EmailVerified, Is.True);
        }

        // ── Lifetime failures ────────────────────────────────────────────────────

        [Test]
        public async Task Expired_Token_Is_Rejected()
        {
            string token = Mint(expires: DateTime.UtcNow.AddHours(-2), notBefore: DateTime.UtcNow.AddHours(-3));
            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.Expired));
        }

        [Test]
        public async Task Not_Yet_Valid_Token_Is_Rejected()
        {
            string token = Mint(notBefore: DateTime.UtcNow.AddHours(1));
            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.NotYetValid));
        }

        // ── Claim failures ───────────────────────────────────────────────────────

        [Test]
        public async Task Wrong_Issuer_Is_Rejected()
        {
            string token = Mint(issuer: "https://evil.example.com/auth/v1");
            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.WrongIssuer));
        }

        [Test]
        public async Task Wrong_Audience_Is_Rejected()
        {
            string token = Mint(audience: "anon");
            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.WrongAudience));
        }

        // ── Tampered payload ─────────────────────────────────────────────────────

        [Test]
        public async Task Tampered_Payload_Is_Rejected()
        {
            string token = Mint();
            string[] parts = token.Split('.');

            // Replace the payload portion with a different subject; the original signature no
            // longer covers this content, so the verifier must reject it.
            string evil = Base64UrlEncode(Encoding.UTF8.GetBytes(
                @"{""sub"":""hacker"",""email"":""hacker@evil.com"",""aud"":""authenticated""," +
                @"""iss"":""https://test.supabase.co/auth/v1"",""exp"":9999999999,""nbf"":0,""iat"":0}"));
            string tampered = $"{parts[0]}.{evil}.{parts[2]}";

            var result = await _verifier.VerifyAsync(tampered);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.SignatureInvalid));
        }

        // ── Algorithm attacks ────────────────────────────────────────────────────

        [Test]
        public async Task Alg_None_Token_Is_Rejected()
        {
            // A "none" algorithm token has no signature — the header, payload, and trailing dot.
            string header  = Base64UrlEncode(Encoding.UTF8.GetBytes(@"{""alg"":""none"",""typ"":""JWT""}"));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
                @"{""sub"":""attacker"",""aud"":""authenticated""," +
                @"""iss"":""https://test.supabase.co/auth/v1"",""exp"":9999999999}"));
            string noneToken = $"{header}.{payload}.";

            var result = await _verifier.VerifyAsync(noneToken);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.AlgorithmRejected));
        }

        [Test]
        public async Task Symmetric_HS256_Token_Is_Rejected()
        {
            // HS256 uses a shared secret: the server holding that key could forge tokens. Any token
            // signed with a symmetric algorithm must be rejected at the algorithm gate.
            byte[] secret = Encoding.UTF8.GetBytes("this-secret-is-at-least-32-bytes-long-for-hs256!!");
            var symmetricKey = new SymmetricSecurityKey(secret);
            var creds = new SigningCredentials(symmetricKey, SecurityAlgorithms.HmacSha256);
            string token = Mint(creds: creds);

            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.AlgorithmRejected));
        }

        // ── Key failures ─────────────────────────────────────────────────────────

        [Test]
        public async Task Unknown_Kid_Token_Is_Rejected()
        {
            // Signed with a key whose kid is not in the JWKS — after one refresh, still unknown.
            using var otherRsa = RSA.Create(2048);
            var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "unknown-key-99" };
            var creds = new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256);
            string token = Mint(creds: creds);

            var result = await _verifier.VerifyAsync(token);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(TokenVerificationFailure.UnknownKid));
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private string Mint(
            string subject = "user-abc",
            string email = "player@example.com",
            string issuer = Issuer,
            string audience = "authenticated",
            DateTime? expires = null,
            DateTime? notBefore = null,
            SigningCredentials? creds = null)
        {
            var sc = creds ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256);
            return _handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("sub", subject),
                    new Claim("email", email),
                    new Claim("email_confirmed_at", "2026-01-15T10:00:00Z"),
                }),
                IssuedAt = DateTime.UtcNow.AddSeconds(-5),
                NotBefore = notBefore ?? DateTime.UtcNow.AddSeconds(-5),
                Expires = expires ?? DateTime.UtcNow.AddHours(1),
                SigningCredentials = sc,
            });
        }

        private static string Base64UrlEncode(byte[] data)
            => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ── Fake JWKS HTTP handler ───────────────────────────────────────────────

        private sealed class FakeJwksHandler : HttpMessageHandler
        {
            private readonly string _url;
            private readonly string _body;

            public FakeJwksHandler(string url, string body)
            {
                _url = url;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri?.ToString() == _url)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
