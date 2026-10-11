using Kingdom.Net.Client.Shop;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class WalletDisplayTests
    {
        [Test]
        public void A_Refund_Debt_Reads_As_A_Signed_Gem_Total()
        {
            Assert.Multiple(() =>
            {
                Assert.That(WalletDisplay.Gems(-200), Is.EqualTo("-200 Gems"));
                Assert.That(WalletDisplay.Gems(-1200), Is.EqualTo("-1,200 Gems"));
                Assert.That(WalletDisplay.Gems(long.MinValue), Does.StartWith("-"));
            });
        }

        [Test]
        public void A_Settled_Wallet_Keeps_The_Existing_Label()
        {
            Assert.Multiple(() =>
            {
                Assert.That(WalletDisplay.Gems(0), Is.EqualTo("Gems  0"));
                Assert.That(WalletDisplay.Gems(550), Is.EqualTo("Gems  550"));
                Assert.That(WalletDisplay.Gems(15000), Is.EqualTo("Gems  15,000"));
            });
        }

        [Test]
        public void A_Negative_Gem_Balance_Is_Representable_On_The_Client()
        {
            var wallet = new WalletBalances(300, -200, 0);

            Assert.That(wallet.Gems, Is.EqualTo(-200));
        }

        [Test]
        public void Shards_Distinguish_Unavailable_From_An_Authoritative_Zero()
        {
            Assert.Multiple(() =>
            {
                Assert.That(WalletDisplay.ShardsUnavailable, Is.EqualTo("—"));
                Assert.That(WalletDisplay.Shards(0), Is.EqualTo("0"));
                Assert.That(WalletDisplay.Shards(12500), Is.EqualTo("12,500"));
                Assert.That(WalletDisplay.Shards(-1200), Is.EqualTo("-1,200"));
            });
        }

        [Test]
        public void Icon_backed_wallets_render_amounts_without_repeating_currency_names()
        {
            Assert.Multiple(() =>
            {
                Assert.That(WalletDisplay.IconAmount(0), Is.EqualTo("0"));
                Assert.That(WalletDisplay.IconAmount(12500), Is.EqualTo("12,500"));
                Assert.That(WalletDisplay.IconAmount(-1200), Is.EqualTo("-1,200"));
                Assert.That(WalletDisplay.IconAmount(long.MinValue), Does.StartWith("-"));
            });
        }
    }
}
