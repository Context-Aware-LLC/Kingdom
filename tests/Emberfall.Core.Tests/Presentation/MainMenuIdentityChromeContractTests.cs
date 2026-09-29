using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// The main menu keeps navigation and a shared quest/wallet top rail only. Character and deck
    /// context belongs on the roster and collection surfaces, so the former upper-left identity
    /// plate must not return as either dormant markup or dead presentation binding.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuIdentityChromeContractTests
    {
        [Test]
        public void Main_menu_does_not_render_or_bind_character_and_deck_identity()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "mainmenu.uss"));
            string controller = File.ReadAllText(Path.Combine(
                unityRoot, "Scripts", "Presentation", "MainMenuController.cs"));
            XDocument document = XDocument.Parse(uxml);
            XElement screenRoot = document.Root!.Elements().Single(
                element => (string?)element.Attribute("name") == "root");
            string?[] rootChrome = screenRoot.Elements()
                .Select(element => (string?)element.Attribute("name"))
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(rootChrome, Is.EqualTo(new string?[]
                {
                    "scrim",
                    "top-rail",
                    "command",
                    "footer",
                    "leaderboard-modal",
                }), "The root-level chrome inventory should not regain an identity panel under a new name.");
                Assert.That(uxml, Does.Not.Contain("name=\"identity\""),
                    "The upper-left character/deck plate should not be in the main-menu tree.");
                Assert.That(uxml, Does.Not.Contain("identity-showcase"));
                Assert.That(uxml, Does.Not.Contain("identity-name"));
                Assert.That(uxml, Does.Not.Contain("identity-deck"));
                Assert.That(styles, Does.Not.Contain(".mm-identity"),
                    "Removed chrome should not leave an unused style family behind.");
                Assert.That(controller, Does.Not.Contain("BindIdentity"));
                Assert.That(controller, Does.Not.Contain("RefreshIdentity"),
                    "The menu should not load roster/deck data solely for removed chrome.");
            });
        }

        [Test]
        public void Main_menu_wordmark_is_outlined_vector_art()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            string wordmarkPath = Path.Combine(
                unityRoot, "Art", "UI", "MainMenu_Wordmark_KingdomOfBadIdeas.svg");
            string wordmark = File.ReadAllText(wordmarkPath);
            string metadata = File.ReadAllText(wordmarkPath + ".meta");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "UI", "MainMenu", "mainmenu.uss"));

            Assert.Multiple(() =>
            {
                Assert.That(wordmark, Does.Not.Contain("<text"),
                    "The brand must remain outlined art rather than runtime font rendering.");
                Assert.That(metadata, Does.Contain("svgType: 3"),
                    "Unity 6000.5 serializes VectorImage as SVG type 3.");
                Assert.That(metadata, Does.Not.Contain("svgType: 4"),
                    "SVG type 4 selects UISVGImage and fails this UI Toolkit background import.");
                Assert.That(uxml, Does.Contain("class=\"mm-wordmark-art\""));
                Assert.That(uxml, Does.Not.Contain("text=\"KINGDOM\""));
                Assert.That(styles, Does.Contain(
                    "MainMenu_Wordmark_KingdomOfBadIdeas.svg"));
                Assert.That(styles, Does.Contain(
                    "-unity-background-scale-mode: scale-to-fit"));
            });
        }
    }
}
