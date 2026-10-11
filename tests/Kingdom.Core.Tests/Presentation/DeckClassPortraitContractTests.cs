using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class DeckClassPortraitContractTests
    {
        [Test]
        public void Deck_entry_surfaces_share_the_ordered_pair_portrait()
        {
            string presentation = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation");

            string portrait = File.ReadAllText(Path.Combine(presentation, "DeckClassPortrait.cs"));
            string collection = File.ReadAllText(Path.Combine(presentation, "CollectionController.cs"));
            string roster = File.ReadAllText(Path.Combine(presentation, "CharacterRosterController.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(portrait, Does.Contain("PrimaryShare = 0.63f"),
                    "The primary class must own most of the portrait's area.");
                Assert.That(portrait, Does.Contain("DrawPair(context, width, height)"),
                    "Both class images must be emitted by one mesh pass so the diagonal is shared, not spaced.");
                Assert.That(portrait, Does.Contain("UvX(bottomRightX"),
                    "The diagonal must clip an undistorted rectangle, not shear each portrait into a trapezoid.");
                Assert.That(collection, Does.Contain("DeckClassPortrait.Mount(button"),
                    "Collection's deck drawer must use the shared class portrait.");
                Assert.That(roster, Does.Contain("DeckClassPortrait.Mount(art"),
                    "The account-wide deck grid must use the same class portrait.");
            });
        }

        [Test]
        public void Portrait_backed_deck_controls_are_visually_textless()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string collection = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "CollectionController.cs"));
            string styles = File.ReadAllText(Path.Combine(
                assets, "UI", "Collection", "collection.uss"));

            Assert.Multiple(() =>
            {
                Assert.That(collection, Does.Not.Contain("deck-choice-pairing"),
                    "The class portraits replace the drawer's visible class-pair caption.");
                Assert.That(collection, Does.Not.Contain("text = $\"{capturedDeck.Name}"),
                    "The portrait-backed drawer button must not render deck-name/count text.");
                Assert.That(styles, Does.Match(
                        @"(?s)\.deck-class-portrait--drawer\s*\{[^}]*left:\s*0;[^}]*right:\s*0;"),
                    "With no label beside it, the drawer portrait must fill the button width.");
                Assert.That(styles, Does.Match(
                        @"(?s)\.deck-choice\s*\{[^}]*border-radius:\s*var\(--space-2\);[^}]*overflow:\s*hidden;"),
                    "The button must clip its full-bleed portrait to the same rounded profile.");
                Assert.That(styles, Does.Match(
                        @"(?s)\.deck-choice:hover\s+\.deck-class-portrait--drawer[^}]*opacity:"),
                    "The opaque full-bleed image must not hide the button's hover/focus feedback.");
            });
        }

        [Test]
        public void Prepared_sheet_is_a_shipping_ui_sprite()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string metadata = File.ReadAllText(Path.Combine(
                assets, "Sprites", "hero_portrait_sheet.png.meta"));
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));

            Assert.Multiple(() =>
            {
                Assert.That(metadata, Does.Contain("textureType: 8"),
                    "The prepared sheet must import as a Sprite for IUiSpriteProvider.");
                Assert.That(metadata, Does.Contain("spriteMode: 1"),
                    "The custom mesh consumes one full-sheet Sprite and owns the eight cell UVs.");
                Assert.That(metadata, Does.Contain("enableMipMap: 1"),
                    "The large hero sheet is minified in deck controls and needs mipmaps to avoid aliasing.");
                Assert.That(metadata, Does.Contain("filterMode: 2"),
                    "Trilinear filtering must blend between mip levels as portrait sizes change.");
                Assert.That(group, Does.Contain("m_GUID: 7563e120342457c45b5340dee9a0b6c0"));
                Assert.That(group, Does.Contain("m_Address: ui/hero_portrait_sheet"),
                    "Player builds need the same sprite key as Editor play mode.");
            });
        }
    }
}
