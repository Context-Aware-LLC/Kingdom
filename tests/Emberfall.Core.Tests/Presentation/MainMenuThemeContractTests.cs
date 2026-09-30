using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// The runtime dropdown menu is attached to the panel root rather than beneath the field that
    /// opened it. Main-menu UXML styles therefore cannot make its labels readable; the rule has to
    /// live in the PanelSettings theme, while the selected value stays local to the field.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuThemeContractTests
    {
        [Test]
        public void Character_class_dropdown_text_is_styled_at_both_runtime_roots()
        {
            string uiDirectory = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "UI", "MainMenu");
            string themePath = Path.Combine(uiDirectory, "MainMenuTheme.tss");
            string themeMetaPath = themePath + ".meta";
            string panelSettingsPath = Path.Combine(uiDirectory, "MainMenuPanelSettings.asset");
            string screenStylesPath = Path.Combine(uiDirectory, "mainmenu.uss");

            string theme = File.ReadAllText(themePath);
            string themeMeta = File.ReadAllText(themeMetaPath);
            string panelSettings = File.ReadAllText(panelSettingsPath);
            string screenStyles = File.ReadAllText(screenStylesPath);
            // `\r?$` and not `$`: under RegexOptions.Multiline .NET anchors `$` before the `\n`
            // alone, so on a CRLF checkout the `\r` sits between the hex and the anchor and the
            // match fails. This file arrives LF or CRLF depending on the clone, which made the
            // assertion pass for the author and fail on rebase into a checkout that normalised it.
            Match guid = Regex.Match(themeMeta, @"^guid: ([0-9a-f]{32})\r?$", RegexOptions.Multiline);

            Assert.Multiple(() =>
            {
                Assert.That(guid.Success, Is.True, "The Main Menu theme needs a committed Unity GUID.");
                Assert.That(panelSettings, Does.Contain($"guid: {guid.Groups[1].Value}"),
                    "The panel must load the Main Menu-specific theme so reparented popup rows see it.");
                Assert.That(theme, Does.Match(
                    @"(?s)\.unity-base-dropdown__label\s*\{[^}]*color:\s*var\(--unity-colors-dropdown-text\)"),
                    "Popup option labels need the default theme's contrasting dropdown text token.");
                Assert.That(screenStyles, Does.Match(
                    @"(?s)\.mm-modal__dropdown\s+\.unity-base-popup-field__text\s*\{[^}]*color:\s*var\(--ink-1\)"),
                    "The closed fields need an explicit readable selected-value color.");
            });
        }
    }
}
