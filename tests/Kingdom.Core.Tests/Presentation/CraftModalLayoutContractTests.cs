using System.IO;
using System.Linq;
using System.Xml.Linq;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class CraftModalLayoutContractTests
    {
        [TestCase("craft-render-cost")]
        [TestCase("craft-reduce-payout")]
        [TestCase("craft-shard-balance")]
        public void Craft_currency_amount_has_a_shard_icon_instead_of_a_word(string amountName)
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            XDocument document = XDocument.Load(Path.Combine(assets, "UI", "Collection", "CollectionScreen.uxml"));
            XElement amount = document.Descendants().Single(e => (string?)e.Attribute("name") == amountName);
            XElement[] icons = amount.Parent!.Elements().Where(e =>
                ((string?)e.Attribute("class") ?? "").Split(' ').Contains("craft-shard-icon")).ToArray();
            string styles = File.ReadAllText(Path.Combine(assets, "UI", "Collection", "collection.uss"));
            string texture = Path.Combine(assets, "Art", "UI", "Crafting", "Currency", "craft-shard-cyan-v1.png");
            string importer = File.ReadAllText(Path.Combine(assets, "Scripts", "Editor", "CardArtPostprocessor.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(icons, Has.Length.EqualTo(1));
                Assert.That(amount.Parent.Elements().Any(e => (string?)e.Attribute("text") == "shards"), Is.False);
                Assert.That(styles, Does.Contain("/Assets/Art/UI/Crafting/Currency/craft-shard-cyan-v1.png"));
                Assert.That(importer, Does.Contain("[\"currency\"] = ApplyCurrencyIcon"));
                Assert.That(importer, Does.Contain("importer.filterMode = FilterMode.Trilinear;"));
                Assert.That(File.Exists(texture), Is.True);
                Assert.That(File.ReadAllText(texture + ".meta"), Does.Contain("enableMipMap: 1"));
                Assert.That(File.ReadAllText(texture + ".meta"), Does.Contain("filterMode: 2"));
            });
            Assert.That((string?)icons.Single().Attribute("tooltip"), Is.EqualTo("Shards"));
        }

        [Test]
        public void Craft_processing_does_not_insert_a_status_row_above_the_card()
        {
            string modal = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Assets", "Scripts", "Presentation", "CraftModal.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(modal, Does.Contain("SetWorking(string.Empty, submitting: true)"),
                    "The animation is the processing feedback; text would resize the card area.");
                Assert.That(modal, Does.Contain("unknown || submitting || string.IsNullOrEmpty(message)"),
                    "Loading and submitting must keep the status label out of layout.");
                Assert.That(modal, Does.Not.Contain("\"Rendering...\""));
                Assert.That(modal, Does.Not.Contain("\"Reducing...\""));
                Assert.That(modal, Does.Contain("public void ShowError(string message)"),
                    "Failures must retain readable recovery feedback.");
            });
        }
    }
}
