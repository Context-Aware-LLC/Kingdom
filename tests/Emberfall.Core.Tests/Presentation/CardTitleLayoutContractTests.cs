using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// Card faces are shared by the match, focus reveal, collection-wall bake and craft preview.
    /// Their one-line title must consume its available width before ellipsis becomes eligible.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CardTitleLayoutContractTests
    {
        private static string Presenter() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
            "CardPresenter.cs"));

        private static string Board() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
            "BoardController.cs"));

        private static string TmpSettings() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "TextMesh Pro", "Resources",
            "TMP Settings.asset"));

        [Test]
        public void Card_titles_auto_fit_before_falling_back_to_ellipsis()
        {
            string presenter = Presenter();

            Assert.Multiple(() =>
            {
                Assert.That(presenter,
                    Does.Contain("nameLabel.enableAutoSizing = true;"),
                    "A fixed-size title hands every long name directly to ellipsis even though "
                    + "the title can remain readable by shrinking within its band.");
                Assert.That(presenter,
                    Does.Contain("nameLabel.fontSizeMax = FrameRarityCatalog.NameEm * H * 10f;"),
                    "Short titles must retain the authored display size.");
                Assert.That(presenter,
                    Does.Contain("nameLabel.fontSizeMin = FrameRarityCatalog.NameEmFloor * H * 10f;"),
                    "Auto-fit needs a readable floor so ellipsis remains the final fallback.");
                Assert.That(presenter,
                    Does.Contain("nameLabel.overflowMode = TextOverflowModes.Ellipsis;"),
                    "Names beyond the readable floor must stay inside the card face.");
            });
        }

        [Test]
        public void The_authored_title_rectangle_cannot_be_rewritten_by_TMP_container_autosize()
        {
            Assert.That(TmpSettings(), Does.Contain("m_autoSizeTextContainer: 1"),
                "This regression is armed by the project's global TMP container-autosize setting.");
            Assert.That(Board(), Does.Contain("tmp.autoSizeTextContainer = false;"),
                "MakeLabel authors an exact card-relative rectangle. TMP container autosize must "
                + "not replace that rectangle with a text-derived width while font auto-fit is "
                + "trying to fit the title inside it.");
        }
    }
}
