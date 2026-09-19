using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuQuestPanelContractTests
    {
        [Test]
        public void Startup_surface_has_a_safe_area_quest_panel_backed_by_the_live_endpoint()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "mainmenu.uss"));
            string controller = File.ReadAllText(Path.Combine(
                unityRoot, "Scripts", "Presentation", "MainMenuController.cs"));
            XDocument document = XDocument.Parse(uxml);

            XElement panel = document.Descendants()
                .Single(e => (string?)e.Attribute("name") == "quest-panel");
            XElement status = panel.Descendants()
                .Single(e => (string?)e.Attribute("name") == "quest-status");
            XElement list = panel.Descendants()
                .Single(e => (string?)e.Attribute("name") == "quest-list");

            Assert.Multiple(() =>
            {
                Assert.That(((string?)panel.Attribute("class") ?? "").Split(' '),
                    Does.Contain("emb-edge"));
                Assert.That((string?)status.Attribute("text"), Is.EqualTo("Loading quests…"));
                Assert.That(list.Name.LocalName, Is.EqualTo("ScrollView"));
                Assert.That(styles, Does.Contain(".mm-quests"));
                Assert.That(styles, Does.Contain(".mm-quest-row"));
                Assert.That(styles, Does.Contain(".mm-quest-progress"));
                Assert.That(controller, Does.Contain("BindQuestPanel();"));
                Assert.That(controller, Does.Contain("FetchQuestsAsync(accessToken)"));
                Assert.That(controller, Does.Contain("RenderQuests("));
            });
        }
    }
}
