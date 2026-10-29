using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    [TestFixture]
    [Category("Unit")]
    public sealed class GemGrantServiceTests
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000);

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => At.ToUnixTimeMilliseconds();
        }

        private sealed class StubValidator : IStoreReceiptValidator
        {
            private readonly StoreReceiptValidation _result;
            public int Calls { get; private set; }

            public StubValidator(StoreReceiptValidation result) => _result = result;

            public Task<StoreReceiptValidation> ValidateAsync(
                StoreReceipt receipt,
                CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(_result);
            }
        }

        private sealed class EchoProviderValidator : IStoreReceiptValidator
        {
            public Task<StoreReceiptValidation> ValidateAsync(
                StoreReceipt receipt,
                CancellationToken ct = default)
                => Task.FromResult(StoreReceiptValidation.Valid(
                    new ValidatedStorePurchase(
                        receipt.Provider,
                        "store-txn-1",
                        "gems.tier.200",
                        "player-1")));
        }

        private static Account AuthenticatedAccount(string playerId = "player-1")
            => new Account(playerId, "google", "subject-hash", At);

        [Test]
        public void Catalog_Exposes_The_Six_Founder_Locked_Abstract_Gem_Tiers()
        {
            Assert.That(
                GemBundleCatalog.All.Select(bundle =>
                    (bundle.ProductKey, bundle.Gems, bundle.UsdPriceCents)),
                Is.EqualTo(new[]
                {
                    ("gems.tier.200", 200L, 199),
                    ("gems.tier.550", 550L, 499),
                    ("gems.tier.1200", 1200L, 999),
                    ("gems.tier.2600", 2600L, 1999),
                    ("gems.tier.7000", 7000L, 4999),
                    ("gems.tier.15000", 15000L, 9999),
                }));
            Assert.That(
                GemBundleCatalog.All.Select(bundle => bundle.ProductKey),
                Has.None.Contains("emberfall"));
        }

        [Test]
        public async Task A_Rejected_Receipt_Never_Grants_Gems()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Invalid("invalid_signature"));
            var service = new GemGrantService(ledger, validator, new FixedTime());

            GemGrantOutcome result = await service.GrantAsync(
                AuthenticatedAccount(), new StoreReceipt("play", "opaque-signed-payload"));

            Assert.That(result.Status, Is.EqualTo(GemGrantStatus.Rejected));
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
        }

        [Test]
        public async Task A_Validated_Store_Transaction_Grants_Exactly_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Valid(
                new ValidatedStorePurchase("play", "store-txn-1", "gems.tier.550", "player-1")));
            var service = new GemGrantService(ledger, validator, new FixedTime());
            var receipt = new StoreReceipt("play", "opaque-signed-payload");

            GemGrantOutcome first = await service.GrantAsync(AuthenticatedAccount(), receipt);
            GemGrantOutcome replay = await service.GrantAsync(AuthenticatedAccount(), receipt);

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(GemGrantStatus.Granted));
                Assert.That(replay.Status, Is.EqualTo(GemGrantStatus.Replayed));
                Assert.That(replay.Wallet.Gems, Is.EqualTo(550));
            });
            Assert.That(await ledger.HistoryAsync("player-1"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task An_Unknown_Product_Key_Is_Rejected_Before_The_Ledger()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Valid(
                new ValidatedStorePurchase("play", "store-txn-1", "live.sku.not-mapped", "player-1")));
            var service = new GemGrantService(ledger, validator, new FixedTime());

            GemGrantOutcome result = await service.GrantAsync(
                AuthenticatedAccount(), new StoreReceipt("play", "opaque-signed-payload"));

            Assert.That(result.Status, Is.EqualTo(GemGrantStatus.Rejected));
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
        }

        [Test]
        public async Task A_Validated_Receipt_Cannot_Be_Claimed_By_Another_Account()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Valid(
                new ValidatedStorePurchase("play", "store-txn-1", "gems.tier.200", "player-1")));
            var service = new GemGrantService(ledger, validator, new FixedTime());

            GemGrantOutcome result = await service.GrantAsync(
                AuthenticatedAccount("player-2"),
                new StoreReceipt("play", "opaque-signed-payload"));

            Assert.That(result.Status, Is.EqualTo(GemGrantStatus.Rejected));
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
            Assert.That(await ledger.HistoryAsync("player-2"), Is.Empty);
        }

        [Test]
        public async Task A_Store_Transaction_Id_Cannot_Grant_Once_Per_Provider_Alias()
        {
            var ledger = new InMemoryEntitlementLedger();
            var service = new GemGrantService(
                ledger,
                new EchoProviderValidator(),
                new FixedTime());

            await service.GrantAsync(
                AuthenticatedAccount(), new StoreReceipt("play", "play-receipt"));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.GrantAsync(
                    AuthenticatedAccount(), new StoreReceipt("app_store", "apple-receipt")));

            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gems), Is.EqualTo(200));
            Assert.That(await ledger.HistoryAsync("player-1"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_Deferred_Purchase_Grants_Nothing_And_Is_Not_A_Rejection()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Pending(
                new ValidatedStorePurchase("play", "store-txn-1", "gems.tier.550", "player-1")));
            var service = new GemGrantService(ledger, validator, new FixedTime());

            GemGrantOutcome result = await service.GrantAsync(
                AuthenticatedAccount(), new StoreReceipt("play", "opaque-signed-payload"));

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(GemGrantStatus.Pending));
                Assert.That(result.Wallet.Gems, Is.EqualTo(0));
                Assert.That(result.RejectionReason, Is.Empty);
            });
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
        }

        [Test]
        public async Task A_Deferred_Purchase_That_Later_Completes_Grants_Exactly_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            var purchase = new ValidatedStorePurchase("play", "store-txn-1", "gems.tier.550", "player-1");
            var receipt = new StoreReceipt("play", "opaque-signed-payload");
            var deferred = new StubValidator(StoreReceiptValidation.Pending(purchase));
            var completed = new StubValidator(StoreReceiptValidation.Valid(purchase));
            var time = new FixedTime();

            GemGrantOutcome pending = await new GemGrantService(ledger, deferred, time)
                .GrantAsync(AuthenticatedAccount(), receipt);
            var live = new GemGrantService(ledger, completed, time);
            GemGrantOutcome cleared = await live.GrantAsync(AuthenticatedAccount(), receipt);
            GemGrantOutcome callbackAndClient = await live.GrantAsync(AuthenticatedAccount(), receipt);

            Assert.Multiple(() =>
            {
                Assert.That(pending.Status, Is.EqualTo(GemGrantStatus.Pending));
                Assert.That(cleared.Status, Is.EqualTo(GemGrantStatus.Granted));
                Assert.That(callbackAndClient.Status, Is.EqualTo(GemGrantStatus.Replayed));
            });
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gems), Is.EqualTo(550));
            Assert.That(await ledger.HistoryAsync("player-1"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_Deferred_Purchase_Bound_To_Another_Player_Is_Rejected_Not_Pending()
        {
            var ledger = new InMemoryEntitlementLedger();
            var validator = new StubValidator(StoreReceiptValidation.Pending(
                new ValidatedStorePurchase("play", "store-txn-1", "gems.tier.200", "player-1")));
            var service = new GemGrantService(ledger, validator, new FixedTime());

            GemGrantOutcome result = await service.GrantAsync(
                AuthenticatedAccount("player-2"), new StoreReceipt("play", "opaque-signed-payload"));

            Assert.That(result.Status, Is.EqualTo(GemGrantStatus.Rejected));
            Assert.That(await ledger.HistoryAsync("player-2"), Is.Empty);
        }
    }
}
