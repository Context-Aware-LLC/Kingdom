using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>/store/play/notifications</c>: Play's Real-time developer notifications over Pub/Sub. The
    /// status code is Pub/Sub's ack, so each test reads the code as well as the ledger.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PlayNotificationEndpointTests
    {
        private const string Package = "com.contextaware.kobi";
        private const string Bearer = "pubsub-oidc-token";

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_790_000_000_000;
        }

        private sealed class StubVerifier : IPushTokenVerifier
        {
            public PushTokenVerdict Verdict { get; set; } = PushTokenVerdict.Accepted;
            public Task<PushTokenVerdict> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(rawToken == Bearer ? Verdict : PushTokenVerdict.Rejected);
        }

        /// <summary>Play's answer for a token, as <see cref="PlayStoreReceiptValidator"/> would give it.</summary>
        private sealed class StubPlay : IStoreReceiptValidator, IStorePurchaseAcknowledger
        {
            public StoreReceiptValidation Answer { get; set; } = StoreReceiptValidation.Invalid("unknown_token");
            public int Acknowledged { get; private set; }

            public Task<StoreReceiptValidation> ValidateAsync(StoreReceipt receipt, CancellationToken ct = default)
                => Task.FromResult(Answer);

            public Task AcknowledgeAsync(StoreReceipt receipt, CancellationToken ct = default)
            {
                Acknowledged++;
                return Task.CompletedTask;
            }
        }

        private sealed class DownLedger : IEntitlementLedger
        {
            private readonly InMemoryEntitlementLedger _inner = new InMemoryEntitlementLedger();
            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default) => _inner.GrantAsync(grant, ct);
            public Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default) => _inner.ApplyBatchAsync(batch, ct);
            public Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(string playerId, CancellationToken ct = default)
                => _inner.AcquirePlayerMutationLockAsync(playerId, ct);
            public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default) => _inner.BalanceAsync(playerId, item, ct);
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default)
                => _inner.BalancesAsync(playerId, ct);
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default)
                => _inner.HistoryAsync(playerId, ct);
            public Task<EntitlementGrant?> FindAsync(string externalTxnId, CancellationToken ct = default)
                => throw new TimeoutException("ledger is down");
        }

        private InMemoryEntitlementLedger _ledger = null!;
        private InMemoryAccountStore _accounts = null!;
        private StubVerifier _verifier = null!;
        private StubPlay _play = null!;
        private Account _account = null!;

        [SetUp]
        public async Task SetUp()
        {
            _ledger = new InMemoryEntitlementLedger();
            _accounts = new InMemoryAccountStore(new FixedTime());
            _verifier = new StubVerifier();
            _play = new StubPlay();
            _account = await _accounts.ResolveOrCreateAsync("supabase", "subject-hash");
        }

        private PlayNotificationEndpoint Endpoint(IEntitlementLedger? ledger = null)
            => new PlayNotificationEndpoint(_verifier, Package, ledger ?? _ledger, _accounts, _play, _play, new FixedTime());

        /// <summary>A gem grant exactly as <see cref="GemGrantService"/> writes one.</summary>
        private Task Bought(string orderId, long gems)
            => _ledger.ApplyBatchAsync(new EntitlementBatch(
                _account.PlayerId, "store:" + orderId,
                new[] { new EntitlementDelta(EconomyItems.Gems, gems, "store_receipt:play") },
                requirement: null, DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000)));

        private static ReadOnlyMemory<byte> Push(object notification)
        {
            string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notification)));
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                message = new { data, messageId = "1", publishTime = "2026-10-29T00:00:00Z" },
                subscription = "projects/kingdom-of-bad-ideas/subscriptions/play-rtdn-push",
            }));
        }

        private static ReadOnlyMemory<byte> Voided(string orderId, int productType = 2, string package = Package)
            => Push(new
            {
                version = "1.0",
                packageName = package,
                eventTimeMillis = "1790000000000",
                voidedPurchaseNotification = new { purchaseToken = "tok", orderId, productType, refundType = 1 },
            });

        private static ReadOnlyMemory<byte> Purchased(string sku = "gems.tier.200", int notificationType = 1)
            => Push(new
            {
                version = "1.0",
                packageName = Package,
                eventTimeMillis = "1790000000000",
                oneTimeProductNotification = new { version = "1.0", notificationType, purchaseToken = "tok", sku },
            });

        private Task<long> Gems() => _ledger.BalanceAsync(_account.PlayerId, EconomyItems.Gems);

        // ── Authentication ────────────────────────────────────────────────────────

        [Test]
        public async Task No_Bearer_Is_401_And_Writes_Nothing()
        {
            await Bought("GPA.1", 200);
            PlayNotificationResult r = await Endpoint().HandleAsync(null, Voided("GPA.1"));
            Assert.That(r.HttpStatus, Is.EqualTo(401));
            Assert.That(await Gems(), Is.EqualTo(200));
        }

        [Test]
        public async Task A_Bearer_That_Is_Not_The_Subscriptions_Is_401()
        {
            await Bought("GPA.1", 200);
            PlayNotificationResult r = await Endpoint().HandleAsync("someone-elses-google-token", Voided("GPA.1"));
            Assert.That(r.HttpStatus, Is.EqualTo(401));
            Assert.That(await Gems(), Is.EqualTo(200));
        }

        [Test]
        public async Task Google_Keys_Unreachable_Is_503_So_PubSub_Redelivers()
        {
            _verifier.Verdict = PushTokenVerdict.Unavailable;
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Voided("GPA.1"));
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        // ── Refunds ───────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Voided_Order_Claws_Its_Gems_Back_Once()
        {
            await Bought("GPA.1", 200);
            PlayNotificationEndpoint endpoint = Endpoint();

            PlayNotificationResult first = await endpoint.HandleAsync(Bearer, Voided("GPA.1"));
            PlayNotificationResult redelivered = await endpoint.HandleAsync(Bearer, Voided("GPA.1"));
            long gems = await Gems();
            EntitlementGrant? clawback = await _ledger.FindAsync("refund:GPA.1:0");

            Assert.Multiple(() =>
            {
                Assert.That(first.HttpStatus, Is.EqualTo(200));
                Assert.That(first.Status, Is.EqualTo("refunded"));
                Assert.That(redelivered.HttpStatus, Is.EqualTo(200));
                Assert.That(redelivered.Status, Is.EqualTo("replayed"));
                Assert.That(gems, Is.EqualTo(0));
                Assert.That(clawback, Is.Not.Null,
                    "the clawback is keyed on Play's order id, which the reconciliation report reads");
            });
        }

        [Test]
        public async Task A_Refund_After_The_Gems_Are_Spent_Leaves_The_Player_In_Debt()
        {
            await Bought("GPA.1", 200);
            await _ledger.ApplyBatchAsync(new EntitlementBatch(
                _account.PlayerId, "spend-1",
                new[] { new EntitlementDelta(EconomyItems.Gems, -150, "pack_purchase") },
                requirement: null, DateTimeOffset.UnixEpoch));

            await Endpoint().HandleAsync(Bearer, Voided("GPA.1"));

            Assert.That(await Gems(), Is.EqualTo(-150));
        }

        [Test]
        public async Task A_Voided_Order_We_Never_Granted_Is_Acked_With_Nothing_Written()
        {
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Voided("GPA.unredeemed"));
            Assert.That(r.HttpStatus, Is.EqualTo(200));
            Assert.That(r.Status, Is.EqualTo("not_granted"));
            Assert.That(await _ledger.HistoryAsync(_account.PlayerId), Is.Empty);
        }

        [Test]
        public async Task A_Voided_Subscription_Is_Ignored()
        {
            await Bought("GPA.1", 200);
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Voided("GPA.1", productType: 1));
            Assert.That(r.Status, Is.EqualTo("ignored"));
            Assert.That(await Gems(), Is.EqualTo(200));
        }

        [Test]
        public async Task Another_Apps_Notification_Is_Acked_And_Ignored()
        {
            await Bought("GPA.1", 200);
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Voided("GPA.1", package: "com.other.app"));
            Assert.That(r.HttpStatus, Is.EqualTo(200));
            Assert.That(r.Status, Is.EqualTo("wrong_package"));
            Assert.That(await Gems(), Is.EqualTo(200));
        }

        [Test]
        public async Task A_Ledger_Outage_Is_503_So_The_Refund_Is_Not_Lost()
        {
            PlayNotificationResult r = await Endpoint(new DownLedger()).HandleAsync(Bearer, Voided("GPA.1"));
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        // ── Purchases ─────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Completed_Purchase_Is_Granted_And_Acknowledged_Without_The_Client()
        {
            _play.Answer = StoreReceiptValidation.Valid(
                new ValidatedStorePurchase("play", "GPA.7", "gems.tier.200", _account.PlayerId));

            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Purchased());
            long gems = await Gems();

            Assert.Multiple(() =>
            {
                Assert.That(r.Status, Is.EqualTo("granted"));
                Assert.That(gems, Is.EqualTo(200));
                Assert.That(_play.Acknowledged, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task A_Purchase_Still_Pending_Writes_Nothing()
        {
            _play.Answer = StoreReceiptValidation.Pending(
                new ValidatedStorePurchase("play", "pending-x", "gems.tier.200", _account.PlayerId));

            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Purchased());

            Assert.That(r.Status, Is.EqualTo("not_paid"));
            Assert.That(await Gems(), Is.EqualTo(0));
            Assert.That(_play.Acknowledged, Is.EqualTo(0));
        }

        [Test]
        public async Task A_Canceled_Pending_Purchase_Is_Ignored()
        {
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer, Purchased(notificationType: 2));
            Assert.That(r.Status, Is.EqualTo("ignored"));
        }

        // ── The voided-list sweep ─────────────────────────────────────────────────

        private sealed class VoidedList : IPlayOrders
        {
            public List<PlayVoidedPurchase> Rows { get; } = new List<PlayVoidedPurchase>();
            public Task<string?> GetOrderStateAsync(string orderId, CancellationToken ct = default)
                => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedAsync(long sinceMs, CancellationToken ct = default)
                => Task.FromResult((IReadOnlyList<PlayVoidedPurchase>)Rows);
        }

        [Test]
        public async Task The_Sweep_Claws_Back_A_Void_Play_Never_Notified_Once()
        {
            await Bought("GPA.1", 200);
            var play = new VoidedList
            {
                Rows =
                {
                    new PlayVoidedPurchase("GPA.1", "tok", 1_790_000_000_000, 0),
                    new PlayVoidedPurchase("GPA.unredeemed", "tok-2", 1_790_000_000_000, 8),
                },
            };
            var sweep = new PlayVoidedSweep(play, new PlayOrderRefunds(_ledger, _accounts, new FixedTime()), new FixedTime());

            int first = await sweep.SweepAsync();
            int second = await sweep.SweepAsync();

            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(0), "the order id makes the next pass a replay");
            Assert.That(await Gems(), Is.EqualTo(0));
        }

        [Test]
        public async Task A_Notification_After_The_Sweep_Is_A_Replay()
        {
            await Bought("GPA.1", 200);
            var play = new VoidedList { Rows = { new PlayVoidedPurchase("GPA.1", "tok", 1_790_000_000_000, 0) } };
            await new PlayVoidedSweep(play, new PlayOrderRefunds(_ledger, _accounts, new FixedTime()), new FixedTime())
                .SweepAsync();

            PlayNotificationResult late = await Endpoint().HandleAsync(Bearer, Voided("GPA.1"));

            Assert.That(late.Status, Is.EqualTo("replayed"));
            Assert.That(await Gems(), Is.EqualTo(0));
        }

        // ── Envelope ──────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Malformed_Or_Oversized_Message_Is_Acked_Not_Retried_Forever()
        {
            PlayNotificationResult malformed = await Endpoint().HandleAsync(Bearer, Encoding.UTF8.GetBytes("{\"message\":{}}"));
            PlayNotificationResult oversized = await Endpoint().HandleAsync(Bearer, null);
            Assert.That(malformed.HttpStatus, Is.EqualTo(200));
            Assert.That(malformed.Status, Is.EqualTo("malformed"));
            Assert.That(oversized.HttpStatus, Is.EqualTo(200));
        }

        [Test]
        public async Task The_Console_Test_Message_Is_Acked()
        {
            PlayNotificationResult r = await Endpoint().HandleAsync(Bearer,
                Push(new { version = "1.0", packageName = Package, eventTimeMillis = "1", testNotification = new { version = "1.0" } }));
            Assert.That(r.HttpStatus, Is.EqualTo(200));
            Assert.That(r.Status, Is.EqualTo("test"));
        }
    }
}
