using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class AegisEffectAssetContractTests
    {
        [Test]
        public void Aegis_is_board_only_and_masks_the_gold_quad_to_a_rounded_shell()
        {
            string binder = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", "CardMaterialBinder.cs"));
            string rimSubgraph = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Shaders", "SGF_RimFresnel.shadersubgraph"));
            string material = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Materials", "M_Aegis.mat"));

            Assert.Multiple(() =>
            {
                Assert.That(binder, Does.Contain(
                        "bool want = _boardStatusEffectsVisible && (auras & AuraSet.Shield) != 0;"),
                    "Aegis is a board-minion treatment; a shielded card in hand must not show its quad.");
                Assert.That(binder, Does.Contain("ApplyAegis(auras);"),
                    "The board-visibility transition must immediately refresh Aegis.");

                Assert.That(rimSubgraph, Does.Contain(
                        "float outerMask = 1.0 - smoothstep(-aa, aa, sdf);"),
                    "The Aegis quad needs a finite rounded silhouette whose alpha reaches zero before its bounds.");
                Assert.That(rimSubgraph, Does.Contain(
                        "float shell = saturate(outerMask - innerMask);"),
                    "The visible gold should be a shell around the card, not a filled rectangular field.");
                Assert.That(rimSubgraph, Does.Not.Contain(
                        "float d = saturate(length(UV - float2(0.5, 0.5)) * 2.0);"),
                    "Saturating radial distance makes the quad's entire outer region—including all four corners—opaque.");
                Assert.That(material, Does.Contain("- _ShimmerAmount: 0"),
                    "An untextured full-quad shimmer adds alpha outside the shell and recreates the gold rectangle.");
            });
        }
    }
}
