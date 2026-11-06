using System.IO;
using System.Linq;
using System.Xml.Linq;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuQuestPanelContractTests
    {
        [Test]
        public void Startup_surface_places_translucent_quests_and_icon_wallets_in_one_top_rail()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "mainmenu.uss"));
            string controller = File.ReadAllText(Path.Combine(
                unityRoot, "Scripts", "Presentation", "MainMenuController.cs"));
            XDocument document = XDocument.Parse(uxml);

            XElement rail = document.Descendants()
                .Single(e => (string?)e.Attribute("name") == "top-rail");
            XElement status = rail.Descendants()
                .Single(e => (string?)e.Attribute("name") == "quest-status");
            XElement list = rail.Descendants()
                .Single(e => (string?)e.Attribute("name") == "quest-list");
            XElement wallets = rail.Descendants()
                .Single(e => (string?)e.Attribute("name") == "wallet-strip");

            Assert.Multiple(() =>
            {
                Assert.That(((string?)rail.Attribute("class") ?? "").Split(' '),
                    Does.Contain("emb-edge"));
                Assert.That((string?)status.Attribute("text"), Is.EqualTo("Loading quests…"));
                Assert.That(list.Name.LocalName, Is.EqualTo("VisualElement"));
                Assert.That(wallets.Descendants().Count(e =>
                    ((string?)e.Attribute("class") ?? "").Split(' ').Contains("mm-wallet-icon")),
                    Is.EqualTo(3));
                Assert.That(uxml, Does.Not.Contain("CURRENT QUESTS"));
                Assert.That(uxml, Does.Not.Contain("Rewards are granted automatically"));
                Assert.That(uxml, Does.Not.Contain("quest-panel"));
                Assert.That(styles, Does.Contain(".mm-top-rail"));
                Assert.That(styles, Does.Contain(".mm-quest-list").And.Contain("flex-direction: row;"));
                Assert.That(styles, Does.Contain(".mm-quest-row"));
                Assert.That(styles, Does.Contain("background-color: rgba(12, 16, 22, 0.6);"),
                    "Quest cards should preserve the backdrop instead of rebuilding the old panel.");
                Assert.That(styles, Does.Contain(".mm-quest-progress"));
                Assert.That(styles, Does.Contain("icon_currency_gold.png"));
                Assert.That(styles, Does.Contain("icon_currency_gems.png"));
                Assert.That(styles, Does.Contain("craft-shard-cyan-v1.png"));
                Assert.That(controller, Does.Contain("BindQuestPanel();"));
                Assert.That(controller, Does.Contain("FetchQuestsAsync(accessToken)"));
                Assert.That(controller, Does.Contain("RenderQuests("));
                Assert.That(controller, Does.Contain("snapshot.Wallet.Gold"));
                Assert.That(controller, Does.Contain("snapshot.Wallet.Gems"));
                Assert.That(controller, Does.Contain("snapshot.Wallet.Shards"));
            });
        }
    }
}
