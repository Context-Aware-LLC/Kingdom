using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// Source-level contract for NOR-210. Unity-side presentation assemblies need the Editor seat to
    /// compile, but the screen shape and navigation seam are still deterministic files that the
    /// headless gate can protect.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionDeckDrawerContractTests
    {
        private static string Asset(params string[] parts)
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            foreach (string part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        [Test]
        public void Main_menu_has_one_collection_entry_that_bypasses_the_roster()
        {
            string uxml = Asset("UI", "MainMenu", "MainMenuScreen.uxml");
            string controller = Asset("Scripts", "Presentation", "MainMenuController.cs");

            Assert.Multiple(() =>
            {
                Assert.That(uxml, Does.Contain("name=\"btn-collection\" text=\"Collection\""));
                Assert.That(uxml, Does.Not.Contain("name=\"btn-deck-builder\""));
                Assert.That(controller, Does.Contain("Wire(\"btn-collection\", OnCollection)"));
                Assert.That(controller, Does.Match(
                    @"(?s)private void OnCollection\(\).*?ReturnScene\s*=\s*\""MainMenu\"".*?Go\(\""Collection\""\)"),
                    "The menu entry must open the persistent Collection shell directly.");
            });
        }

        [Test]
        public void Collection_keeps_the_wall_mounted_and_declares_one_right_drawer()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");

            Assert.Multiple(() =>
            {
                Assert.That(uxml, Does.Contain("name=\"collection-grid\""));
                Assert.That(uxml, Does.Contain("name=\"decks-button\" text=\"Decks\""));
                Assert.That(uxml, Does.Contain("name=\"back-button\" text=\"← Back\""));
                Assert.That(uxml, Does.Contain("name=\"collection-empty\""));
                Assert.That(uxml, Does.Contain("name=\"deck-drawer\" class=\"deck-drawer\""));
                Assert.That(uxml, Does.Contain("name=\"deck-chooser-list\""));
                Assert.That(uxml, Does.Contain("name=\"deck-editor\""));
                Assert.That(uxml, Does.Contain("name=\"manage-decks-button\""));
                Assert.That(uxml, Does.Not.Contain("name=\"deck-panel\""),
                    "The retired inline deck panel must not compete with the right drawer.");
            });
        }

        [Test]
        public void Collection_assets_keep_the_visual_tree_styles_and_wall_binding_valid()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");

            Assert.Multiple(() =>
            {
                Assert.That(() => XDocument.Parse(uxml), Throws.Nothing,
                    "Malformed UXML makes Unity reject the complete collection visual tree.");
                Assert.That(Regex.Matches(uxml, "name=\"collection-grid\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"deck-list\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"curve-strip\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"count-label\"").Count, Is.EqualTo(1));

                Assert.That(styles, Does.Contain(".col-scope {"));
                Assert.That(styles, Does.Contain(".col-button {"));
                Assert.That(styles, Does.Contain(".col-button--decks {"));
                Assert.That(styles, Does.Contain(".class-tab_art-verdant {"));
                Assert.That(styles, Does.Not.Match(@"background-image:\s*url\([^\r\n]+\)(?!\s*;)"),
                    "One incomplete declaration makes Unity discard the entire USS file.");

                Assert.That(controller, Does.Match(
                    @"(?m)^\s*_grid\s*=\s*_root\.Q<VisualElement>\(""collection-grid""\);$"));
                Assert.That(controller, Does.Match(
                    @"(?s)_grid\s*=\s*_root\.Q<VisualElement>\(""collection-grid""\);\s*if \(_grid == null\).*?return;"),
                    "CardWall must never be constructed with a null collection-grid host.");
            });
        }

        [Test]
        public void Drawer_animates_with_translate_without_resizing_the_card_wall()
        {
            string styles = Asset("UI", "Collection", "collection.uss");
            Match closed = Regex.Match(styles, @"(?s)\.deck-drawer\s*\{(?<body>.*?)\}");
            Match open = Regex.Match(styles, @"(?s)\.deck-drawer--open\s*\{(?<body>.*?)\}");

            Assert.Multiple(() =>
            {
                Assert.That(closed.Success, Is.True);
                Assert.That(closed.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(closed.Groups["body"].Value, Does.Contain("right: 0"));
                Assert.That(closed.Groups["body"].Value, Does.Match(@"translate:\s*1[01]0%\s+0%"));
                Assert.That(closed.Groups["body"].Value, Does.Contain("transition-property: translate, opacity"));
                Assert.That(closed.Groups["body"].Value, Does.Not.Contain("transition-property: width"));
                Assert.That(open.Success, Is.True);
                Assert.That(open.Groups["body"].Value, Does.Match(@"translate:\s*0%\s+0%"));
            });
        }

        [Test]
        public void Drawer_state_replaces_screen_modes_and_back_closes_it_first()
        {
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            string roster = Asset("Scripts", "Presentation", "CharacterRosterController.cs");
            string rosterUxml = Asset("UI", "CharacterRoster", "CharacterRosterScreen.uxml");

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain("OpenDeckDrawer"));
                Assert.That(controller, Does.Contain("CloseDeckDrawer"));
                Assert.That(controller, Does.Contain("OpenDeckEditor"));
                Assert.That(controller, Does.Not.Contain("StampMode"));
                Assert.That(controller, Does.Not.Contain("collection--deckbuild"));
                Assert.That(controller, Does.Contain("CharacterRosterController.ReturnScene = \"Collection\""),
                    "The drawer's management doorway must come back to Collection.");
                Assert.That(roster, Does.Contain("Go(_returnScene)"));
                Assert.That(rosterUxml, Does.Not.Contain("text=\"Continue to Deck Builder\""));
                Assert.That(controller, Does.Match(
                    @"(?s)private void HandleBack\(\).*?if \(_drawerOpen\).*?CloseDeckDrawer\(\).*?return;.*?Exit\(\)"),
                    "Back must dismiss and save the drawer before it leaves Collection.");
            });
        }
    }
}
