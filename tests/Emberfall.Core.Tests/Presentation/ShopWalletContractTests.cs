using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class ShopWalletContractTests
    {
        [Test]
        public void Shop_wallet_has_separate_large_icons_and_reads_the_authoritative_collection_wallet()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            string shop = Path.Combine(assets, "UI", "Shop");
            XDocument document = XDocument.Load(Path.Combine(shop, "ShopScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(shop, "shop.uss"));
            string screen = File.ReadAllText(Path.Combine(assets, "Scripts", "Presentation", "ShopScreenController.cs"));
            string panel = File.ReadAllText(Path.Combine(assets, "Scripts", "Presentation", "ShopPanelController.cs"));

            XElement gold = document.Descendants().Single(e => (string?)e.Attribute("name") == "shop-gold-balance");
            XElement gems = document.Descendants().Single(e => (string?)e.Attribute("name") == "shop-gem-balance");

            Assert.Multiple(() =>
            {
                Assert.That((string?)gold.Parent?.Attribute("tooltip"), Is.EqualTo("Gold"));
                Assert.That((string?)gems.Parent?.Attribute("tooltip"), Is.EqualTo("Gems"));
                Assert.That(document.Descendants().Count(e => ((string?)e.Attribute("class") ?? "")
                    .Split(' ').Contains("shop-wallet__icon")), Is.EqualTo(2));
                Assert.That(styles, Does.Contain("width: 72px;").And.Contain("height: 72px;"));
                Assert.That(styles, Does.Contain("font-size: 48px;"),
                    "The old 22px wallet type must be more than doubled.");
                Assert.That(styles, Does.Contain("/Assets/Art/UI/Icons/icon_currency_gold.png"));
                Assert.That(styles, Does.Contain("/Assets/Art/UI/Icons/icon_currency_gems.png"));
                Assert.That(File.Exists(Path.Combine(assets, "Art", "UI", "Icons", "icon_currency_gold.png")), Is.True);
                Assert.That(File.Exists(Path.Combine(assets, "Art", "UI", "Icons", "icon_currency_gems.png")), Is.True);
                Assert.That(screen, Does.Contain("CollectionGateway.FetchAsync(accessToken)"));
                Assert.That(screen, Does.Contain("AuthGateway.TokenForConnectAsync()"));
                Assert.That(panel, Does.Contain("using Emberfall.Net.Client.Collection;"),
                    "CollectionSnapshot is declared in the client collection namespace, not Core.Collection.");
                Assert.That(panel, Does.Contain("snapshot.HasAuthoritativeWallet"));
                Assert.That(panel, Does.Contain("WalletDisplay.IconAmount(snapshot.Wallet.Gold)"));
                Assert.That(panel, Does.Contain("WalletDisplay.IconAmount(snapshot.Wallet.Gems)"));
            });
        }
    }
}
