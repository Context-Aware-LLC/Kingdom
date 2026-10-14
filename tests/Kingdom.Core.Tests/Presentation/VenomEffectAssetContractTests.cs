using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class VenomEffectAssetContractTests
    {
        private static readonly (string File, string Guid, string Address)[] Frames =
        {
            ("effect_venom_bubbles_1.png", "cf2333b0c67b45d899551186059c26c6", "ui/effect_venom_bubbles_1"),
            ("effect_venom_bubbles_2.png", "3d176bcbca0747acae1276e1bb7cd93e", "ui/effect_venom_bubbles_2"),
            ("effect_venom_bubbles_3.png", "ef2e6d9a233f40109a825ff58f7d2bcf", "ui/effect_venom_bubbles_3")
        };

        [Test]
        public void Venom_bubbles_three_addressable_frames_on_the_attack_socket()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));
            string board = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "BoardController.cs"));
            string binder = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "CardMaterialBinder.cs"));

            foreach (var frame in Frames)
            {
                string assetPath = Path.Combine(assets, "Art", "UI", "Board", frame.File);
                string metadata = File.ReadAllText(assetPath + ".meta");
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(assetPath), Is.True, $"Missing Venom frame {frame.File}.");
                    Assert.That(metadata, Does.Contain($"guid: {frame.Guid}"));
                    Assert.That(metadata, Does.Contain("textureType: 8"));
                    Assert.That(metadata, Does.Contain("alphaIsTransparency: 1"));
                    // A frame missing from the group still draws in the Editor and is blank in a build.
                    Assert.That(group, Does.Contain($"m_GUID: {frame.Guid}"));
                    Assert.That(group, Does.Contain($"m_Address: {frame.Address}"));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(board, Does.Contain("venomRoot.transform.SetParent(attackSocket.transform, false);"),
                    "The ring rides the attack socket so it follows the socket's placement and visibility.");
                Assert.That(board, Does.Contain(
                    "SpriteMaterials.SetPreservingSize(venomFirst, _uiSprites?.Get(VenomBubblesFirstSpriteKey));"));
                Assert.That(board, Does.Contain(
                    "SpriteMaterials.SetPreservingSize(venomSecond, _uiSprites?.Get(VenomBubblesSecondSpriteKey));"));
                Assert.That(board, Does.Contain(
                    "SpriteMaterials.SetPreservingSize(venomThird, _uiSprites?.Get(VenomBubblesThirdSpriteKey));"));
                Assert.That(binder, Does.Contain("_boardStatusEffectsVisible && (auras & AuraSet.Venom) != 0"));
            });
        }
    }
}
