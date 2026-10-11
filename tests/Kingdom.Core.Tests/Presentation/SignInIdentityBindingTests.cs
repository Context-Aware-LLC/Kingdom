using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// A successful sign-in must bind this device's collection scope to the identity that signed in.
    ///
    /// <para><b>The defect this guards has already shipped once, and it presented as data loss.</b>
    /// The server has always been portable — the Supabase subject hashes to a stable <c>player_id</c>
    /// that the entitlement ledger is keyed on — but the local scope was keyed on an id minted on the
    /// device, with nothing joining the two. Measured across a reinstall on 2026-08-29: the same email
    /// produced scope <c>ad7278eb…</c> and then <c>44f5749f…</c>, and the display name and roster in
    /// the first were unreachable from the second. <c>DeviceAccounts.BindToIdentity</c> is the join.</para>
    ///
    /// <para><b>Why a source test, for the same reason as #329.</b> A sign-in path that forgets to
    /// bind has no failure: it signs the player in, returns normally, and hands them a scope that is
    /// merely empty. There is no exception, no log line and no assertion that can tell an empty
    /// collection from a new player. What can be checked cheaply is that no path reaches the
    /// continuation on its own, so that is what this checks.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER:</b> if the gate stops owning the continuation — the binding moves
    /// into <c>AuthTokenProvider</c>, or sign-in stops being a Unity screen — delete this fixture with
    /// that change. It pins a shape, not a behaviour, and a shape that no longer exists fails for the
    /// wrong reason.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class SignInIdentityBindingTests
    {
        private static string Gate() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
            "SignInGateController.cs"));

        /// <summary>
        /// Exactly one place invokes the post-sign-in continuation, and it is the one that binds.
        ///
        /// <para>There are five ways into a session on this screen — a typed code, a typed password, a
        /// tapped mail link on a cold start, the same link on a running process, and Google. Each one
        /// used to hide the modal and invoke <c>_onSignedIn</c> itself, which is four opportunities to
        /// add a fifth that does not bind.</para>
        /// </summary>
        [Test]
        public void Only_one_path_hands_control_on_after_a_sign_in()
        {
            string gate = Gate();

            MatchCollection invocations = Regex.Matches(gate, @"\bdone\?\.Invoke\(\)");

            Assert.That(invocations.Count, Is.EqualTo(1),
                "Every successful sign-in must funnel through the one method that binds this device "
                + "to the signed-in identity. A second invocation site is a path that can sign a "
                + "player in and hand them an unbound scope — which looks exactly like the reinstall "
                + "data loss this replaced, and reports nothing.");
        }

        /// <summary>
        /// The one path also pulls the collection down, between binding and handing control on.
        ///
        /// <para><b>Binding finds the folder; this is what puts anything back in it.</b> They are
        /// separate halves of the same promise and they fail identically — a sign-in that binds but
        /// never syncs lands the player in the correct, empty scope, which renders as <i>owns
        /// everything</i> and reports nothing. Order matters for the same reason the binding's does:
        /// the sync writes through <c>PlayerAccount.Local</c>, so a sync before the bind writes the
        /// server's answer into whichever scope the device happened to mint.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER:</b> if the collection stops being synced at sign-in — moved
        /// to app start, to the collection screen, or to a push — delete this one assertion with that
        /// change. The other two pin the binding and outlive it.</para>
        /// </summary>
        [Test]
        public void The_one_path_syncs_the_collection_between_binding_and_handing_control_on()
        {
            Assert.That(Gate(), Does.Match(
                @"(?s)DeviceAccounts\.TryBindToIdentity\([^)]*\);.*?"
                + @"CollectionGateway\.SyncAsync\(.*?done\?\.Invoke\(\)"),
                "The gate must sync the collection after binding and before invoking the "
                + "continuation. Without the sync a reinstalled player reaches the right identity and "
                + "an empty local scope, which the collection wall renders as owning every card — the "
                + "exact opposite of the truth, with nothing anywhere reporting a problem.");
        }

        private static string Menu() => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation",
            "MainMenuController.cs"));

        /// <summary>
        /// A launch that is ALREADY signed in restores the player's data too.
        ///
        /// <para><b>The gate's path covers the sign-in TRANSITION and nothing else.</b> It runs when
        /// the gate was shown — the first launch after installing — and never again, so for every
        /// subsequent launch, which is the normal case, binding and both syncs simply did not
        /// happen. Measured on a device 2026-09-02: a launch with a restored session produced no
        /// <c>[SignIn]</c>, <c>[Roster]</c> or <c>[Collection]</c> line at all, the collection wall
        /// rendered the full pool off an absent ownership record, and the server's roster stayed on
        /// the server.</para>
        ///
        /// <para><b>It fails silently, which is why it is pinned in source.</b> Exactly like the
        /// binding above: nothing throws, nothing logs, and the result is a screen that looks
        /// populated. The cheap check is that the menu's boot still names the two gateways.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER:</b> when the client blocks the boot on <c>GET /session</c>
        /// (spec <c>login-handshake.md</c> §2), the resume moves there and this fixture's subject
        /// changes with it — update the path this reads, or delete this one assertion if the menu
        /// stops owning the boot.</para>
        /// </summary>
        [Test]
        public void An_already_signed_in_launch_binds_and_syncs_before_the_menu_appears()
        {
            string menu = Menu();

            Assert.That(menu, Does.Match(
                @"(?s)DeviceAccounts\.TryBindToIdentity\([^)]*\);.*?"
                + @"CollectionGateway\.SyncSnapshotAsync\(.*?RosterGateway\.SyncAsync\(.*?BeginFirstRun\(collection\)"),
                "MainMenuController must bind the identity and run both syncs before it hands the "
                + "player the menu. Without it only the very first launch after an install ever "
                + "restores anything: every launch after that reaches the right identity with a "
                + "stale local cache, and an absent ownership record renders as owning every card.");
        }

        /// <summary>The one path binds, and binds before it hands control on — the continuation's
        /// first act is to read <c>PlayerAccount.Local</c> to decide whether a display name is still
        /// owed, and that read has to land in the bound account.</summary>
        [Test]
        public void The_one_path_binds_the_identity_before_handing_control_on()
        {
            Assert.That(Gate(), Does.Match(
                @"(?s)DeviceAccounts\.TryBindToIdentity\([^)]*\);.*?done\?\.Invoke\(\)"),
                "The gate must call DeviceAccounts.TryBindToIdentity before invoking the "
                + "continuation. Binding afterwards means the name step reads whichever scope the "
                + "device happened to mint, and can ask a returning player for a name they already "
                + "have.");
        }
    }
}
