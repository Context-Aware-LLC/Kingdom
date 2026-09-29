using System;
using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class CraftModalSyncContractTests
    {
        [Test]
        public void Opening_crafting_reads_one_card_without_resyncing_the_collection()
        {
            string controller = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "Scripts", "Presentation", "CollectionController.cs"));
            string openCraft = Between(controller,
                "private async void OpenCraft(CardDefinition def)",
                "private bool CraftIsCurrent");
            string commitCraft = Between(controller,
                "private async void CommitCraft(CardDefinition def, bool render)",
                "private static string CraftError");

            Assert.Multiple(() =>
            {
                Assert.That(openCraft, Does.Contain("CraftingGateway.ReadAsync(def.Id, accessToken, ct)"),
                    "Opening the modal still needs the authoritative card and shard snapshot.");
                Assert.That(openCraft, Does.Contain("ShowCraft(def, reply)"),
                    "The card-specific snapshot should be enough to draw both offers.");
                Assert.That(openCraft, Does.Not.Contain("RefreshCraftCollection"),
                    "Inspecting a card must not download and rebuild the complete collection.");
                Assert.That(commitCraft, Does.Contain("await RefreshCraftCollection(accessToken, ct)"),
                    "A confirmed Render or Reduce changes ownership and must still refresh the collection.");
            });
        }

        private static string Between(string source, string startMarker, string endMarker)
        {
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing source marker: {startMarker}");
            Assert.That(end, Is.GreaterThan(start), $"Missing source marker: {endMarker}");
            return source.Substring(start, end - start);
        }
    }
}
