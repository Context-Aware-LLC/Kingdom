using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// A pool replacement can change both the number and identity of virtualized rows. Rebinding
    /// only the rows Unity currently realizes leaves rows from a wider pool visible after entering
    /// Owned-only deck editing; ordinary query changes may keep the cheaper rebind path.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CardWallPoolRefreshContractTests
    {
        [Test]
        public void Replacing_The_Pool_Rebuilds_Virtualized_Rows_While_Querying_Only_Rebinds()
        {
            string source = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
                "CardWall.cs"));

            string setPool = Slice(source, "public void SetPool", "public void SetQuery");
            string setQuery = Slice(source, "public void SetQuery", "public void RefreshTiles");
            string applyQuery = Slice(source, "private void ApplyQuery", "private VisualElement MakeRow");

            Assert.Multiple(() =>
            {
                Assert.That(setPool, Does.Contain("ApplyQuery(rebuild: true)"),
                    "A narrower ownership/deck pool must discard realized rows from the old pool.");
                Assert.That(setQuery, Does.Contain("ApplyQuery(rebuild: false)"),
                    "Facet and sort changes can retain the cheaper visible-row rebind.");
                Assert.That(applyQuery, Does.Contain("if (rebuild)").And.Contain("_list.Rebuild()"));
                Assert.That(applyQuery, Does.Contain("else").And.Contain("_list.RefreshItems()"));
            });
        }

        private static string Slice(string source, string start, string end)
        {
            int from = source.IndexOf(start, System.StringComparison.Ordinal);
            int to = source.IndexOf(end, from + start.Length, System.StringComparison.Ordinal);
            Assert.That(from, Is.GreaterThanOrEqualTo(0), $"Missing start marker: {start}");
            Assert.That(to, Is.GreaterThan(from), $"Missing end marker: {end}");
            return source.Substring(from, to - from);
        }
    }
}
