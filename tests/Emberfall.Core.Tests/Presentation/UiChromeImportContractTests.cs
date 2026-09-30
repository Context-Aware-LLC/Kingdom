using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class UiChromeImportContractTests
    {
        private static readonly IReadOnlyList<string> TransparentChromeSprites = new[]
        {
            "ui_button_primary_ember.png",
            "ui_button_secondary_slate.png",
            "ui_input_recessed_slate.png",
            "ui_panel_outer_forged.png",
            "ui_panel_recessed_slate.png",
            "ui_scroll_fade.png",
        };

        [Test]
        public void Transparent_chrome_uses_the_input_texture_alpha_channel()
        {
            string chromeDirectory = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Art", "UI", "Chrome");

            Assert.Multiple(() =>
            {
                foreach (string sprite in TransparentChromeSprites)
                {
                    string metadata = File.ReadAllText(Path.Combine(chromeDirectory, sprite + ".meta"));
                    Assert.That(metadata, Does.Contain("alphaUsage: 1"),
                        $"{sprite} must use Input Texture Alpha. Ignoring its alpha channel "
                        + "turns transparent nine-slice centers and corners into opaque rectangles.");
                    Assert.That(metadata, Does.Contain("alphaIsTransparency: 1"),
                        $"{sprite} must treat its input alpha as transparency to avoid edge-color bleed.");
                }
            });
        }
    }
}
