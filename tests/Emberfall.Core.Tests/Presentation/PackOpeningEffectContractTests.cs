using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class PackOpeningEffectContractTests
    {
        [Test]
        public void Pack_tear_has_no_full_pack_ellipse()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string animator = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "PackTearAnimator.cs"));
            string styles = File.ReadAllText(Path.Combine(
                assets, "UI", "PackVault", "packvault.uss"));

            Assert.Multiple(() =>
            {
                Assert.That(animator, Does.Not.Contain("tear-burst"),
                    "The opening beat must not create the pale ellipse around the whole pack.");
                Assert.That(styles, Does.Not.Contain(".tear-burst"),
                    "The removed full-pack ellipse must not survive as dormant presentation CSS.");
                Assert.That(animator, Does.Contain("tear-half--parted"),
                    "Removing the ellipse must leave the physical wrapper split intact.");
                Assert.That(styles, Does.Contain(".tear-half--parted"),
                    "The torn wrapper pieces still need their separation transition.");
            });
        }
    }
}
