using System.IO;
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
            string shopController = File.ReadAllText(Path.Combine(
                unityRoot, "Scripts", "Presentation", "ShopPanelController.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(uxml, Does.Contain("name=\"shard-balance\" text=\"Shards  —\""),
                    "The pre-snapshot state must say unavailable rather than fabricate zero.");
                Assert.That(styles, Does.Contain(".mm-shard-balance"));
                Assert.That(styles, Does.Contain("top: 2%;"));
                Assert.That(styles, Does.Contain("right: 2%;"));
                Assert.That(styles, Does.Contain("min-width: 12%;"),
                    "The readout footprint must scale with the canvas.");
                Assert.That(menuController, Does.Contain("Screen.safeArea"));
                Assert.That(menuController, Does.Contain("Length.Percent"),
                    "Runtime safe-area insets must be normalized into percentage layout values.");
                Assert.That(menuController, Does.Contain("WalletDisplay.ShardsUnavailable"));
                Assert.That(shopController, Does.Contain("snapshot.Wallet.Shards"),
                    "The readout must consume the authoritative wallet projection.");
            });
        }
    }
}
