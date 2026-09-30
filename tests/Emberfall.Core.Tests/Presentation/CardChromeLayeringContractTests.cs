using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// The card's SpriteRenderers live inside one SortingGroup. Chrome that overlaps the frame
    /// therefore needs an explicit renderer order; tiny Z offsets alone are not a stable contract
    /// across the match camera and the collection-face bake camera.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CardChromeLayeringContractTests
    {
        private static string PresentationFile(string name) => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", name));

        [Test]
        public void Rarity_gem_and_bezel_render_above_the_card_frame()
        {
            string board = PresentationFile("BoardController.cs");
            string catalog = PresentationFile("FrameRarityCatalog.cs");

            Assert.Multiple(() =>
            {
                Assert.That(catalog, Does.Contain("public const int FrameOrder = 0;"));
                Assert.That(catalog, Does.Contain("public const int GemSocketOrder = FrameOrder + 1;"));
                Assert.That(catalog, Does.Contain("public const int GemOrder = GemSocketOrder + 1;"));
                Assert.That(board, Does.Contain(
                    "frameRenderer.sortingOrder = FrameRarityCatalog.FrameOrder;"));
                Assert.That(board, Does.Contain(
                    "gemSocket.sortingOrder = FrameRarityCatalog.GemSocketOrder;"));
                Assert.That(board, Does.Contain(
                    "gemRenderer.sortingOrder = FrameRarityCatalog.GemOrder;"));
            });
        }
    }
}
