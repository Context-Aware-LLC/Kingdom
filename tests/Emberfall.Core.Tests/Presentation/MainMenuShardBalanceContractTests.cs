using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuShardBalanceContractTests
    {
        [Test]
        public void Readout_Starts_Unavailable_And_Follows_The_Top_Right_Safe_Area()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "mainmenu.uss"));
            string menuController = File.ReadAllText(Path.Combine(
                unityRoot, "Scripts", "Presentation", "MainMenuController.cs"));
            XDocument document = XDocument.Parse(uxml);
            XElement amount = document.Descendants().Single(e => (string?)e.Attribute("name") == "shard-balance");
            XElement wallet = amount.Parent!;

            Assert.Multiple(() =>
            {
                Assert.That((string?)amount.Attribute("text"), Is.EqualTo("—"),
                    "The pre-snapshot state must say unavailable rather than fabricate zero.");
                Assert.That((string?)wallet.Attribute("name"), Is.EqualTo("shard-wallet"));
                Assert.That(((string?)wallet.Attribute("class") ?? "").Split(' '), Does.Contain("emb-edge"));
                Assert.That((string?)wallet.Attribute("tooltip"), Is.EqualTo("Shards"));
                Assert.That(wallet.Elements().Count(e => (string?)e.Attribute("class") == "mm-shard-icon"), Is.EqualTo(1));
                Assert.That(styles, Does.Contain("/Assets/Art/UI/Crafting/Currency/craft-shard-cyan-v1.png"));
                Assert.That(File.Exists(Path.Combine(unityRoot, "Art", "UI", "Crafting", "Currency", "craft-shard-cyan-v1.png")), Is.True);
                Assert.That(menuController, Does.Contain("_shardWallet.style.right = Length.Percent("));
                Assert.That(menuController, Does.Contain("_shardWallet.style.top = Length.Percent("));
                Assert.That(menuController, Does.Not.Contain("_shardBalance.style.right"));
                Assert.That(styles, Does.Contain(".mm-shard-balance"));

                // The gutter is a TOKEN, not a percentage. `top: 2%` / `right: 2%` were both
                // percentages of the panel WIDTH, so the readout moved with aspect ratio and
                // landed somewhere different on every device; --space-6 is a fixed 24dp that
                // means the same thing everywhere. See the comment on the rule itself.
                Assert.That(styles, Does.Contain("top: var(--space-6);"));
                Assert.That(styles, Does.Contain("right: var(--space-6);"));
                Assert.That(styles, Does.Contain("min-width: 12%;"),
                    "The readout footprint must scale with the canvas.");

                // The gutter above is the DESIGN offset. Hardware edge clearance is a separate
                // mechanism and reaches the readout only through .emb-edge, which composes with
                // it as a margin -- see .emb-edge in theme.uss. Losing the class puts the
                // readout under a display cutout with nothing else to catch it.
                Assert.That(uxml, Does.Contain("name=\"shard-balance\"").And.Contain("emb-edge"),
                    "The readout must opt in to the shared edge clearance.");
                Assert.That(menuController, Does.Contain("Screen.safeArea"));
                Assert.That(menuController, Does.Contain("Length.Percent"),
                    "Runtime safe-area insets must be normalized into percentage layout values.");
                Assert.That(menuController, Does.Contain("WalletDisplay.ShardsUnavailable"));
                // The SHARD readout is the main menu's. ShopPanelController used to carry it and
                // now renders only gold and gems -- asserting against that file measured a
                // currency this test is not about.
                Assert.That(menuController, Does.Contain("Wallet.Shards"),
                    "The readout must consume the authoritative wallet projection.");
            });
        }
    }
}
