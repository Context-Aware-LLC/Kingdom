using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Tests.Support;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// EF-074c (NOR-229) — Resolve an authenticated identity at the door.
    ///
    /// Three DONE-WHEN cases:
    ///   1. Valid token → authenticated account (not the device-token hash).
    ///   2. Invalid token → socket closed; no downgrade to guest.
    ///   3. Absent token → today's guest identity, unchanged.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class AuthenticatedGateTests
    {
        private const string DeviceToken = "auth-gate-test-device-token-fixed";
        private const string Subject = "supabase-user-abc-123";
        private const string Email = "player@example.com";

        // ── 1. Valid token → authenticated account ───────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Valid_Auth_Token_Yields_An_Authenticated_Account()
        {
            using var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "gate-test-key" };
            var verifier = new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, Email, EmailVerified: true)));
            var store = new InMemoryAccountStore(new FakeTime());

            await WithServerAsync(verifier, store, async (wsUri, gate) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, DeviceToken, authToken: "valid.token.here");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, DeviceToken, authToken: "valid.token.here");

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                var accounts = gate.AccountsOf(new MatchId(start0.Snapshot.State.Id.Value));
                Assert.That(accounts, Is.Not.Null);

                // The account in the gate must come from the store (keyed on subject hash),
                // NOT from AccountId.FromDeviceToken.
                string subHash = HashCredential(Subject);
                var storeAccount = await store.ResolveOrCreateAsync("supabase", subHash);
                var expectedId = AccountId.FromAuthenticatedAccount(storeAccount.PlayerId);

                Assert.That(accounts!.Value.p0, Is.EqualTo(expectedId));
                Assert.That(accounts.Value.p1, Is.EqualTo(expectedId));

                // The store must have persisted the email.
                Assert.That(storeAccount.Email, Is.EqualTo(Email));

                // Must NOT equal the device-token-derived id.
                Assert.That(accounts.Value.p0, Is.Not.EqualTo(AccountId.FromDeviceToken(DeviceToken)));
            });
        }

        // ── 2. Invalid token → socket closed, no downgrade ──────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Invalid_Auth_Token_Closes_The_Socket()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
            var store = new InMemoryAccountStore(new FakeTime());

            await WithServerAsync(verifier, store, async wsUri =>
            {
                using var bad = new ClientWebSocket();
                await bad.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(bad, DeviceToken, authToken: "bad.token.here");

                bool closed = await WaitForCloseAsync(bad, TimeSpan.FromSeconds(10));
                Assert.That(closed, Is.True, "an invalid auth token must close the socket, not fall back to guest");
            });
        }

        // ── 3. Absent token → guest path unchanged ───────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Absent_Auth_Token_Uses_Guest_Device_Token_Identity()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.Malformed));
            var store = new InMemoryAccountStore(new FakeTime());

            await WithServerAsync(verifier, store, async (wsUri, gate) =>
            {
                const string tokenA = "device-token-guest-a";
                const string tokenB = "device-token-guest-b";

                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, tokenA);   // no authToken
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, tokenB);   // no authToken

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                var accounts = gate.AccountsOf(new MatchId(start0.Snapshot.State.Id.Value));
                Assert.That(accounts, Is.Not.Null);

                // Guest accounts are the device-token hash, not from the verifier or store.
                Assert.That(new[] { accounts!.Value.p0, accounts.Value.p1 }, Is.EquivalentTo(
                    new[] { AccountId.FromDeviceToken(tokenA), AccountId.FromDeviceToken(tokenB) }));
            });
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string HashCredential(string value)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }

        private static Task WithServerAsync(
            IIdentityTokenVerifier verifier, IAccountStore store, Func<Uri, Task> body)
            => WithServerAsync(verifier, store, (uri, _) => body(uri));

        private static async Task WithServerAsync(
            IIdentityTokenVerifier verifier, IAccountStore store,
            Func<Uri, Emberfall.Net.Server.Transport.MatchGate, Task> body)
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-auth-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions { EngineContractVersion = "interfaces.v4" },
                TokenVerifier = verifier,
                AccountStore = store,
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"), built.Gate);
            }
            finally
            {
                await built.App.StopAsync();
                await TryDeleteDirectoryAsync(replays);
            }
        }

        private static Task SendHelloAsync(ClientWebSocket ws, string deviceToken, string authToken = "")
        {
            var frame = ProtocolCodec.Encode(
                new HelloMsg(deviceToken, "interfaces.v4", "test", "auth-gate-tests",
                             DisplayName: "", AuthToken: authToken),
                ProtocolCodec.PreMatch, 0);
            return ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<bool> WaitForCloseAsync(ClientWebSocket ws, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return true;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (WebSocketException) { return true; }
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var res = ProtocolCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (!res.Ok) throw new InvalidOperationException($"Server sent undecodable frame.");
                if (res.Message is T t) return t;
            }
        }

        private static async Task TryDeleteDirectoryAsync(string dir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        // ── Stub verifier ─────────────────────────────────────────────────────────

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult _result;
            public StubVerifier(TokenVerificationResult result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(_result);
        }
    }
}
