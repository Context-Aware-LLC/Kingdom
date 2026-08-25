using System;
using System.IO;
using System.Linq;
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
            XDocument document = XDocument.Parse(uxml);
            XElement screenRoot = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "root");
            XElement queryBar = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "query-bar");
            XElement toolbar = screenRoot.Elements().First();

            Assert.Multiple(() =>
            {
                Assert.That(uxml, Does.Contain("name=\"collection-grid\""));
                Assert.That(uxml, Does.Contain("name=\"decks-button\" text=\"Decks\""));
                Assert.That(uxml, Does.Contain("name=\"back-button\" text=\"← Back\""));
                Assert.That(uxml, Does.Not.Contain("name=\"collection-header\""));
                Assert.That(uxml, Does.Not.Contain("name=\"collection-count\""));
                Assert.That(uxml, Does.Not.Contain("text=\"Collection\""));
                // Navigation sits in a PERSISTENT toolbar in the header's old space; the query bar
                // became a slide-out panel behind `query-toggle` and is no longer always on screen.
                // Decks and Back cannot live inside it -- a dismissable panel is not a place to put
                // the only way off the screen.
                Assert.That((string?)toolbar.Attribute("class"),
                    Does.Contain("col-query-toolbar"),
                    "The always-visible toolbar should occupy the header's old space.");
                Assert.That(toolbar.Descendants().Any(element =>
                    (string?)element.Attribute("name") == "decks-button"), Is.True,
                    "Decks belongs in the persistent toolbar, not the dismissable query panel.");
                Assert.That(toolbar.Descendants().Any(element =>
                    (string?)element.Attribute("name") == "back-button"), Is.True,
                    "Back belongs in the persistent toolbar, not the dismissable query panel.");
                Assert.That(toolbar.Descendants().Any(element =>
                    (string?)element.Attribute("name") == "query-toggle"), Is.True,
                    "The toolbar is what opens the query panel.");
                Assert.That(queryBar.Descendants().Any(element =>
                    (string?)element.Attribute("name") == "query-close"), Is.True,
                    "A panel that slides out needs its own way back.");
                Assert.That(uxml, Does.Contain("name=\"collection-empty\""));

                // Matched on the class LIST, not the whole attribute. Pinning the exact string
                // `class="deck-drawer"` broke the moment the drawer took the shared chrome-panel
                // styling, which says nothing about whether the drawer still exists.
                Assert.That((string?)document.Descendants().Single(element =>
                    (string?)element.Attribute("name") == "deck-drawer").Attribute("class"),
                    Does.Contain("deck-drawer"));
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
                Assert.That(controller, Does.Not.Contain("collection-count"));
                Assert.That(controller, Does.Match(
                    @"(?s)_grid\s*=\s*_root\.Q<VisualElement>\(""collection-grid""\);\s*if \(_grid == null\).*?return;"),
                    "CardWall must never be constructed with a null collection-grid host.");
            });
        }

        [Test]
        public void Collection_filter_opener_reads_as_a_hanging_ribbon()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            XDocument document = XDocument.Parse(uxml);
            XElement toggle = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "query-toggle");
            Match ribbon = Regex.Match(styles, @"(?s)\.col-button--filters\s*\{(?<body>.*?)\}");

            Assert.Multiple(() =>
            {
                Assert.That(toggle.Elements().Any(element =>
                    (string?)element.Attribute("class") == "col-filter-ribbon__label"), Is.True);
                Assert.That(toggle.Elements().Any(element =>
                    (string?)element.Attribute("class") == "col-filter-ribbon__chevron"), Is.True);
                Assert.That(ribbon.Success, Is.True);
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("left: 50%"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("height: 72px"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("translate: -50% 0"));
            });
        }

        [Test]
        public void Collection_wall_targets_two_art_first_rows_across_supported_aspects()
        {
            string sharedStyles = Asset("UI", "Deckbuilder", "deckbuilder.uss");
            string collectionStyles = Asset("UI", "Collection", "collection.uss");
            string wall = Asset("Scripts", "Presentation", "CardWall.cs");

            const float tileWidth = 310f;
            const float tileHeight = 435f;
            const float minGutter = 16f;
            const float rowSafety = 2f;

            // ScaleWithScreenSize matches height, so these are panel units for a 16:9 reference
            // panel and a 20:9 phone/ultrawide panel respectively. The deductions are the authored
            // shell: root sides, class strip, outer panel, recessed scroll, then CardWall's tie guard.
            float referenceAvailable = 1920f - 48f - 130f - 32f - 40f - rowSafety;
            float wideAvailable = 2400f - 48f - 130f - 32f - 40f - rowSafety;
            int referenceColumns = Math.Max(1,
                (int)Math.Floor((referenceAvailable + minGutter) / (tileWidth + minGutter)));
            int wideColumns = Math.Max(1,
                (int)Math.Floor((wideAvailable + minGutter) / (tileWidth + minGutter)));

            // Root bottom + 60px toolbar + toolbar-to-wall margin + both nested panel insets.
            float wallViewportHeight = 1080f - 24f - 60f - 16f - 32f - 40f;
            int completeRows = (int)Math.Floor(wallViewportHeight / (tileHeight + 16f));

            Assert.Multiple(() =>
            {
                Assert.That(sharedStyles, Does.Match(@"(?s)\.card-tile\s*\{.*?width:\s*310px;.*?height:\s*435px;"));
                Assert.That(collectionStyles, Does.Match(
                    @"(?s)\.collection \.col-scroll\.chrome-panel--recessed\s*\{\s*padding:\s*20px;\s*\}"));
                Assert.That(wall, Does.Contain("public const float TileWidth = 310f;"));
                Assert.That(wall, Does.Contain("public const float TileHeight = 435f;"));
                Assert.That(referenceColumns, Is.EqualTo(5), "16:9 should show about ten cards, not a dense wall.");
                Assert.That(wideColumns, Is.EqualTo(6), "20:9 may spend its extra width on one more card.");
                Assert.That(completeRows, Is.EqualTo(2), "The larger face must still leave two complete rows visible.");
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
