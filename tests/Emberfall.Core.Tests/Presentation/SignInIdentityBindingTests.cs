using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
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
            RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation",
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
