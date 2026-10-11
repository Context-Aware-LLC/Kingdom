using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
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
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
            "CardPresenter.cs"));

        private static string Board() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
            "BoardController.cs"));

        private static string TmpSettings() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "TextMesh Pro", "Resources",
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

        /// <summary>
        /// This test asserted the hazard was ARMED (<c>m_autoSizeTextContainer: 1</c>) so that the
        /// local guard below had something to guard against. <c>0775f222</c> disarmed it globally —
        /// card text was running off the sides of the card, because container autosize makes
        /// <c>TMP_Text.Rebuild</c> write <c>sizeDelta = GetPreferredValues(Infinity, Infinity)</c>
        /// every frame, so wrapping never fires and font auto-fit never shrinks. Every
        /// TextMeshPro here is built in code with an authored rect and none of them wants that.
        ///
        /// <para>So the assertion inverts: the global setting staying OFF is now the protection,
        /// and it covers all thirteen sites. The local guard in MakeLabel is kept and still
        /// asserted — it is the one that survives someone flipping the global back.</para>
        /// </summary>
        [Test]
        public void The_authored_title_rectangle_cannot_be_rewritten_by_TMP_container_autosize()
        {
            Assert.That(TmpSettings(), Does.Contain("m_autoSizeTextContainer: 0"),
                "Turning the project's global TMP container-autosize back on re-arms this "
                + "regression at all thirteen code-built TextMeshPro sites at once, and only the "
                + "two with a local guard would survive it.");
            Assert.That(Board(), Does.Contain("tmp.autoSizeTextContainer = false;"),
                "MakeLabel authors an exact card-relative rectangle. TMP container autosize must "
                + "not replace that rectangle with a text-derived width while font auto-fit is "
                + "trying to fit the title inside it.");
        }
    }
}
