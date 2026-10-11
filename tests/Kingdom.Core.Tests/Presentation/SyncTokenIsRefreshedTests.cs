using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// Every server request made on this device carries a token that was refreshed first, never the
    /// one sitting on disk.
    ///
    /// <para><b>The defect this guards has shipped, and it presented as "play mode cannot find a
    /// game."</b> A Supabase access token lives an hour; the refresh token is what survives a
    /// launch. <c>MainMenuController.ResumeThenFinishBootstrap</c> passed
    /// <c>AuthGateway.Provider.Current.AccessToken</c> — the stored one — to both gateways, so on the
    /// launch it exists to serve, a returning player's, it was usually expired. Measured
    /// 2026-09-03: <c>GET /collection</c> and <c>GET /roster</c> both answered 401 at boot, and
    /// eleven minutes later the <c>/ws</c> hello logged <c>[auth] authenticated</c> for the same
    /// identity — because connect refreshes and the boot sync did not.</para>
    ///
    /// <para><b>Why a source test.</b> The 401 has no failure of its own. Both gateways swallow every
    /// outcome into a status and take the "keep whatever this device already had" branch by design,
    /// which is correct — but it means an expired token and a healthy launch are indistinguishable
    /// from inside this screen. The consequence arrives in a different screen, minutes later, as a
    /// deck built from an unconstrained pool and a socket the gate closes with
    /// <c>deck rejected: You don't own X</c>. Nothing between those two points can assert anything.
    /// What CAN be checked cheaply is that no call site reads a token off a stored session, so that
    /// is what this checks.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER:</b> if <c>AuthTokenProvider</c> starts refreshing inside
    /// <c>Current</c> — making the stored token safe to read directly — or the gateways start taking
    /// the provider instead of a string, delete this fixture with that change. It pins a shape, and a
    /// shape that no longer exists fails for the wrong reason.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class SyncTokenIsRefreshedTests
    {
        private static readonly string[] Sources =
        {
            "MainMenuController.cs",
            "SignInGateController.cs",
            "PackVaultController.cs",
        };

        private static string Read(string fileName) => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", fileName));

        /// <summary>
        /// The same source with every line comment removed, because the rule is about what the code
        /// does and these files explain the defect in prose that names the very spelling it bans.
        ///
        /// <para>A <c>//</c> inside a string literal — a URL — truncates that line early. That can
        /// only ever hide a match, never invent one, and no token is read from a URL.</para>
        /// </summary>
        private static string Code(string fileName) =>
            Regex.Replace(Read(fileName), @"//.*$", "", RegexOptions.Multiline);

        /// <summary>
        /// No screen hands a gateway a token it read off the stored session.
        ///
        /// <para>The stored token is reachable four ways — <c>AuthGateway.Provider.Current</c>, a
        /// local <c>AuthSession</c>, <c>_auth.Current</c>, or an <c>AuthSession</c> field — and every
        /// one of them ends in <c>.AccessToken</c>. That suffix is the whole tell, so it is what this
        /// matches rather than any one of the four spellings.</para>
        /// </summary>
        [Test]
        public void No_screen_reads_a_token_off_the_stored_session()
        {
            foreach (string fileName in Sources)
            {
                MatchCollection stored = Regex.Matches(Code(fileName), @"\.AccessToken\b");

                Assert.That(stored.Count, Is.EqualTo(0),
                    $"{fileName} reads .AccessToken off a stored session. A stored access token is "
                    + "an hour old at most and usually expired on a returning player's launch; the "
                    + "route answers 401, the sync keeps whatever the device already had, and the "
                    + "failure surfaces two screens later as a deck the gate refuses. Use "
                    + "AuthGateway.TokenForConnectAsync(), which refreshes first.");
            }
        }

        /// <summary>
        /// The boot path — the one this defect shipped on — still syncs, and still does it with the
        /// refreshed token.
        ///
        /// <para>The test above is an absence, and an absence also passes on a file that stopped
        /// syncing altogether. That would be the original 2026-09-02 defect returning: a launch with
        /// a restored session that pulls nothing down at all. This is the matching presence.</para>
        /// </summary>
        [Test]
        public void The_boot_path_syncs_with_a_refreshed_token()
        {
            string menu = Code("MainMenuController.cs");

            Assert.That(menu, Does.Match(@"AuthGateway\.TokenForConnectAsync\(\)"),
                "The boot resume must ask for a refreshed token before it syncs.");
            Assert.That(menu, Does.Match(@"CollectionGateway\.SyncSnapshotAsync\("),
                "A launch with a restored session must still pull the collection and wallet down.");
            Assert.That(menu, Does.Match(@"RosterGateway\.SyncAsync\("),
                "A launch with a restored session must still pull the roster down.");
        }
    }
}
