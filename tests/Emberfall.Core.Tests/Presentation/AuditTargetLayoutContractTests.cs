using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class AuditTargetLayoutContractTests
    {
        private static string Asset(params string[] parts)
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            foreach (string part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        private static string Rule(string styles, string selector)
        {
            Match match = Regex.Match(
                styles,
                @"(?ms)^" + Regex.Escape(selector) + @"\s*\{(?<body>.*?)^\}");
            Assert.That(match.Success, Is.True, $"Missing USS rule: {selector}");
            return match.Groups["body"].Value;
        }

        [Test]
        public void Crafting_actions_use_the_full_body_height_without_inline_geometry_overrides()
        {
            string uxml = Asset("UI", "Collection", "CollectionScreen.uxml");
            string styles = Asset("UI", "Collection", "collection.uss");
            XDocument document = XDocument.Parse(uxml);
            XElement modal = document.Descendants()
                .Single(element => (string?)element.Attribute("name") == "craft-modal");
            string inlineStyle = (string?)modal.Attribute("style") ?? string.Empty;
            string halves = Rule(styles, ".craft-halves");
            string half = Rule(styles, ".craft-half");

            Assert.Multiple(() =>
            {
                Assert.That(inlineStyle, Does.Not.Match(@"\b(left|right|top|bottom)\s*:"),
                    "UI Builder geometry overrides the safe-area USS and can push the modal below the screen.");
                Assert.That(halves, Does.Contain("flex-direction: row;"),
                    "Render and Reduce need the body's full height. Stacking them gives each half less "
                    + "height than its labels and button, so Unity shrinks the boxes while text still draws through.");
                Assert.That(halves, Does.Contain("justify-content: space-between;"));
                Assert.That(half, Does.Match(@"width:\s*49%;"));
                Assert.That(half, Does.Contain("margin-bottom: 0;"));
            });
        }

        [Test]
        public void Shop_pack_copy_wraps_and_the_purchase_row_stays_in_normal_flow()
        {
            string styles = Asset("UI", "Shop", "shop.uss");
            string pack = Rule(styles, ".shop-pack");
            string detail = Rule(styles, ".shop-pack__detail");
            string actions = Rule(styles, ".shop-pack__actions");

            Assert.Multiple(() =>
            {
                Assert.That(pack, Does.Contain("flex-shrink: 0;"),
                    "A pack must size itself from its content inside the vertical ScrollView.");
                Assert.That(detail, Does.Contain("white-space: normal;"),
                    "The one-line description in audit-3 paints through the neighbouring pack.");
                Assert.That(actions, Does.Contain("flex-shrink: 0;"),
                    "Purchase buttons must remain part of the card's measured height instead of collapsing below it.");
            });
        }
    }
}
