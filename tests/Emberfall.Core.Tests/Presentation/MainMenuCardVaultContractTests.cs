using System.IO;
using System.Linq;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class MainMenuCardVaultContractTests
    {
        [Test]
        public void Card_vault_uses_a_separate_socket_inside_the_navigation_plate()
        {
            string unityRoot = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall");
            string uxml = File.ReadAllText(Path.Combine(
                unityRoot, "Assets", "UI", "MainMenu", "MainMenuScreen.uxml"));
            string styles = File.ReadAllText(Path.Combine(
                unityRoot, "Assets", "UI", "MainMenu", "mainmenu.uss"));
            string controller = File.ReadAllText(Path.Combine(
                unityRoot, "Assets", "Scripts", "Presentation", "MainMenuController.cs"));
            string buildSettings = File.ReadAllText(Path.Combine(
                unityRoot, "ProjectSettings", "EditorBuildSettings.asset"));

            XDocument document = XDocument.Parse(uxml);
            XElement command = document.Descendants().Single(
                element => (string?)element.Attribute("name") == "command");
            XElement plate = command.Elements().Single(
                element => (string?)element.Attribute("name") == "nav-plate");
            XElement menu = plate.Elements().Single(
                element => (string?)element.Attribute("name") == "menu");
            XElement dock = plate.Elements().Single(
                element => (string?)element.Attribute("name") == "vault-dock");

            Assert.Multiple(() =>
            {
                Assert.That(menu.Descendants().Any(
                    element => (string?)element.Attribute("name") == "btn-card-vault"), Is.False,
                    "The vault must not become another member of the existing row-button group.");
                Assert.That(dock.Descendants().Any(
                    element => (string?)element.Attribute("name") == "btn-card-vault"), Is.True,
                    "The vault image needs its own socket and hit target.");
                Assert.That((string?)dock.Attribute("class"), Does.Not.Contain("mm-vault-dock--hidden"),
                    "The vault is a permanent destination, including when its balance is zero.");
                Assert.That(dock.Descendants().Any(
                    element => (string?)element.Attribute("name") == "vault-pack-count"), Is.True,
                    "The socket owns its count badge rather than borrowing unrelated HUD chrome.");
                Assert.That(dock.Descendants().Single(
                    element => (string?)element.Attribute("name") == "vault-pack-count")
                    .Attribute("text")?.Value, Is.EqualTo("× 0"),
                    "The permanent entry must have an honest pre-fetch empty state.");
                Assert.That(styles, Does.Contain(".mm-nav-plate {")
                    .And.Contain("ui_panel_outer_forged.png"));
                Assert.That(styles, Does.Contain(".mm-vault-button {")
                    .And.Contain(".mm-vault-art {")
                    .And.Contain("pack_ef_01_pyra_cinderbound_4.png")
                    .And.Contain("-unity-background-scale-mode: scale-to-fit;")
                    .And.Contain("border-radius: 52px;"),
                    "The temporary pack illustration must render in the distinct round control.");
                Assert.That(controller, Does.Contain("Wire(\"btn-card-vault\", OnCardVault);")
                    .And.Contain("PackVaultController.ReturnScene = \"MainMenu\";")
                    .And.Contain("RenderVaultEntry(snapshot);")
                    .And.Contain("Go(\"PackVault\");")
                    .And.Not.Contain("mm-vault-dock--hidden"));
                Assert.That(buildSettings, Does.Contain("path: Assets/Scenes/PackVault.unity")
                    .And.Contain("enabled: 1"),
                    "A live menu button must not target a scene absent from the player.");
            });
        }

        [Test]
        public void Onboarding_routes_on_the_starter_balance_and_the_marker_only_selects_mode()
        {
            string controller = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
                "MainMenuController.cs"));

            int balanceCheck = controller.IndexOf(
                "snapshot.PackCount(StarterPackId) <= 0", System.StringComparison.Ordinal);
            int markerRead = controller.IndexOf(
                "FirstRunMarkers.Has(FirstRunMarkers.Gift)", System.StringComparison.Ordinal);
            int vaultRoute = controller.IndexOf(
                "Go(\"PackVault\");", markerRead, System.StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(controller, Does.Contain(
                    "StarterPackId = OnboardingGrant.StarterPack().Id;"));
                Assert.That(controller, Does.Not.Contain(
                    "StarterPackId = \"pack.emberfall.starter\""),
                    "The route must follow the domain-owned starter SKU composition.");
                Assert.That(controller, Does.Contain("await PackGateway.ReadShelfAsync("),
                    "The launch route must use the live ledger projection, not local onboarding state.");
                Assert.That(balanceCheck, Is.GreaterThanOrEqualTo(0));
                Assert.That(markerRead, Is.GreaterThan(balanceCheck),
                    "The marker may be read only after the starter balance decides there is a route.");
                Assert.That(vaultRoute, Is.GreaterThan(markerRead));
                Assert.That(controller, Does.Contain("? PackVaultMode.Vault")
                    .And.Contain(": PackVaultMode.Gift;"),
                    "Seen gift means ordinary Vault mode; an unseen gift means Gift mode.");
            });
        }
    }
}
