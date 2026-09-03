using System;
using System.Globalization;
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
            XElement collectionPanel = body.Elements()
                .Single(element => (string?)element.Attribute("name") == "collection-panel");
            XElement toolbar = screenRoot.Elements().Single(element =>
                ((string?)element.Attribute("class"))?.Split(' ')
                    .Contains("col-query-toolbar") == true);

            Assert.Multiple(() =>
            {
                Assert.That(collectionPanel.Elements().Any(element =>
                    element.Name.LocalName == "ListView"
                    && (string?)element.Attribute("name") == "collection-list"), Is.True,
                    "The collection shell should own one virtualized card-row list.");
                Assert.That(collectionPanel.Elements().Any(element =>
                    (string?)element.Attribute("name") == "collection-empty"), Is.True,
                    "The empty state belongs beside the virtualized list, not inside recycled content.");
                Assert.That(uxml, Does.Not.Contain("name=\"collection-grid\""));
                Assert.That(uxml, Does.Not.Contain("name=\"collection-scroll\""));
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
            string rowTemplate = Asset("UI", "Collection", "CardRowTemplate.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            string themeStyles = Asset("UI", "Common", "theme.uss");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            string scene = Asset("Scenes", "Collection.unity");
            XDocument rowDocument = XDocument.Parse(rowTemplate);
            XElement row = rowDocument.Descendants()
                .Single(element => (string?)element.Attribute("name") == "card-row");

            Assert.Multiple(() =>
            {
                Assert.That(() => XDocument.Parse(uxml), Throws.Nothing,
                    "Malformed UXML makes Unity reject the complete collection visual tree.");
                Assert.That(Regex.Matches(uxml, "name=\"collection-list\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"deck-list\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"curve-strip\"").Count, Is.EqualTo(1));
                Assert.That(Regex.Matches(uxml, "name=\"count-label\"").Count, Is.EqualTo(1));
                Assert.That(() => XDocument.Parse(rowTemplate), Throws.Nothing);
                Assert.That(row.Elements().Count(element =>
                    ((string?)element.Attribute("class"))?.Split(' ').Contains("card-tile") == true),
                    Is.EqualTo(5), "The reusable row template must expose exactly five card slots.");
                Assert.That(row.Elements().Select(element => (string?)element.Attribute("name")),
                    Is.EquivalentTo(new[] { "card-0", "card-1", "card-2", "card-3", "card-4" }),
                    "CardWall binds each slot by its zero-based name; the fifth column must be bindable.");

                Assert.That(styles, Does.Contain(".col-scope {"));
                Assert.That(styles, Does.Contain(".col-button {"));
                Assert.That(styles, Does.Contain(".col-button--decks {"));
                // MOVED TO theme.uss ON 2026-09-03 (Sam, 6b65de82 line of work). The class-tab art
                // is shared chrome, not collection chrome, so it belongs in the theme both screens
                // link. What this assertion is really for is that the element
                // CollectionScreen.uxml carries has a rule SOMEWHERE the screen loads — so it
                // follows the rule rather than pinning the file it used to live in.
                Assert.That(themeStyles, Does.Contain(".class-tab_art-verdant {"),
                    "CollectionScreen.uxml still carries a class-tab_art-verdant element; a rule " +
                    "that exists in neither sheet it links renders an untextured tab silently.");
                Assert.That(styles, Does.Not.Match(@"background-image:\s*url\([^\r\n]+\)(?!\s*;)"),
                    "One incomplete declaration makes Unity discard the entire USS file.");

                Assert.That(controller, Does.Match(
                    @"(?m)^\s*_collectionList\s*=\s*_root\.Q<ListView>\(""collection-list""\);$"));
                Assert.That(controller, Does.Not.Contain("collection-count"));
                Assert.That(controller, Does.Match(
                    @"(?s)_collectionList\s*=\s*_root\.Q<ListView>\(""collection-list""\);\s*if \(_collectionList == null\).*?return;"),
                    "CardWall must never be constructed with a null collection-list host.");
                Assert.That(controller, Does.Contain(
                    "new CardWall(_collectionList, cardRowTemplate, _faceBaker)"));
                Assert.That(scene, Does.Contain(
                    "cardRowTemplate: {fileID: 9197481963319205126, guid: e617ac4a78d14df58c6e36a0eab81a2b, type: 3}"),
                    "The scene must assign the row template instead of relying on a runtime path lookup.");
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
                // Hangs from the top-LEFT, the edge its drawer now arrives from. It was centred
                // (left: 50%; translate: -50% 0) while the drawer came down across the full width.
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("left: 0"));
                Assert.That(ribbon.Groups["body"].Value, Does.Contain("height: 56px"));
                Assert.That(toolbar.Success, Is.True);
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("position: absolute"));
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("left: var(--edge-safe)"));
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("right: var(--edge-safe)"));
                // The class rail is IN FLOW above the wall as of 2026-08-31 and this overlay is
                // positioned against the screen ROOT, so a top:0 here paints over the nine tabs and
                // eats their taps. Nothing about the rail's own styling would reveal that.
                Assert.That(toolbar.Groups["body"].Value, Does.Contain("top: var(--touch-min)"),
                    "The toolbar overlay must clear the in-flow class rail, not paint over it.");
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

        /// <summary>
        /// <para>THE SCROLLVIEW IS THE WHOLE POINT OF THIS TEST. Stacked in a ~420dp column the
        /// filter controls come to roughly 668dp against a 411dp panel, so the drawer overflows by
        /// more than half a screen and the bottom chip families are simply unreachable without a
        /// scroll. On a desk-sized Game View the drawer is short enough to look finished, which is
        /// exactly how a deleted ScrollView would survive review.</para>
        /// <para>This test used to assert the OPPOSITE and it was right to: while the drawer slid
        /// down across the full width, the rows were laid out ACROSS it, came to at most 332dp, and
        /// the 79dp remainder was the outside-tap target that closed it. Both the 56px group margins
        /// that centred those rows in the forged brackets' bay, and the brackets themselves, went
        /// with the top drawer on 2026-08-31 — see collection.uss. The outside-tap target is now the
        /// wall to the RIGHT of the drawer, which needs no arithmetic to stay true.</para>
        /// </summary>
        [Test]
        public void Collection_left_query_drawer_must_scroll_because_its_rows_overflow_the_panel()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            XDocument document = XDocument.Parse(uxml);
            XElement queryBar = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "query-bar");

            const float phonePanelHeight = 411.4f;
            const float drawerMinWidth = 420f;
            const float edgeSafe = 52f;
            const float interiorGutter = 24f;
            const float compactControlHeight = 52f;
            const float rowMarginBottom = 8f;
            const float drawerVerticalPadding = 32f;

            // Chips wrap inside the drawer's content width rather than running off a full-width bar.
            float contentWidth = drawerMinWidth - edgeSafe - interiorGutter;
            float wideChipsPerRow = MathF.Floor(contentWidth / (128f + 8f));
            float costChipsPerRow = MathF.Floor(contentWidth / (60f + 8f));
            float rarityRows = MathF.Ceiling(4f / wideChipsPerRow);
            float typeRows = MathF.Ceiling(3f / wideChipsPerRow);
            float costRows = MathF.Ceiling(8f / costChipsPerRow);

            // search + sort + sort-dir + reset now stack, then the scope row, then three chip groups.
            float stackedHeight = drawerVerticalPadding
                + 5f * (compactControlHeight + rowMarginBottom)
                + (rarityRows + typeRows + costRows) * compactControlHeight
                + 3f * rowMarginBottom;

            Assert.Multiple(() =>
            {
                Assert.That(wideChipsPerRow, Is.GreaterThanOrEqualTo(2f),
                    "A chip family that cannot fit two chips per row is a column of chips, not a row.");
                Assert.That(stackedHeight, Is.GreaterThan(phonePanelHeight),
                    "If the rows ever fit the panel, this drawer no longer needs its ScrollView "
                    + "and this test is measuring nothing.");
                Assert.That(queryBar.Elements().Any(element =>
                    (string?)element.Attribute("name") == "query-scroll"), Is.True,
                    "The overflowing rows must live in a ScrollView or the last chip family is unreachable.");
                Assert.That(queryBar.Elements().Any(element =>
                    ((string?)element.Attribute("class"))?.Split(' ')
                        .Contains("emb-scroll-fade") == true), Is.True,
                    "Scrollbars are hidden game-wide, so the fade is the only 'there is more' signal — "
                    + "and it must be the ScrollView's SIBLING or it scrolls away with the content.");
                Assert.That(styles, Does.Match(
                    @"(?s)\.col-query-scroll\s*\{.*?min-height:\s*0;"),
                    "Without min-height:0 the ScrollView floors on its content and pushes the "
                    + "overflow straight back out of the drawer.");
                Assert.That(styles, Does.Match(
                    @"(?s)\.col-query__fade\s*\{.*?-unity-background-image-tint-color:"),
                    "The shared fade ramp is white; untinted it fades the filter list to white.");
                Assert.That(styles, Does.Match(
                    @"(?s)\.collection \.db-chip\s*\{.*?min-height:\s*52px;.*?height:\s*52px;"));
                Assert.That(styles, Does.Match(
                    @"(?s)#query-rarities \.db-chip,\s*#query-types \.db-chip\s*\{\s*width:\s*128px;"));
                Assert.That(styles, Does.Match(
                    @"(?s)#query-costs \.db-chip\s*\{\s*width:\s*60px;"));
                Assert.That(styles, Does.Match(
                    @"(?s)\.chrome-input\s*\{.*?min-height:\s*52px;.*?height:\s*52px;"));
                Assert.That(controller, Does.Contain(
                    "_queryScrim?.RegisterCallback<ClickEvent>(_ => SetQueryDrawerOpen(false));"));
            });
        }

        /// <summary>
        /// Selection is said by CONTRAST, and the strip never changes shape.
        ///
        /// <para>This test asserted the opposite until 2026-08-30: nine `.class-tab-frame`
        /// children and a gold border faded in by `--on`. Sam removed the frame, so the assertion
        /// is inverted rather than deleted — the failure mode worth guarding is a half-removal, a
        /// rule with no element or an element with no rule, which renders as nothing and reads as
        /// working. What survived the inversion is what was never about the frame: nine tabs
        /// always, `--on` never moving geometry, and the muting that now carries selection alone.
        /// </para>
        /// </summary>
        [Test]
        public void Collection_class_tabs_mark_selection_by_muting_the_others_and_never_move()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            XDocument document = XDocument.Parse(uxml);
            XElement[] tabs = document.Descendants()
                .Where(element => ((string?)element.Attribute("class"))?.Split(' ')
                    .Contains("class-tab_art") == true)
                .ToArray();
            Match muted = Regex.Match(styles, @"(?s)\.class-tab_art--muted\s*\{(?<body>.*?)\}");

            Assert.Multiple(() =>
            {
                Assert.That(tabs, Has.Length.EqualTo(9));
                Assert.That(uxml, Does.Not.Contain("class-tab-frame"),
                    "The gold selected frame was removed by ruling; no element may carry it.");
                Assert.That(styles, Does.Not.Match(@"(?s)\.class-tab-frame\s*\{"),
                    "A rule for an element nobody applies renders nothing and reads as working.");
                Assert.That(muted.Success, Is.True);
                Assert.That(muted.Groups["body"].Value, Does.Contain("opacity:"));
                Assert.That(styles, Does.Not.Match(@"(?s)\.class-tab_art--on\s*\{.*?translate:"));
                Assert.That(controller, Does.Contain("bool constrained = _selectedClasses.Count > 0;"));
                Assert.That(controller, Does.Contain(
                    "EnableInClassList(\"class-tab_art--muted\", constrained && available && !selected);"));
            });
        }

        /// <summary>
        /// The card size is authored in USS and C# may only shrink it (Sam, 2026-09-01).
        ///
        /// <para><b>What this replaced, and why that mattered.</b> The previous test here pinned
        /// three fixed tile tiers — 340x477, 480x674, 620x870 — by asserting that a hypothetical
        /// 3500x2200 panel would resolve the largest one to five columns and two rows. It did, and
        /// the test was green for as long as it existed. But no device ever presented that
        /// viewport: selecting even the SMALLEST tier required 970dp of scroll viewport against a
        /// landscape phone's 411dp panel, so the ladder those constants formed had never once been
        /// climbed. A test that computes over invented inputs cannot notice that, which is the
        /// whole lesson — it verified the arithmetic of a feature instead of its reachability.</para>
        ///
        /// <para>So this asserts the wiring rather than any number: that the size is declared in
        /// the stylesheet, that CardWall reads those declarations, and that the tier ladder has not
        /// come back. The numbers themselves are now a design choice living in one file, and
        /// pinning them here would only make changing them a two-file edit.</para>
        /// </summary>
        [Test]
        public void Collection_wall_takes_its_tile_size_from_uss_not_from_a_tier_table()
        {
            string collectionStyles = Asset("UI", "Collection", "collection.uss");
            string wall = Asset("Scripts", "Presentation", "CardWall.cs");

            Assert.Multiple(() =>
            {
                Assert.That(collectionStyles, Does.Match(@"(?s)\.collection-list\s*\{.*?--card-tile-width:\s*\d+;.*?--card-tile-height:\s*\d+;"),
                    "The phone tile size must be declared on .collection-list — that is the authoring surface.");
                Assert.That(collectionStyles, Does.Match(@"(?s)\.collection\.emb-sw-expanded \.collection-list\s*\{.*?--card-tile-height:\s*\d+;"),
                    "A larger screen class must be able to author a larger card.");
                Assert.That(collectionStyles, Does.Not.Match(@"(?s)\.card-tile\s*\{[^}]*?\bwidth:"),
                    "A width on .card-tile is overwritten inline by Layout every pass. Declaring one "
                    + "there is how this file came to carry three different tile widths, none read.");

                Assert.That(wall, Does.Contain("new CustomStyleProperty<float>(\"--card-tile-width\")"));
                Assert.That(wall, Does.Contain("new CustomStyleProperty<float>(\"--card-tile-height\")"));
                Assert.That(wall, Does.Match(@"RegisterCallback<CustomStyleResolvedEvent>"),
                    "Declaring the properties is not reading them.");
                Assert.That(wall, Does.Contain("ResolveTileSize(_authoredTileWidth, _authoredTileHeight, viewportHeight"),
                    "Layout must solve from the AUTHORED size, not from a constant.");

                Assert.That(wall, Does.Not.Match(@"const\s+float\s+(Medium|Desktop)Tile(Width|Height)"),
                    "The fixed tier table was deleted because no viewport could select it.");
                Assert.That(wall, Does.Not.Match(@"\bbool\s+TierFits\b"));

                Assert.That(wall, Does.Contain("private const float MaxGutter = 32f;"));
                Assert.That(wall, Does.Contain("public const int CardsPerRow = 5;"));
                Assert.That(wall, Does.Contain(
                    "_list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;"));
                Assert.That(wall, Does.Contain("_list.makeItem = MakeRow;"));
                Assert.That(wall, Does.Contain("_list.bindItem = BindRow;"));
                Assert.That(wall, Does.Not.Contain("Dictionary<int, VisualElement>"),
                    "A virtualized wall must not treat one persistent element as each card's identity.");
                Assert.That(wall, Does.Contain("_baker.SetFaceSize(Mathf.RoundToInt(tileWidth)"),
                    "A larger tile must be freshly baked at its display resolution, not upscaled.");
            });
        }

        /// <summary>
        /// CardWall's fallback pair must equal the COMPACT declaration in collection.uss.
        ///
        /// <para>They are two copies of one number and nothing made them agree. The fallback is
        /// real — it is what the wall uses before the first <c>CustomStyleResolvedEvent</c>, and
        /// what <c>CardFaceBaker</c> seeds its RenderTexture with — so it cannot simply be deleted
        /// in favour of the stylesheet. What it can be is pinned.</para>
        ///
        /// <para>This is the failure collection.uss's own header records, one copy shorter: that
        /// file carried a 276 in a comment, a 350 on <c>.card-tile</c> and a 340 in C#, three
        /// numbers for one tile, and they diverged unnoticed for weeks precisely BECAUSE nothing
        /// read them. A number that nothing reads still has to be right the day something does —
        /// and the offscreen verifier now reads this one.</para>
        /// </summary>
        [Test]
        public void The_card_wall_fallback_size_matches_the_compact_size_authored_in_uss()
        {
            string collectionStyles = Asset("UI", "Collection", "collection.uss");
            string wall = Asset("Scripts", "Presentation", "CardWall.cs");

            // The bare .collection-list block is the compact declaration; screen-class variants
            // have a .collection.emb-sw-* prefix and do not match this anchor.
            Match list = Regex.Match(collectionStyles, @"(?m)^\.collection-list\s*\{(?<body>[^}]*)\}");
            Assert.That(list.Success, Is.True, "collection.uss must declare a bare .collection-list block.");

            float UssVar(string name)
            {
                Match m = Regex.Match(list.Groups["body"].Value, name + @":\s*(?<v>[0-9.]+)\s*;");
                Assert.That(m.Success, Is.True, $"{name} must be declared on .collection-list.");
                return float.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            }

            float CsConst(string name)
            {
                Match m = Regex.Match(wall, @"const\s+float\s+" + name + @"\s*=\s*(?<v>[0-9.]+)f\s*;");
                Assert.That(m.Success, Is.True, $"CardWall.cs must declare {name}.");
                return float.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            }

            Assert.Multiple(() =>
            {
                Assert.That(CsConst("TileWidth"), Is.EqualTo(UssVar("--card-tile-width")),
                    "CardWall.TileWidth is the pre-style fallback and must be the compact size "
                    + "authored in collection.uss. It is what the wall lays out with until the "
                    + "first CustomStyleResolvedEvent arrives and what CardFaceBaker sizes its "
                    + "first RenderTexture from, so a drift here is a wrong first paint and a "
                    + "wasted bake pass, on the frame a player actually sees.");
                Assert.That(CsConst("TileHeight"), Is.EqualTo(UssVar("--card-tile-height")),
                    "CardWall.TileHeight must be the compact --card-tile-height.");
            });
        }

        /// <summary>
        /// The wall reports when its initially visible page has finished baking, and the Collection
        /// holds a loading plate until it does. Further virtualized rows bake lazily as they bind.
        ///
        /// <para>The TIMEOUT is the part worth a guard. The plate covers the whole screen, so any
        /// path that never delivers the completion signal is a hang with no message — and the
        /// baker has one: a rig it cannot build delivers nothing at all.</para>
        /// </summary>
        [Test]
        public void The_collection_holds_a_loading_plate_until_the_wall_has_baked()
        {
            string wall = Asset("Scripts", "Presentation", "CardWall.cs");
            string controller = Asset("Scripts", "Presentation", "CollectionController.cs");
            string styles = Asset("UI", "Collection", "collection.uss");
            Match firstBakeCompleted = Regex.Match(controller,
                @"(?s)private void OnFirstBakeCompleted\(\)\s*\{(?<body>.*?)\n\s*\}");

            Assert.Multiple(() =>
            {
                Assert.That(wall, Does.Contain("public event Action BakePassCompleted;"));
                Assert.That(wall, Does.Match(@"_bakesOutstanding\s*=\s*initialCardCount;"),
                    "The outstanding count must be seeded before the loop: a cached face delivers "
                    + "synchronously, so counting up from zero completes on the first card.");
                Assert.That(wall, Does.Contain("generation != _bakeGeneration"),
                    "A superseded pass's callbacks still arrive and must not complete the current one.");
                Assert.That(wall, Does.Contain("visibleRows * CardsPerRow"),
                    "Loading must wait for the visible page, not eagerly bake the complete pool.");
                Assert.That(wall, Does.Match(
                    @"binding\.Version\s*!=\s*version\s*\|\|\s*binding\.Card\?\.Id\s*!=\s*cardId"),
                    "A recycled slot must reject an asynchronous face from its previous card.");

                Assert.That(controller, Does.Contain("_wall.BakePassCompleted += OnFirstBakeCompleted;"));
                Assert.That(controller, Does.Contain("ShowLoadingPlate();  // before the wall"),
                    "SetPool can complete a warm bake pass inline, before a later Create could run.");
                Assert.That(controller, Does.Match(@"const\s+float\s+LoadingPlateTimeout"),
                    "A full-screen plate with no timeout is a hang with no message.");
                Assert.That(controller, Does.Contain("_wall.BakePassCompleted -= OnFirstBakeCompleted;"),
                    "The wall outlives the first pass and raises this again on every resize.");
                Assert.That(controller, Does.Contain("_loading.OnFadedOut += RevealScreen;"),
                    "Screen Space Overlay rows paint above the world-space loading plate, so the "
                    + "collection must stay hidden until the plate's fade has actually finished.");
                Assert.That(firstBakeCompleted.Success, Is.True);
                Assert.That(firstBakeCompleted.Groups["body"].Value,
                    Does.Not.Contain("RevealScreen();"),
                    "Completing the first two rows starts the plate fade; revealing here puts those "
                    + "rows on top of the transition while it is still running.");
                Assert.That(styles, Does.Match(
                    @"(?s)#collection-list\s+\.unity-collection-view__item:hover\s*,\s*#collection-list\s+\.unity-list-view__item:hover\s*\{.*?background-color:\s*rgba\(0,\s*0,\s*0,\s*0\);"),
                    "ListView's built-in item hover paints one highlight behind the complete "
                    + "five-card row. The named-list selector must cover both UI Toolkit item "
                    + "class generations and outrank Unity's built-in theme.");
                Assert.That(controller, Does.Match(
                    @"(?s)private void OnEnable\(\)\s*\{.*?_ownedScope\s*=\s*true;.*?LoadOwnership\(\);"),
                    "Every collection entry must begin in Owned scope. A field initializer only "
                    + "covers first construction, not a controller that is disabled and reused.");
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
