using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class CollectionDecksButtonStyleTests
    {
        [Test]
        public void Secondary_chrome_decks_button_has_no_gold_underlay_in_its_transparent_corners()
        {
            string collection = Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "UI", "Collection");
            XDocument document = XDocument.Load(Path.Combine(collection, "CollectionScreen.uxml"));
            XElement decksButton = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "decks-button");
            string[] classes = ((string?)decksButton.Attribute("class") ?? "")
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            string styles = File.ReadAllText(Path.Combine(collection, "collection.uss"));

            Assert.Multiple(() =>
            {
                Assert.That(classes, Does.Contain("col-button--decks"));
                Assert.That(classes, Does.Contain("chrome-button--secondary"),
                    "The slate nine-slice is the visible Decks button surface.");
                Assert.That(styles, Does.Match(
                        @"(?s)\.col-button--decks\.chrome-button--secondary\s*\{[^}]*background-color:\s*transparent;"),
                    "The slate sprite has transparent outer corners; an accent underlay leaks through them as gold corners.");
            });
        }
    }
}
