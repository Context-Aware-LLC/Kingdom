using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// #329 — the app boot-looped MainMenu → AccountCreate about four times a second, for ever,
    /// as soon as a player had a display name.
    ///
    /// <para>The cause was one commented-out line: <c>SignInGateController</c> looked up
    /// <c>signin-modal</c>, first run became a SCENE on 2026-08-25 and renamed that element to
    /// <c>signin-view</c>, and the lookup was commented out rather than updated. The field is
    /// <c>readonly</c>, so it was null for the life of the process, and <c>Open()</c> took its
    /// "the view is missing from the UXML" escape hatch on every single launch — invoking the
    /// success callback synchronously without ever showing a sign-in screen.</para>
    ///
    /// <para><b>Why a source test and not a behavioural one.</b> The failure had no failure: the
    /// skip path logs nothing, returns normally, and is indistinguishable from a player who
    /// signed in. It survived a green suite, a green gate and a device build. What can be checked
    /// cheaply is the wiring that made it possible, so that is what this checks — the element name
    /// the gate looks for must be the element name the screen actually has, and the escape hatch
    /// must latch.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class SignInGateBootLoopTests
    {
        private static string Gate() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
            "SignInGateController.cs"));

        private static string Screen() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "UI", "AccountCreate",
            "AccountCreateScreen.uxml"));

        /// <summary>The gate must look for an element the screen actually contains. Either half
        /// being renamed alone is the 2026-08-25 mistake exactly.</summary>
        [Test]
        public void Gate_binds_its_view_to_an_element_the_account_create_screen_declares()
        {
            string gate = Gate();
            // ANCHORED, so only whitespace may precede it. A bare search matches the COMMENTED-OUT
            // line too, which is the one thing this test exists to reject — #329 was a live lookup
            // turned into a comment, and a guard that accepts a comment as a binding reproduces the
            // bug it is guarding. (Verified against the pre-fix file: unanchored, this happily
            // matched `// _modal = root.Q<VisualElement>("signin-modal");`.)
            Match lookup = Regex.Match(
                gate, @"^[ \t]*_modal\s*=\s*root\.Q<VisualElement>\(""(?<name>[\w-]+)""\)",
                RegexOptions.Multiline);

            Assert.Multiple(() =>
            {
                Assert.That(lookup.Success, Is.True,
                    "The sign-in gate must bind its view. An unassigned readonly field is #329: "
                    + "permanently null, so every Open() skips the gate and returns success.");

                string name = lookup.Groups["name"].Value;
                Assert.That(Screen(), Does.Contain($@"name=""{name}"""),
                    $"The gate looks up '{name}', which AccountCreateScreen.uxml does not declare. "
                    + "A lookup that resolves to null cannot fail loudly — it silently opens the "
                    + "front door and boot-loops the app.");
            });
        }

        /// <summary>Continuing past a gate you could not show is allowed — a broken screen must not
        /// brick the boot. Continuing while still REPORTING the gate as outstanding is the loop,
        /// because MainMenu routes on exactly that answer and hands the player straight back.</summary>
        [Test]
        public void Skipping_the_gate_stops_it_reporting_itself_as_still_required()
        {
            string gate = Gate();
            Match isRequired = Regex.Match(gate, @"public bool IsRequired\s*=>(?<body>[^;]+);");

            Assert.Multiple(() =>
            {
                Assert.That(isRequired.Success, Is.True);
                Assert.That(isRequired.Groups["body"].Value, Does.Contain("Skipped"),
                    "IsRequired must account for a gate that already gave up this run, or a skip "
                    + "leaves MainMenu dispatching to AccountCreate for ever (#329).");

                // Statements may sit between the two (the funnel records the skip there); what matters
                // is that the latch comes first, with no return or other hand-off in between.
                Assert.That(gate, Does.Match(@"Skipped\s*=\s*true;(?:\s*\n[^\n{}]*?;){0,3}\s*\n\s*onSignedIn\?\.Invoke\(\);"),
                    "The escape hatch must latch BEFORE handing control on. Setting it afterwards, "
                    + "or not at all, is the boot loop.");
            });
        }
    }
}
