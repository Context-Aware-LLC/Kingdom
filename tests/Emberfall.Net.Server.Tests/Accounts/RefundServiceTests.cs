using System;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    [TestFixture]
    [Category("Unit")]
    public sealed class RefundServiceTests
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000);

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => At.ToUnixTimeMilliseconds();
        }

        private static Account AuthenticatedAccount(string playerId = "player-1")
            => new Account(playerId, "google", "subject-hash", At);

        private static EntitlementGrant GemGrant(string playerId, long qty, string storeTxnId, int slot = 0)
            => new EntitlementGrant(
                playerId, EconomyItems.Gems, qty, "store_receipt:play",
                "store:" + storeTxnId + ":" + slot, At);

        [Test]
        public async Task Refund_After_Spend_Drives_Wallet_Negative()
        {
            // Player buys 200 gems, spends all 200 on packs, then the store refunds the purchase.
            // The clawback must land even though the gems are already gone, driving the balance to -200.
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var economy = new EconomyService(ledger, new FixedTime());
            var service = new RefundService(ledger, new FixedTime());

            // Buy 200 gems via store receipt
            await ledger.GrantAsync(GemGrant(account.PlayerId, 200, "store-txn-1"));
            // Spend 200 gems on 2 packs (100 gems each)
            var pack = new Emberfall.Core.Collection.BoosterPack(Emberfall.Core.Contracts.SetId.EMBERFALL);
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-1");
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-2");

            // Refund the original gem purchase
            var refund = new ValidatedStoreRefund("refund-id-1", "store-txn-1", account.PlayerId);
            RefundOutcome outcome = await service.RefundAsync(account, refund);

            Assert.Multiple(() =>
            {
                Assert.That(outcome.Status, Is.EqualTo(RefundStatus.Refunded));
                Assert.That(outcome.Wallet.Gems, Is.EqualTo(-200),
                    "clawback after full spend drives the balance negative");
            });
        }

        [Test]
        public async Task Later_Purchase_Nets_Against_Negative_Balance()
        {
            // After spending 200 gems and then getting a refund, the wallet sits at -200.
            // Buying a 550-gem bundle must net to +350 — the debt clears without blocking the purchase.
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var economy = new EconomyService(ledger, new FixedTime());
            var service = new RefundService(ledger, new FixedTime());

            // Buy 200 gems, spend them on 2 packs, then get refunded → -200
            await ledger.GrantAsync(GemGrant(account.PlayerId, 200, "store-txn-1"));
            var pack = new Emberfall.Core.Collection.BoosterPack(Emberfall.Core.Contracts.SetId.EMBERFALL);
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-1");
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-2");
            await service.RefundAsync(account, new ValidatedStoreRefund("refund-id-1", "store-txn-1", account.PlayerId));
            Assert.That((await economy.GetWalletAsync(account)).Gems, Is.EqualTo(-200));

            // Player buys a 550-gem bundle — must succeed despite the negative balance
            await ledger.GrantAsync(GemGrant(account.PlayerId, 550, "store-txn-2"));

            WalletBalances wallet = await economy.GetWalletAsync(account);
            Assert.That(wallet.Gems, Is.EqualTo(350),
                "a new purchase nets against the debt: -200 + 550 = 350");
        }

        [Test]
        public async Task Double_Delivered_Refund_Is_A_No_Op()
        {
            // The store may deliver the same refund notification more than once (at-least-once delivery).
            // The second delivery must return Replayed and write nothing to the ledger.
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var economy = new EconomyService(ledger, new FixedTime());
            var service = new RefundService(ledger, new FixedTime());

            // Buy 200 gems, spend them, so the refund will drive gems negative
            await ledger.GrantAsync(GemGrant(account.PlayerId, 200, "store-txn-1"));
            var pack = new Emberfall.Core.Collection.BoosterPack(Emberfall.Core.Contracts.SetId.EMBERFALL);
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-1");
            await economy.PurchasePackAsync(account, pack, EconomyCurrency.Gems, "server-spend-2");

            var refund = new ValidatedStoreRefund("refund-id-1", "store-txn-1", account.PlayerId);

            RefundOutcome first = await service.RefundAsync(account, refund);
            int rowsAfterFirstRefund = (await ledger.HistoryAsync(account.PlayerId)).Count;

            RefundOutcome second = await service.RefundAsync(account, refund);
            int rowsAfterSecondRefund = (await ledger.HistoryAsync(account.PlayerId)).Count;

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(RefundStatus.Refunded));
                Assert.That(second.Status, Is.EqualTo(RefundStatus.Replayed),
                    "a duplicate refund notification is a no-op");
                Assert.That(second.Wallet.Gems, Is.EqualTo(-200),
                    "the wallet is unchanged by the replay");
                Assert.That(rowsAfterSecondRefund, Is.EqualTo(rowsAfterFirstRefund),
                    "the replay appends nothing");
            });
        }

        [Test]
        public async Task Refund_For_Unknown_Purchase_Returns_PurchaseNotFound()
        {
            // The server may never have seen the original purchase (race, or it never completed).
            // This must be a reported outcome, not an exception.
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var service = new RefundService(ledger, new FixedTime());

            var refund = new ValidatedStoreRefund("refund-id-1", "store-txn-unknown", account.PlayerId);
            RefundOutcome outcome = await service.RefundAsync(account, refund);

            Assert.Multiple(() =>
            {
                Assert.That(outcome.Status, Is.EqualTo(RefundStatus.PurchaseNotFound));
                Assert.That(outcome.Wallet.Gems, Is.EqualTo(0));
            });
        }
    }
}
