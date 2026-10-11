using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    [TestFixture]
    public sealed class ShopEndpointTests
    {
        private const string Subject = "shop-buyer";
        private const string Token = "valid-token";
        private const string PackId = "pack.pendingham";
        private const string RequestId = "a904f47b-39dc-4802-9453-c076e2eb1774";

        private sealed class Clock : ITimeSource { public long NowMs => 1_700_000_000_000; }
        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string token, CancellationToken ct = default)
                => Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity(Subject, "buyer@example.com", EmailVerified: true)));
        }
        private sealed class EmptyCards : ICardDatabase
        {
            public bool TryGet(int id, out CardDefinition def) { def = null!; return false; }
            public CardDefinition Get(int id) => throw new KeyNotFoundException();
            public IReadOnlyCollection<CardDefinition> AllCollectible() => Array.Empty<CardDefinition>();
        }

        private static BoosterPackGrantService Opener(IEntitlementLedger ledger)
            => new BoosterPackGrantService(ledger, new InMemoryPackPityStore(),
                new FixedPackSeedSource(1), new EmptyCards(), new Clock());

        [Test]
        public async Task Buying_grants_an_unopened_pack_once_and_never_opens_it()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            Account buyer = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of(Subject), "buyer@example.com");
            await ledger.GrantAsync(new EntitlementGrant(buyer.PlayerId, EconomyItems.Gold, 500,
                "test", "fund-shop-buyer", DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
            await ledger.GrantAsync(new EntitlementGrant(buyer.PlayerId,
                OnboardingGrant.StarterPack().Id, 2, "test", "existing-vault-packs",
                DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
            var economy = new EconomyService(ledger, new Clock());
            var opener = Opener(ledger);

            ShopEndpointResult catalog = await ShopEndpoint.ReadAsync(Token, new Verifier(),
                accounts, ledger, economy, opener, null);

            ShopEndpointResult first = await ShopEndpoint.PurchaseAsync(Token, PackId, "gold", RequestId,
                new Verifier(), accounts, ledger, economy, opener, null);
            ShopEndpointResult replay = await ShopEndpoint.PurchaseAsync(Token, PackId, "gold", RequestId,
                new Verifier(), accounts, ledger, economy, opener, null);
            ShopEndpointResult changedCurrency = await ShopEndpoint.PurchaseAsync(Token, PackId,
                "gems", RequestId, new Verifier(), accounts, ledger, economy, opener, null);

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(ShopEndpointStatus.Purchased));
                Assert.That(replay.Status, Is.EqualTo(ShopEndpointStatus.Replayed));
                Assert.That(changedCurrency.Status, Is.EqualTo(ShopEndpointStatus.Conflict),
                    "one intent cannot be rebound to a different currency");
                Assert.That(first.Wallet.Gold, Is.EqualTo(300));
                Assert.That(replay.UnopenedCount, Is.EqualTo(1));
                Assert.That(catalog.VaultCount, Is.EqualTo(2));
                Assert.That(first.VaultCount, Is.EqualTo(3));
                Assert.That(replay.VaultCount, Is.EqualTo(3));
            });
            Assert.That(await ledger.BalanceAsync(buyer.PlayerId, PackId), Is.EqualTo(1));
            Assert.That((await ledger.HistoryAsync(buyer.PlayerId)).Any(row =>
                row.Item.StartsWith(BoosterPackGrantService.CardItemPrefix, StringComparison.Ordinal)),
                Is.False, "purchase must not grant cards or open the pack");
        }

        [Test]
        public async Task Starter_and_unopenable_packs_cannot_be_sold()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            var economy = new EconomyService(ledger, new Clock());

            ShopEndpointResult starter = await ShopEndpoint.PurchaseAsync(Token,
                OnboardingGrant.StarterPack().Id, "gold", RequestId,
                new Verifier(), accounts, ledger, economy, Opener(ledger), null);
            ShopEndpointResult noOpener = await ShopEndpoint.PurchaseAsync(Token,
                PackId, "gold", RequestId, new Verifier(), accounts, ledger, economy, null, null);

            Assert.That(starter.Status, Is.EqualTo(ShopEndpointStatus.InvalidRequest));
            Assert.That(noOpener.Status, Is.EqualTo(ShopEndpointStatus.Unavailable));
        }

        [TestCase(0)]
        [TestCase(-5)]
        [TestCase(EconomyService.MaxPackQuantity + 1)]
        public async Task A_quantity_outside_the_bound_is_an_invalid_request(long quantity)
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            ShopEndpointResult result = await ShopEndpoint.PurchaseAsync(Token, PackId, "gold",
                RequestId, new Verifier(), accounts, ledger, new EconomyService(ledger, new Clock()),
                Opener(ledger), null, default, quantity);

            Assert.That(result.Status, Is.EqualTo(ShopEndpointStatus.InvalidRequest));
        }

        [Test]
        public async Task Insufficient_funds_does_not_create_a_pack()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            Account buyer = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of(Subject), "buyer@example.com");
            ShopEndpointResult result = await ShopEndpoint.PurchaseAsync(Token, PackId, "gems",
                RequestId, new Verifier(), accounts, ledger, new EconomyService(ledger, new Clock()),
                Opener(ledger), null);

            Assert.That(result.Status, Is.EqualTo(ShopEndpointStatus.InsufficientFunds));
            Assert.That(await ledger.BalanceAsync(buyer.PlayerId, PackId), Is.Zero);
        }
    }
}
