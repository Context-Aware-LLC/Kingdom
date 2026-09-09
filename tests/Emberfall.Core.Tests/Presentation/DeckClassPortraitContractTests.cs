using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class DeckClassPortraitContractTests
    {
        [Test]
        public void Deck_entry_surfaces_share_the_ordered_pair_portrait()
        {
            string presentation = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation");

            string portrait = File.ReadAllText(Path.Combine(presentation, "DeckClassPortrait.cs"));
            string collection = File.ReadAllText(Path.Combine(presentation, "CollectionController.cs"));
            string roster = File.ReadAllText(Path.Combine(presentation, "CharacterRosterController.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(portrait, Does.Contain("PrimaryShare = 0.63f"),
                    "The primary class must own most of the portrait's area.");
                Assert.That(portrait, Does.Contain("DrawPair(context, width, height)"),
                    "Both class images must be emitted by one mesh pass so the diagonal is shared, not spaced.");
                Assert.That(collection, Does.Contain("DeckClassPortrait.Mount(button"),
                    "Collection's deck drawer must use the shared class portrait.");
                Assert.That(roster, Does.Contain("DeckClassPortrait.Mount(art"),
                    "The account-wide deck grid must use the same class portrait.");
            });
        }

        [Test]
        public void Prepared_sheet_is_a_shipping_ui_sprite()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
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
                Assert.That(group, Does.Contain("m_GUID: 7563e120342457c45b5340dee9a0b6c0"));
                Assert.That(group, Does.Contain("m_Address: ui/hero_portrait_sheet"),
                    "Player builds need the same sprite key as Editor play mode.");
            });
        }
    }
}
