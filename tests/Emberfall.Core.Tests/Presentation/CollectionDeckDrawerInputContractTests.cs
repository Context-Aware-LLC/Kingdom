using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    // Source contract only: live UI Toolkit picking and wheel dispatch still need an Editor check.
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionDeckDrawerInputContractTests
    {
        [Test]
        public void Outside_click_blocker_is_enabled_only_for_the_deck_chooser()
        {
            string source = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "Scripts", "Presentation", "CollectionController.cs"));
            int start = source.IndexOf("private void ShowDrawerPane(bool editor)", System.StringComparison.Ordinal);
            int end = source.IndexOf("private void RebuildDeckChooser()", start, System.StringComparison.Ordinal);
            string paneTransition = source.Substring(start, end - start);

            Assert.Multiple(() =>
            {
                Assert.That(paneTransition, Does.Contain(
                    "_deckDrawerScrim?.EnableInClassList(\"deck-drawer-scrim--open\", !editor);"),
                    "Selecting a deck must release the wall; returning to the chooser must restore outside-click dismissal.");
                Assert.That(source, Does.Not.Contain(
                    "_deckDrawerScrim?.AddToClassList(\"deck-drawer-scrim--open\");"),
                    "Opening a drawer must not unconditionally block card clicks and wheel events while editing.");
                Assert.That(source, Does.Contain(
                    "_deckDrawerScrim?.RemoveFromClassList(\"deck-drawer-scrim--open\");"),
                    "Closing the drawer must also remove its input blocker.");
            });
        }
    }
}
