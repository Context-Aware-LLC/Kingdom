using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// UI Toolkit paints the collection's live RenderTexture backgrounds incorrectly on Android:
    /// the source texture reads back with its socket rings and rarity gem intact, while the panel
    /// omits those layers. The wall therefore consumes an immutable Texture2D snapshot. The
    /// ListView bind callbacks also cannot initiate captures: that lazy path reproduces the frame
    /// drawing over the socket and gem layers in the Editor. Other CardSnapshot users may keep
    /// RenderTexture because they do not cross this UI Toolkit seam.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionCardFaceTextureContractTests
    {
        [Test]
        public void Collection_freezes_card_captures_before_handing_them_to_ui_toolkit()
        {
            string presentation = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation");
            string baker = File.ReadAllText(Path.Combine(presentation, "CardFaceBaker.cs"));
            string wall = File.ReadAllText(Path.Combine(presentation, "CardWall.cs"));
            string craft = File.ReadAllText(Path.Combine(presentation, "CraftModal.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(baker, Does.Contain("Dictionary<int, Texture2D>"),
                    "The collection cache must own immutable Texture2D faces, not live render targets.");
                Assert.That(baker, Does.Contain(".ReadPixels("),
                    "Freezing must cross a synchronizing readback boundary after Camera.Render.");
                Assert.That(baker, Does.Contain("TryGetCachedFace"),
                    "Virtualized rows need a read-only cache API that cannot start lazy work.");
                Assert.That(wall, Does.Contain("_bakesOutstanding = _pool.Count"),
                    "The loading pass must bake the complete pool, not only the initial viewport.");
                Assert.That(wall, Does.Contain("_baker.TryGetCachedFace(def.Id"),
                    "ListView.bindItem must only display a face completed by the eager pass.");
                Assert.That(wall, Does.Contain("Background.FromTexture2D(face)"),
                    "UI Toolkit must paint the frozen texture path that holds on Android.");
                Assert.That(wall, Does.Not.Contain("Background.FromRenderTexture(face)"),
                    "The RenderTexture background path is the reproduced Android paint failure.");
                Assert.That(craft, Does.Contain("Background.FromTexture2D((Texture2D)tex)"),
                    "The crafting modal shares the collection baker and must consume its frozen face.");
                Assert.That(craft, Does.Not.Contain("Background.FromRenderTexture((RenderTexture)tex)"),
                    "No collection UI Toolkit consumer may cast the frozen face back to RenderTexture.");
            });
        }
    }
}
