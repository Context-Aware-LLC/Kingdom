using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class FrostEffectAssetContractTests
    {
        private static readonly (string File, string Guid, string Address)[] Pieces =
        {
            ("effect_frost_crystals_left.png", "b1388c0dd4c443a2865d24223c3798ca", "ui/effect_frost_crystals_left"),
            ("effect_frost_crystals_right.png", "e3d0ab896b5040a1b08f4650fb842413", "ui/effect_frost_crystals_right"),
            ("effect_frost_crystals_lower.png", "f6a417c05c2e4e6bad388519b296ea0a", "ui/effect_frost_crystals_lower")
        };

        [Test]
        public void Frost_combines_the_surface_shader_with_three_animated_crystal_pieces()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));
            string board = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "BoardController.cs"));
            string binder = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "CardMaterialBinder.cs"));
            string view = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "FrostCrystalView.cs"));

            foreach (var piece in Pieces)
            {
                string assetPath = Path.Combine(assets, "Art", "UI", "Board", piece.File);
                string metadata = File.ReadAllText(assetPath + ".meta");
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(assetPath), Is.True, $"Missing Frost piece {piece.File}.");
                    Assert.That(metadata, Does.Contain($"guid: {piece.Guid}"));
                    Assert.That(metadata, Does.Contain("textureType: 8"));
                    Assert.That(metadata, Does.Contain("alphaIsTransparency: 1"));
                    Assert.That(group, Does.Contain($"m_GUID: {piece.Guid}"));
                    Assert.That(group, Does.Contain($"m_Address: {piece.Address}"));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(assets, "Shaders", "SG_Frost.shadergraph")), Is.True);
                Assert.That(board, Does.Contain("MaterialCatalog.Frost"),
                    "The approved full-card Frost shader must remain the surface treatment.");
                Assert.That(board, Does.Contain("FrostCrystalsLeftSpriteKey"));
                Assert.That(board, Does.Contain("FrostCrystalsRightSpriteKey"));
                Assert.That(board, Does.Contain("FrostCrystalsLowerSpriteKey"));
                Assert.That(board, Does.Contain("SetBoardStatusEffectsVisible(zone == Zone.Board)"));
                Assert.That(binder, Does.Contain("AuraSet.Frosted"));
                Assert.That(view, Does.Contain("EntranceDuration = 0.34f"));
                Assert.That(view, Does.Contain("crystallineOvershoot"),
                    "The generated pieces should grow and settle rather than pop on.");
            });
        }
    }
}
