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
        public void Wallets_Start_At_Zero_And_Travel_With_The_Top_Rail_Safe_Area()
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
            XElement rail = document.Descendants().Single(e => (string?)e.Attribute("name") == "top-rail");
            string?[] resourceCounters = document.Descendants()
                .Where(e => new[] { "gold-balance", "gem-balance", "shard-balance" }
                    .Contains((string?)e.Attribute("name")))
                .Select(e => (string?)e.Attribute("text"))
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(resourceCounters, Is.EqualTo(new string?[] { "0", "0", "0" }),
                    "Every resource counter should start with the same visual zero.");
                Assert.That((string?)wallet.Attribute("name"), Is.EqualTo("shard-wallet"));
                Assert.That(((string?)rail.Attribute("class") ?? "").Split(' '), Does.Contain("emb-edge"));
                Assert.That((string?)wallet.Attribute("tooltip"), Is.EqualTo("Shards"));
                Assert.That(wallet.Descendants().Count(e =>
                    ((string?)e.Attribute("class") ?? "").Split(' ').Contains("mm-wallet-icon--shards")),
                    Is.EqualTo(1));
                Assert.That(styles, Does.Contain("/Assets/Art/UI/Crafting/Currency/craft-shard-cyan-v1.png"));
                Assert.That(File.Exists(Path.Combine(unityRoot, "Art", "UI", "Crafting", "Currency", "craft-shard-cyan-v1.png")), Is.True);
                Assert.That(menuController, Does.Contain("_topRail.style.right = Length.Percent("));
                Assert.That(menuController, Does.Contain("_topRail.style.top = Length.Percent("));
                Assert.That(menuController, Does.Not.Contain("_shardBalance.style.right"));
                Assert.That(styles, Does.Contain(".mm-wallet-strip"));

                // The gutter is a TOKEN, not a percentage. `top: 2%` / `right: 2%` were both
                // percentages of the panel WIDTH, so the readout moved with aspect ratio and
                // landed somewhere different on every device; --space-6 is a fixed 24dp that
                // means the same thing everywhere. See the comment on the rule itself.
                Assert.That(styles, Does.Contain("top: var(--space-6);"));
                Assert.That(styles, Does.Contain("right: var(--space-6);"));
                Assert.That(styles, Does.Contain(".mm-wallet-chip"));

                // The gutter above is the DESIGN offset. Hardware edge clearance is a separate
                // mechanism and reaches the readout only through .emb-edge, which composes with
                // it as a margin -- see .emb-edge in theme.uss. Losing the class puts the
                // readout under a display cutout with nothing else to catch it.
                Assert.That(uxml, Does.Contain("name=\"top-rail\"").And.Contain("emb-edge"),
                    "The shared rail must opt in to edge clearance for quests and every wallet.");
                Assert.That(menuController, Does.Contain("Screen.safeArea"));
                Assert.That(menuController, Does.Contain("Length.Percent"),
                    "Runtime safe-area insets must be normalized into percentage layout values.");
                Assert.That(menuController, Does.Contain("string fallback = WalletDisplay.IconAmount(0);"));
                Assert.That(menuController, Does.Contain("CollectionGateway.SyncSnapshotAsync(accessToken)"),
                    "The menu must reuse the authenticated collection read that carries the wallet.");
                Assert.That(menuController, Does.Contain("collection.HasAuthoritativePacks"),
                    "Starter-pack routing must not depend on whether the separate wallet projection parsed.");
                Assert.That(menuController, Does.Not.Contain("ShopRuntime.Gateway"),
                    "An unbound shop seam must not leave every top-rail balance on its fallback.");
                Assert.That(menuController, Does.Contain("snapshot.Wallet.Shards"),
                    "The readout must consume the authoritative wallet projection.");
                Assert.That(menuController, Does.Contain("snapshot.Wallet.Gold"));
                Assert.That(menuController, Does.Contain("snapshot.Wallet.Gems"));
            });
        }
    }
}
