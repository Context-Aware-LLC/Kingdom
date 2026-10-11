using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Tests.Support;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
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

        // ── 4. The onboarding grant fires at the door ────────────────────────────

        /// <summary>
        /// An authenticated connect grants the account its onboarding collection. This is the moment
        /// the whole ownership chain becomes real: before it, <c>OnboardingGrant</c> had no callers
        /// anywhere and no account had ever been given a card.
        /// </summary>
        [Test]
        [CancelAfter(60_000)]
        public async Task A_Verified_Account_Is_Onboarded_On_Connect()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, Email, EmailVerified: true)));
            var store = new InMemoryAccountStore(new FakeTime());
            var ledger = new InMemoryEntitlementLedger();

            await WithServerAsync(verifier, store, async (wsUri, gate) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, DeviceToken, authToken: "valid.token.here");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, DeviceToken, authToken: "valid.token.here");

                await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                var account = await store.ResolveOrCreateAsync("supabase", HashCredential(Subject));

                var history = await ledger.HistoryAsync(account.PlayerId);
                Assert.That(history, Is.Not.Empty,
                    "an authenticated connect must grant the account its onboarding collection");
                Assert.That(history.All(row => row.Source == OnboardingGrantService.GrantSource), Is.True,
                    "every row from this path is a signup grant");

                // Both sockets are the SAME account and both ran the grant. It must have landed once.
                Assert.That(await ledger.BalanceAsync(account.PlayerId, OnboardingGrant.StarterPack().Id),
                    Is.EqualTo(OnboardingGrant.StarterPackCount),
                    "two connects from one account must not grant two collections");
            }, ledger);
        }

        /// <summary>
        /// A guest writes nothing. <c>EconomyAccountPolicy.RequireDurable</c> refuses inventory
        /// movement before account linking, so the grant skips them rather than throwing — and a
        /// throw here would have closed a socket that has every right to be open.
        /// </summary>
        [Test]
        [CancelAfter(60_000)]
        public async Task A_Guest_Connect_Writes_Nothing_To_The_Ledger()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.Malformed));
            var store = new InMemoryAccountStore(new FakeTime());
            var ledger = new InMemoryEntitlementLedger();

            await WithServerAsync(verifier, store, async (wsUri, gate) =>
            {
                const string tokenA = "device-token-onboard-guest-a";
                const string tokenB = "device-token-onboard-guest-b";

                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, tokenA);   // no authToken
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, tokenB);   // no authToken

                await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                foreach (string token in new[] { tokenA, tokenB })
                    Assert.That(await ledger.HistoryAsync(AccountId.FromDeviceToken(token).Value), Is.Empty,
                        "a guest seat never reaches the account store, so nothing can be granted to it");
            }, ledger);
        }

        // ── 5. L6 is no longer inert ─────────────────────────────────────────────

        /// <summary>
        /// The end-to-end assertion this whole change exists for: with a ledger configured, an
        /// authenticated account is REFUSED a deck containing a card it does not own.
        ///
        /// <para><b>Stated as a differential, because the absolute is not the interesting part.</b>
        /// The identical deck, the identical account and the identical socket sequence are accepted
        /// with no ledger and refused with one — so what this pins is precisely the branch that
        /// stopped returning null, and it cannot pass for some unrelated reason like an off-class
        /// card or a bad count.</para>
        ///
        /// <para>The unowned card is a RARE. The onboarding grant's commons half gives one copy of
        /// every collectible common, and the starter packs are granted as an UNOPENED balance, so a
        /// freshly-onboarded account provably owns no rare at all.</para>
        /// </summary>
        [Test]
        [CancelAfter(60_000)]
        public async Task An_Unowned_Card_Is_Refused_Only_When_A_Ledger_Is_Configured()
        {
            List<DeckCardLine> lines = DeckWithOneUnownedRare();

            Assert.That(await SubmitIsRefusedAsync(lines, ledger: null), Is.False,
                "with no ledger, ownership is unknown and the deck must be seated — today's behaviour");

            Assert.That(await SubmitIsRefusedAsync(lines, ledger: new InMemoryEntitlementLedger()), Is.True,
                "with a ledger, the account owns no rare and the deck must be refused at the handshake");
        }

        /// <summary>
        /// A legal 30 for the pairing with exactly one line swapped for a same-class RARE, keeping the
        /// deck legal on L1–L5 so a refusal is attributable to L6 and nothing else.
        /// </summary>
        private static List<DeckCardLine> DeckWithOneUnownedRare()
        {
            var deck = DefaultDeckGenerator.Generate(
                CardClass.GRAVEKEEP, CardClass.VERDANT, SessionHarness.Db,
                new DeterministicRandom(0xD00DUL), "test-deck");
            var lines = deck.Entries.Select(e => new DeckCardLine(e.CardId, e.Count)).ToList();

            var rare = SessionHarness.Db.AllCollectible()
                .Where(d => d.Rarity == Rarity.RARE && d.Class == CardClass.GRAVEKEEP)
                .OrderBy(d => d.Id)
                .First();

            // Replace, preserving the count so the deck stays exactly thirty cards.
            int slot = lines.FindIndex(l => l.CardId != rare.Id);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0));
            lines[slot] = new DeckCardLine(rare.Id, lines[slot].Count);
            return lines;
        }

        /// <summary>
        /// Drive one authenticated <c>?deck=1</c> socket with this decklist and report whether the
        /// server closed it. Returns false if the socket was seated instead.
        /// </summary>
        private static async Task<bool> SubmitIsRefusedAsync(List<DeckCardLine> lines, IEntitlementLedger? ledger)
        {
            var verifier = new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, Email, EmailVerified: true)));
            var store = new InMemoryAccountStore(new FakeTime());
            bool refused = false;

            await WithServerAsync(verifier, store, async (wsUri, gate) =>
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUri + "?deck=1"), CancellationToken.None);
                await SendHelloAsync(ws, DeviceToken, authToken: "valid.token.here");
                await ws.SendAsync(
                    ProtocolCodec.Encode(
                        new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines),
                        ProtocolCodec.PreMatch, 1),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                refused = await WaitForCloseAsync(ws, TimeSpan.FromSeconds(10));
            }, ledger);

            return refused;
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
            Func<Uri, Kingdom.Net.Server.Transport.MatchGate, Task> body,
            IEntitlementLedger? ledger = null)
        {
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-auth-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                TokenVerifier = verifier,
                AccountStore = store,
                EntitlementLedger = ledger,
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
                new HelloMsg(deviceToken, ProtocolContract.Version, "test", "auth-gate-tests",
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
