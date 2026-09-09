using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// The collection keeps its layout live while the loading plate is shown, because the wall
    /// needs a real viewport before it can choose its initial bake size. Hiding by inherited
    /// visibility is not sufficient: CardWall deliberately stamps visible on populated tiles,
    /// and a descendant may override an inherited hidden value. Parent opacity suppresses the
    /// composited subtree without taking it out of layout or letting a tile opt back in.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionLoadingTransitionContractTests
    {
        [Test]
        public void Loading_plate_suppresses_the_composited_collection_without_collapsing_layout()
        {
            string path = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
                "CollectionController.cs");
            string source = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("_root.style.opacity = 0f;"),
                    "The loading state must make the entire composited UI subtree transparent; "
                    + "CardWall tiles set their own visibility and can override inherited hidden.");
                Assert.That(source, Does.Contain("_root.style.opacity = 1f;"),
                    "The collection must become opaque only after LoadingOverlay.OnFadedOut.");
                Assert.That(source, Does.Not.Contain("_root.style.visibility = Visibility.Hidden;"),
                    "Inherited visibility lets CardWall's inline Visibility.Visible leak tiles "
                    + "over the world-space loading plate.");
                Assert.That(source, Does.Contain("_loading.OnFadedOut += RevealScreen;"),
                    "The collection reveal must remain tied to the plate's completed fade.");
            });
        }
    }
}
