using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class CraftModalLayoutContractTests
    {
        [Test]
        public void Craft_processing_does_not_insert_a_status_row_above_the_card()
        {
            string modal = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "Scripts", "Presentation", "CraftModal.cs"));

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
