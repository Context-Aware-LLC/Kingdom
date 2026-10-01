using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class ShroudEffectAssetContractTests
    {
        private static readonly (string File, string Guid, string Address)[] Pieces =
        {
            ("effect_shroud_smoke_left.png", "59d95ebe303f8b947976c4ec334624f0", "ui/effect_shroud_smoke_left"),
            ("effect_shroud_smoke_right.png", "0806a3c244fb5124498abf38fc803019", "ui/effect_shroud_smoke_right"),
            ("effect_shroud_smoke_lower.png", "0c0614db2a386df4a8a96cda99d2543a", "ui/effect_shroud_smoke_lower")
        };

        [Test]
        public void Shroud_uses_three_transparent_shipping_sprites_with_a_board_only_entrance()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));
            string board = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "BoardController.cs"));
            string binder = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "CardMaterialBinder.cs"));
            string view = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "ShroudSmokeView.cs"));

            foreach (var piece in Pieces)
            {
                string assetPath = Path.Combine(assets, "Art", "UI", "Board", piece.File);
                string metadata = File.ReadAllText(assetPath + ".meta");
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(assetPath), Is.True, $"Missing Shroud smoke piece {piece.File}.");
                    Assert.That(metadata, Does.Contain($"guid: {piece.Guid}"));
                    Assert.That(metadata, Does.Contain("textureType: 8"));
                    Assert.That(metadata, Does.Contain("alphaIsTransparency: 1"));
                    Assert.That(group, Does.Contain($"m_GUID: {piece.Guid}"));
                    Assert.That(group, Does.Contain($"m_Address: {piece.Address}"));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(board, Does.Contain("SetBoardStatusEffectsVisible(zone == Zone.Board)"),
                    "Large Shroud smoke must not decorate cards in hand.");
                Assert.That(board, Does.Contain("ShroudSmokeLeftSpriteKey"));
                Assert.That(board, Does.Contain("ShroudSmokeRightSpriteKey"));
                Assert.That(board, Does.Contain("ShroudSmokeLowerSpriteKey"));
                Assert.That(binder, Does.Contain("AuraSet.Shroud"));
                Assert.That(view, Does.Contain("LeftStartOffset"));
                Assert.That(view, Does.Contain("RightStartOffset"));
                Assert.That(view, Does.Contain("LowerStartOffset"));
                Assert.That(view, Does.Contain("Mathf.SmoothStep"),
                    "The independent pieces should settle into place rather than pop on.");
            });
        }
    }
}
