using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class StatusEffectScaleContractTests
    {
        [Test]
        public void Board_status_sprites_preserve_their_authored_world_size_when_assigned()
        {
            string board = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", "BoardController.cs"));

            // SpriteRenderer.sprite resets .size to the sprite's native PPU extent. These generated
            // textures are 1024-1672 px wide, so direct assignment turns a card-sized treatment
            // into a roughly 10-17 world-unit sprite that fills most of the board. The shared helper
            // restores the exact world size established by MakeSprite after assigning the texture.
            string[] requiredAssignments =
            {
                "SpriteMaterials.SetPreservingSize(frostCrystalLeft, _uiSprites?.Get(FrostCrystalsLeftSpriteKey));",
                "SpriteMaterials.SetPreservingSize(frostCrystalRight, _uiSprites?.Get(FrostCrystalsRightSpriteKey));",
                "SpriteMaterials.SetPreservingSize(frostCrystalLower, _uiSprites?.Get(FrostCrystalsLowerSpriteKey));",
                "SpriteMaterials.SetPreservingSize(bulwarkRenderer, _uiSprites?.Get(BulwarkEffectSpriteKey));",
                "SpriteMaterials.SetPreservingSize(shroudLeft, _uiSprites?.Get(ShroudSmokeLeftSpriteKey));",
                "SpriteMaterials.SetPreservingSize(shroudRight, _uiSprites?.Get(ShroudSmokeRightSpriteKey));",
                "SpriteMaterials.SetPreservingSize(shroudLower, _uiSprites?.Get(ShroudSmokeLowerSpriteKey));"
            };

            Assert.Multiple(() =>
            {
                Assert.That(board, Does.Contain(
                        "float aegisHeight = CardH * 1.30f;"),
                    "Aegis is a card-relative quad and must not become a board- or screen-sized shell.");
                Assert.That(board, Does.Contain(
                        "new Vector3(aegisHeight * aegisAspect, aegisHeight, 1f)"),
                    "The dome must preserve its authored oval rather than stretching to the card aspect.");

                foreach (string assignment in requiredAssignments)
                {
                    Assert.That(board, Does.Contain(assignment),
                        "Every large generated status sprite must preserve its card-relative world size when assigned.");
                }
            });
        }
    }
}
