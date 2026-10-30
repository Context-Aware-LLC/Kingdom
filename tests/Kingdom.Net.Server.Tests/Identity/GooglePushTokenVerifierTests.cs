using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Identity
{
    /// <summary>
    /// The bearer Pub/Sub's push subscription signs. Tokens are self-minted with a local RSA key served
    /// as Google's certs; no network.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class GooglePushTokenVerifierTests
    {
        private const string Audience = "https://emberfall.fly.dev/store/play/notifications";
        private const string PushAccount = "play-rtdn-push@kingdom-of-bad-ideas.iam.gserviceaccount.com";

        private RSA _rsa = null!;
        private RsaSecurityKey _key = null!;
        private CountingCerts _certs = null!;
        private GooglePushTokenVerifier _verifier = null!;

        [SetUp]
        public void SetUp()
        {
            _rsa = RSA.Create(2048);
            _key = new RsaSecurityKey(_rsa) { KeyId = "google-1" };
            RSAParameters p = _rsa.ExportParameters(includePrivateParameters: false);
            string jwks = $@"{{""keys"":[{{""kty"":""RSA"",""kid"":""google-1"",""use"":""sig"",""alg"":""RS256"",""n"":""{B64(p.Modulus!)}"",""e"":""{B64(p.Exponent!)}""}}]}}";
            _certs = new CountingCerts(jwks);
            _verifier = new GooglePushTokenVerifier(Audience, PushAccount, new HttpClient(_certs));
        }

        [TearDown]
        public void TearDown()
        {
            _certs.Dispose();
            _rsa.Dispose();
        }

        [Test]
        public async Task The_Subscriptions_Own_Token_Is_Accepted()
            => Assert.That(await _verifier.VerifyAsync(Mint()), Is.EqualTo(PushTokenVerdict.Accepted));

        [Test]
        public async Task The_Bare_Issuer_Spelling_Is_Accepted_Too()
            => Assert.That(await _verifier.VerifyAsync(Mint(issuer: "accounts.google.com")), Is.EqualTo(PushTokenVerdict.Accepted));

        [Test]
        public async Task Any_Other_Google_Account_Is_Rejected()
        {
            // Anyone can get Google to sign a token for any audience: the email is the check.
            string token = Mint(email: "attacker@gmail.com");
            Assert.That(await _verifier.VerifyAsync(token), Is.EqualTo(PushTokenVerdict.Rejected));
        }

        [Test]
        public async Task An_Unverified_Email_Is_Rejected()
            => Assert.That(await _verifier.VerifyAsync(Mint(emailVerified: false)), Is.EqualTo(PushTokenVerdict.Rejected));

        [Test]
        public async Task Another_Audience_Is_Rejected()
            => Assert.That(await _verifier.VerifyAsync(Mint(audience: "https://elsewhere.example/hook")),
                Is.EqualTo(PushTokenVerdict.Rejected));

        [Test]
        public async Task Another_Issuer_Is_Rejected()
            => Assert.That(await _verifier.VerifyAsync(Mint(issuer: "https://evil.example")), Is.EqualTo(PushTokenVerdict.Rejected));

        [Test]
        public async Task An_Expired_Token_Is_Rejected()
            => Assert.That(await _verifier.VerifyAsync(Mint(expires: DateTime.UtcNow.AddMinutes(-5))),
                Is.EqualTo(PushTokenVerdict.Rejected));

        [Test]
        public async Task A_Token_Signed_By_Another_Key_Is_Rejected()
        {
            using RSA other = RSA.Create(2048);
            string token = Mint(key: new RsaSecurityKey(other) { KeyId = "google-1" });
            Assert.That(await _verifier.VerifyAsync(token), Is.EqualTo(PushTokenVerdict.Rejected));
        }

        [Test]
        public async Task Garbage_Is_Rejected()
            => Assert.That(await _verifier.VerifyAsync("not-a-jwt"), Is.EqualTo(PushTokenVerdict.Rejected));

        [Test]
        public async Task Certs_Unreachable_Is_Unavailable_Not_Rejected()
        {
            var verifier = new GooglePushTokenVerifier(Audience, PushAccount, new HttpClient(new CountingCerts(null)));
            Assert.That(await verifier.VerifyAsync(Mint()), Is.EqualTo(PushTokenVerdict.Unavailable));
        }

        [Test]
        public async Task A_Stream_Of_Unknown_Kids_Costs_One_Refetch_Not_One_Per_Request()
        {
            await _verifier.VerifyAsync(Mint());
            for (int i = 0; i < 20; i++)
                await _verifier.VerifyAsync(Mint(key: new RsaSecurityKey(_rsa) { KeyId = "made-up-" + i }));
            Assert.That(_certs.Fetches, Is.EqualTo(1),
                "the forced refresh waits out its gap; the cert fetch was just made");
        }

        private string Mint(
            string issuer = "https://accounts.google.com", string audience = Audience, string email = PushAccount,
            bool emailVerified = true, DateTime? expires = null, SecurityKey? key = null)
        {
            DateTime now = DateTime.UtcNow;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = new Dictionary<string, object>
                {
                    ["email"] = email,
                    ["email_verified"] = emailVerified,
                    ["sub"] = "1234567890",
                },
                IssuedAt = (expires ?? now.AddHours(1)).AddHours(-1),
                NotBefore = (expires ?? now.AddHours(1)).AddHours(-1),
                Expires = expires ?? now.AddHours(1),
                SigningCredentials = new SigningCredentials(key ?? _key, SecurityAlgorithms.RsaSha256),
            });
        }

        private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed class CountingCerts : HttpMessageHandler
        {
            private readonly string? _jwks;
            public int Fetches { get; private set; }

            public CountingCerts(string? jwks) => _jwks = jwks;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (_jwks == null) throw new HttpRequestException("unreachable");
                if (request.RequestUri?.ToString() != "https://www.googleapis.com/oauth2/v3/certs")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                Fetches++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_jwks, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
