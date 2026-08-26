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
            XElement queryScrim = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "query-scrim");
            XElement body = screenRoot.Elements()
                .Single(element => (string?)element.Attribute("name") == "body");
            XElement toolbar = screenRoot.Elements().Single(element =>
                ((string?)element.Attribute("class"))?.Split(' ')
                    .Contains("col-query-toolbar") == true);

            Assert.Multiple(() =>
            {
                Assert.That(uxml, Does.Contain("name=\"collection-grid\""));
                Assert.That(uxml, Does.Contain("name=\"decks-button\" text=\"Decks\""));
                Assert.That(uxml, Does.Contain("name=\"back-button\" text=\"← Back\""));
                Assert.That(uxml, Does.Not.Contain("name=\"collection-header\""));
                Assert.That(uxml, Does.Not.Contain("name=\"collection-count\""));
                Assert.That(uxml, Does.Not.Contain("text=\"Collection\""));
                // Navigation sits in a PERSISTENT overlay; the query bar became a slide-out panel
                // behind `query-toggle` and is no longer always on screen.
                // Decks and Back cannot live inside it -- a dismissable panel is not a place to put
                // the only way off the screen.
                Assert.That((string?)toolbar.Attribute("class"),
                    Does.Contain("col-query-toolbar"),
                    "The always-visible controls should share one overlay layer.");
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
                    (string?)element.Attribute("name") == "query-close"), Is.False,
                    "The persistent ribbon and Back action already dismiss the query panel.");
                Assert.That(queryBar.Descendants().Any(element =>
                    (string?)element.Attribute("class") == "col-query-drawer-title"), Is.False,
                    "The visible controls make a redundant filter-and-sort heading unnecessary.");
                Assert.That((string?)queryScrim.Attribute("picking-mode"), Is.EqualTo("Ignore"),
                    "The outside-click scrim must not block the wall while the drawer is closed.");
                Assert.That(screenRoot.Elements().ToList().IndexOf(toolbar),
                    Is.GreaterThan(screenRoot.Elements().ToList().IndexOf(body)),
                    "UI Toolkit paints in document order, so overlay chrome must follow the wall.");
                Assert.That(screenRoot.Elements().ToList().IndexOf(queryScrim),
                    Is.LessThan(screenRoot.Elements().ToList().IndexOf(queryBar)),
                    "UI Toolkit paints in document order, so the drawer must remain above its scrim.");
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
                Assert.That(controller, Does.Contain(
                    "_queryScrim?.RegisterCallback<ClickEvent>(_ => SetQueryDrawerOpen(false));"));
                Assert.That(controller, Does.Contain(
                    "_queryScrim?.EnableInClassList(\"col-query-scrim--open\", open);"));
            });
        }

        [Test]
        public void Collection_chrome_overlays_the_wall_and_steps_with_screen_class()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            XDocument document = XDocument.Parse(uxml);
            XElement toggle = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "query-toggle");
            XElement actions = document.Descendants()
                .Single(element => ((string?)element.Attribute("class"))?.Split(' ')
                    .Contains("col-query-actions") == true);
            Match ribbon = Regex.Match(styles, @"(?s)\.col-button--filters\s*\{(?<body>.*?)\}");
            Match toolbar = Regex.Match(styles, @"(?s)\.col-query-toolbar\s*\{(?<body>.*?)\}");

            Assert.Multiple(() =>
            {
                Assert.That(toggle.Elements().Any(element =>
                    (string?)element.Attribute("class") == "col-filter-ribbon__label"), Is.True);
                Assert.That(toggle.Elements().Any(element =>
                    (string?)element.Attribute("class") == "col-filter-ribbon__chevron"), Is.True);
                Assert.That((string?)actions.Attribute("picking-mode"), Is.EqualTo("Ignore"),
                    "The transparent full-width actions wrapper must not intercept the ribbon click.");
                Assert.That(ribbon.Success, Is.True);
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("left: 50%"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("height: 56px"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("translate: -50% 0"));
                Assert.That(toolbar.Success, Is.True);
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("left: var(--edge-safe)"));
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("right: var(--edge-safe)"));
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection\.emb-sw-medium \.col-button--filters\s*\{.*?height:\s*64px;"));
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection\.emb-sw-expanded \.col-button--filters\s*\{.*?height:\s*72px;"));
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection\s*\{.*?padding:\s*0 var\(--space-6\) 0;"),
                    "Compact must not reserve the old 24dp bottom gutter.");
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection\.emb-sw-medium\s*\{.*?padding-bottom:\s*var\(--space-4\);"));
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection\.emb-sw-expanded\s*\{.*?padding-bottom:\s*var\(--space-6\);"));
            });
        }

        [Test]
        public void Collection_class_tabs_mark_selection_with_an_inset_frame_without_moving()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            XDocument document = XDocument.Parse(uxml);
            XElement[] tabs = document.Descendants()
                .Where(element => ((string?)element.Attribute("class"))?.Split(' ')
                    .Contains("class-tab_art") == true)
                .ToArray();
            Match selected = Regex.Match(styles,
                @"(?s)\.class-tab_art--on\s*>\s*\.class-tab-frame\s*\{(?<body>.*?)\}");
            Match frame = Regex.Match(styles, @"(?s)\.class-tab-frame\s*\{(?<body>.*?)\}");

            Assert.Multiple(() =>
            {
                Assert.That(tabs, Has.Length.EqualTo(9));
                Assert.That(tabs.All(tab => tab.Elements().Count(element =>
                    ((string?)element.Attribute("class"))?.Split(' ')
                        .Contains("class-tab-frame") == true
                    && (string?)element.Attribute("picking-mode") == "Ignore") == 1), Is.True);
                Assert.That(frame.Success, Is.True);
                Assert.That(frame.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(frame.Groups["body"].Value, Does.Contain("left: 3px"));
                Assert.That(frame.Groups["body"].Value, Does.Contain("right: 3px"));
                Assert.That(selected.Groups["body"].Value, Does.Contain("opacity: 1"));
                Assert.That(styles, Does.Not.Match(@"(?s)\.class-tab_art--on\s*\{.*?translate:"));
                Assert.That(controller, Does.Contain("bool constrained = _selectedClasses.Count > 0;"));
                Assert.That(controller, Does.Contain(
                    "EnableInClassList(\"class-tab_art--muted\", constrained && available && !selected);"));
            });
        }

        [Test]
        public void Collection_wall_uses_art_first_responsive_tiers_instead_of_screen_pixel_density()
        {
            string collectionStyles = Asset("UI", "Collection", "collection.uss");
            string wall = Asset("Scripts", "Presentation", "CardWall.cs");

            const float desktopWidth = 620f;
            const float desktopHeight = 870f;
            const float minGutter = 16f;

            // Sam's desktop observation was roughly 10 x 4.5 at the old compact size. The same
            // physical-size panel therefore exposes about 3,500 x 2,200 panel units. The desktop
            // tier must convert that real viewport into an art wall, irrespective of monitor DPI.
            const float observedDesktopWidth = 3500f;
            const float observedDesktopHeight = 2200f;
            int desktopColumns = Math.Min(5, Math.Max(1,
                (int)Math.Floor((observedDesktopWidth + minGutter) / (desktopWidth + minGutter))));
            int desktopRows = (int)Math.Floor(observedDesktopHeight / (desktopHeight + 8f));

            Assert.Multiple(() =>
            {
                Assert.That(collectionStyles, Does.Match(@"(?s)\.card-tile\s*\{.*?width:\s*340px;.*?height:\s*477px;"));
                Assert.That(collectionStyles, Does.Match(
                    @"(?s)\.collection \.col-scroll\.chrome-panel--recessed\s*\{\s*padding:\s*0 12px;\s*\}"));
                Assert.That(collectionStyles, Does.Match(
                    @"(?s)\.collection\.emb-sw-expanded \.col-scroll\.chrome-panel--recessed\s*\{\s*padding:\s*4px 20px;\s*\}"));
                Assert.That(wall, Does.Contain("public const float TileWidth = 340f;"));
                Assert.That(wall, Does.Contain("public const float TileHeight = 477f;"));
                Assert.That(wall, Does.Contain("public const float MediumTileWidth = 480f;"));
                Assert.That(wall, Does.Contain("public const float DesktopTileWidth = 620f;"));
                Assert.That(wall, Does.Contain("public const float DesktopTileHeight = 870f;"));
                Assert.That(wall, Does.Contain("private const float MaxGutter = 32f;"));
                Assert.That(wall, Does.Contain("private const int MaxColumns = 5;"));
                Assert.That(wall, Does.Contain("ResolveTileTier(available, viewportHeight"));
                Assert.That(wall, Does.Contain("_baker.SetFaceSize(Mathf.RoundToInt(tileWidth)"),
                    "A larger tile must be freshly baked at its display resolution, not upscaled.");
                Assert.That(desktopColumns, Is.EqualTo(5));
                Assert.That(desktopRows, Is.EqualTo(2));
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
