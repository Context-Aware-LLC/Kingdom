using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class LoadingWordmarkContractTests
    {
        private const string AssetName = "Loading_Wordmark_KingdomOfBadIdeas.svg";
        private const string AssetGuid = "c0175aaa14ee4e63ae426830d30f0708";
        private const string Address = "ui/Loading_Wordmark_KingdomOfBadIdeas";

        [Test]
        public void Loading_wordmark_is_outlined_vector_art_loaded_as_a_sprite()
        {
            string assetPath = Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "Art", "UI", AssetName);
            string svg = File.ReadAllText(assetPath);
            string meta = File.ReadAllText(assetPath + ".meta");
            string overlay = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "Scripts", "Presentation", "LoadingOverlay.cs"));
            string addressables = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));

            Assert.Multiple(() =>
            {
                Assert.That(svg, Does.Contain("viewBox=\"0 0 1600 330\""),
                    "The SVG viewport must preserve the authored loading-screen slot ratio.");
                Assert.That(svg, Does.Contain("<path"),
                    "The wordmark should contain vector outlines.");
                Assert.That(svg, Does.Not.Contain("<text"),
                    "The brand mark must not depend on a font being present at runtime.");
                Assert.That(meta, Does.Contain("svgType: 0"),
                    "SpriteRenderer requires the VectorSprite importer mode.");
                Assert.That(overlay, Does.Contain("var wordmarkSprite = LoadAsset<Sprite>("));
                Assert.That(overlay, Does.Contain(Address));
                Assert.That(overlay, Does.Not.Contain("AddComponent<TextArcWarp>"),
                    "The approved arc is authored into the SVG and must not be warped twice.");
                Assert.That(overlay, Does.Not.Contain("KINGDOM <size=70%>"),
                    "Loading-screen branding must not regress to live TMP text.");
                Assert.That(addressables, Does.Contain($"m_GUID: {AssetGuid}"));
                Assert.That(addressables, Does.Contain($"m_Address: {Address}"));
            });
        }
    }
}
