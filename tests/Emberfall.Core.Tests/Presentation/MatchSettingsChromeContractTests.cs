using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class MatchSettingsChromeContractTests
    {
        [Test]
        public void Persistent_settings_control_clips_its_opaque_sprite_corners()
        {
            string styles = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "UI", "MatchSettings", "Resources", "matchsettings.uss"));

            Assert.That(styles, Does.Match(
                    @"(?s)\.ms-gear\s*\{[^}]*border-radius:\s*var\(--space-2\);[^}]*overflow:\s*hidden;"),
                "The shared settings chrome must reveal surfaces beneath its rounded corners instead of painting an opaque square.");
        }
    }
}
