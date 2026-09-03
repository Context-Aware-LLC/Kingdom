using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// Slice 4 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>. Both tests here cover a
    /// failure with no runtime signal at all — the first ships copy about a verb that does not
    /// exist, the second renders an unstyled box and reports nothing.
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class FirstRunTidbitContractTests
    {
        private static string UnityAssets =>
            Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");

        /// <summary>
        /// The two tail tidbits describe crafting and the store, and neither has a route — spec §4,
        /// slice 4: "teaching a verb that is not wired is worse than teaching nothing".
        ///
        /// <para><b>This test is the retirement trigger, not just a guard.</b> It asserts the stubs
        /// are still stubs, so the commit that wires crafting or the store makes it FAIL and names
        /// what to do. Registering the copy early and wiring the verb late are two different
        /// mistakes and this catches both.</para>
        /// </summary>
        [Test]
        public void Held_tidbits_stay_unregistered_for_exactly_as_long_as_their_verbs_are_stubs()
        {
            string catalog = File.ReadAllText(Path.Combine(
                UnityAssets, "Scripts", "Presentation", "TidbitCatalog.cs"));
            string collection = File.ReadAllText(Path.Combine(
                UnityAssets, "Scripts", "Presentation", "CollectionController.cs"));
            string shop = File.ReadAllText(Path.Combine(
                UnityAssets, "Scripts", "Presentation", "ShopPanelController.cs"));

            int registered = catalog.IndexOf("Tidbit[] Registered", StringComparison.Ordinal);
            int held = catalog.IndexOf("Tidbit[] Held", StringComparison.Ordinal);
            Assert.That(registered, Is.GreaterThanOrEqualTo(0), "TidbitCatalog.Registered is gone.");
            Assert.That(held, Is.GreaterThan(registered), "TidbitCatalog.Held is gone.");

            string registeredBlock = catalog.Substring(registered, held - registered);
            string heldBlock = catalog.Substring(held);

            bool craftingIsStub = collection.Contains("[Craft] STUB:", StringComparison.Ordinal);
            bool storeIsStub = shop.Contains(
                "Purchases are waiting for the server wallet connection.", StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(registeredBlock, Does.Contain("\"characters\""),
                    "The one tidbit that ships must stay registered.");

                if (craftingIsStub)
                {
                    Assert.That(registeredBlock, Does.Not.Contain("\"shards\""),
                        "Crafting is still a [Craft] STUB in CollectionController — the shards " +
                        "tidbit teaches a verb the player cannot perform.");
                    Assert.That(heldBlock, Does.Contain("\"shards\""),
                        "The held copy is final and must stay written out, not deleted.");
                }
                else
                {
                    Assert.That(registeredBlock, Does.Contain("\"shards\""),
                        "Crafting now has a route: move the shards entry from Held to Registered " +
                        "(TidbitCatalog's retirement trigger).");
                }

                if (storeIsStub)
                {
                    Assert.That(registeredBlock, Does.Not.Contain("\"store\""),
                        "The store still answers 'Purchases are waiting for the server wallet " +
                        "connection.' — the store tidbit teaches a verb the player cannot perform.");
                    Assert.That(heldBlock, Does.Contain("\"store\""));
                }
                else
                {
                    Assert.That(registeredBlock, Does.Contain("\"store\""),
                        "The store now has a route: move the store entry from Held to Registered " +
                        "(TidbitCatalog's retirement trigger).");
                }
            });
        }

        /// <summary>
        /// Every <c>ds-</c> rule in the design system reads variables that live ONLY in
        /// DesignTokens.uss — <c>theme.uss</c> carries 6 of the ~72, and none of the ones
        /// <c>.ds-modal</c> and <c>.ds-backdrop</c> need for a surface, a border or a dim.
        ///
        /// <para>A screen that links Overlays.uss and forgets the tokens still <i>renders</i>: a
        /// transparent, borderless box with no dim behind it, and not one line in the console. USS
        /// has no import for a consumer to lean on, so the pair has to be re-linked by hand on every
        /// screen — which is exactly the step that gets missed.</para>
        ///
        /// <para><b>It reads ELEMENTS, not the file text, and that is not fussiness.</b> The first
        /// cut of this test matched the raw string and passed with the link deleted, because the
        /// UXML carries a comment explaining the pair — the comment satisfied the assertion the
        /// comment exists to prevent. A stylesheet link is an element; a class is an attribute; a
        /// note about either is neither.</para>
        /// </summary>
        [Test]
        public void A_screen_using_design_system_classes_links_the_tokens_they_read()
        {
            string uiRoot = Path.Combine(UnityAssets, "UI");
            var offenders = Directory
                .EnumerateFiles(uiRoot, "*.uxml", SearchOption.AllDirectories)
                .Select(path => (path, doc: XDocument.Parse(File.ReadAllText(path))))
                .Where(f => f.doc.Descendants().Any(element =>
                    Regex.IsMatch((string?)element.Attribute("class") ?? "", "(^|\\s)ds-")))
                .Where(f => !f.doc.Descendants()
                    .Where(element => element.Name.LocalName == "Style")
                    .Select(element => (string?)element.Attribute("src") ?? "")
                    .Any(src => src.Contains("DesignTokens.uss", StringComparison.Ordinal)
                             || src.Contains("DesignSystem.uss", StringComparison.Ordinal)))
                .Select(f => Path.GetFileName(f.path))
                .ToList();

            Assert.That(offenders, Is.Empty,
                "These screens use ds- classes without linking DesignTokens.uss, so every colour, " +
                "radius and border in them resolves to nothing and renders silently: "
                + string.Join(", ", offenders));
        }
    }
}
