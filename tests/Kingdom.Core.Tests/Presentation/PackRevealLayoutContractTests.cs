using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class PackRevealLayoutContractTests
    {
        [Test]
        public void Batch_reveal_uses_real_card_faces_and_rarity_receipt_plates()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string presentation = Path.Combine(assets, "Scripts", "Presentation");
            string reveal = File.ReadAllText(Path.Combine(presentation, "PackRevealPanel.cs"));
            string vault = File.ReadAllText(Path.Combine(presentation, "PackVaultController.cs"));
            string styles = File.ReadAllText(Path.Combine(assets, "UI", "PackReveal", "packreveal.uss"))
                .Replace("\r\n", "\n");

            Assert.Multiple(() =>
            {
                Assert.That(vault, Does.Contain("CreateRevealFaceBaker()"));
                Assert.That(reveal, Does.Contain("_faceBaker.Bake(definition"),
                    "The reveal must consume the canonical printed-card baker.");
                Assert.That(reveal, Does.Contain("Background.FromTexture2D(face)"));
                Assert.That(reveal, Does.Contain("fallback.style.display = DisplayStyle.None"),
                    "The art-and-name fallback must not flash before the printed card is ready.");
                Assert.That(reveal, Does.Contain("Length.Percent((100f / shown) - 1f)"),
                    "Six through ten premium cards must still consume the full gallery width.");
                Assert.That(reveal, Does.Contain("SortedDictionary<Rarity"),
                    "Receipt grouping must be rarity-first.");
                Assert.That(reveal, Does.Not.Contain("SortedDictionary<CardClass"),
                    "The result receipt must not be fragmented into class panels.");
                Assert.That(reveal, Does.Contain("rarity == Rarity.COMMON"));
                Assert.That(reveal, Does.Contain("BuildNamedAward(name)"),
                    "Rare and better awards must be listed by card name.");
                Assert.That(styles, Does.Contain(".reveal-cards {\n    width: 80%;"));
                Assert.That(styles, Does.Not.Contain("max-width: 300px;"),
                    "A fixed card cap must not stop a five-card row from filling its 80% gallery.");
                Assert.That(styles, Does.Contain(".reveal-list {\n    width: 80%;"));
                Assert.That(styles, Does.Contain(".reveal-list__panel"));
                Assert.That(styles, Does.Contain("background-color: rgba(7, 11, 17, 0.82);"),
                    "Receipt plates should retain the backdrop through a translucent surface.");
            });
        }
    }
}
