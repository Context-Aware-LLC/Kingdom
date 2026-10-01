using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class BulwarkEffectAssetContractTests
    {
        private const string AssetGuid = "27eac72de70d434e9ff3affaa786cec7";
        private const string Address = "ui/effect_bulwark_shields";

        [Test]
        public void Bulwark_shields_are_a_shipping_sprite_wired_to_the_board_aura()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string assetPath = Path.Combine(
                assets, "Art", "UI", "Board", "effect_bulwark_shields.png");
            string metadata = File.ReadAllText(assetPath + ".meta");
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));
            string board = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "BoardController.cs"));
            string binder = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "CardMaterialBinder.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(assetPath), Is.True,
                    "The selected shields-only concept needs a promoted runtime sprite.");
                Assert.That(metadata, Does.Contain($"guid: {AssetGuid}"));
                Assert.That(metadata, Does.Contain("textureType: 8"),
                    "IUiSpriteProvider loads this asset as a Sprite.");
                Assert.That(metadata, Does.Contain("alphaIsTransparency: 1"),
                    "The shields must composite around the minion without an opaque rectangle.");
                Assert.That(group, Does.Contain($"m_GUID: {AssetGuid}"));
                Assert.That(group, Does.Contain($"m_Address: {Address}"),
                    "Player builds need the same sprite key as Editor play mode.");
                Assert.That(board, Does.Contain("SetBoardStatusEffectsVisible(zone == Zone.Board)"),
                    "A printed Bulwark card in hand must not show the board-status shields.");
                Assert.That(binder, Does.Contain("AuraSet.Bulwark"),
                    "The renderer must follow live Bulwark aura changes.");
            });
        }
    }
}
