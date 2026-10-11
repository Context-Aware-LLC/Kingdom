using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// Both platforms must declare the <c>emberfall://auth</c> URL scheme, or every sign-in path
    /// that leaves the app is dead.
    ///
    /// <para><b>This has already shipped broken once, and nothing noticed.</b> On 2026-09-01 commit
    /// <c>20341326</c> ("possible uss system and collection screen edits") deleted
    /// <c>Assets/Plugins/</c> entirely, through git's RENAME DETECTION — the diff reads
    /// <c>Assets/{Plugins.meta =&gt;}</c>, because a new folder's <c>.meta</c> is
    /// tiny near-identical YAML and git paired it with the old one. Nothing in that commit's message
    /// or file list reads as a deletion.</para>
    ///
    /// <para><b>What it cost:</b> the APK built on 2026-09-02 had zero <c>android:scheme</c> and zero
    /// <c>BROWSABLE</c> entries in its packaged manifest. Google sign-in opened the consent page and
    /// then spun for ever, because the redirect to <c>emberfall://auth?code=…</c> had nothing on the
    /// device to land on. The emailed six-digit LINK was dead for the same reason; only the typed
    /// code still worked, which is why the flow looked mostly fine.</para>
    ///
    /// <para><b>Why a source test.</b> The failure has no exception and no log line — the app simply
    /// waits for a redirect that can never arrive, which is indistinguishable from a slow network.
    /// It cannot be caught by anything that runs inside the game, only by asserting the registration
    /// exists at all. Cheap, and it is the deletion that is the risk rather than an edit.</para>
    ///
    /// <para><b>This does NOT prove the scheme reached the APK.</b> A custom main manifest is a BASE
    /// Unity copies and then overwrites every attribute it owns from PlayerSettings; only additive
    /// elements survive, and an intent-filter is additive. The end-to-end check is a built manifest:
    /// <c>grep -o 'android:scheme="[^"]*"'
    /// Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/src/main/AndroidManifest.xml</c>.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class DeepLinkRegistrationTests
    {
        private static string AndroidManifest() => Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Plugins", "Android",
            "AndroidManifest.xml");

        private static string IosPlist() => Path.Combine(
            RepoPaths.RepoRoot, "unity", "Assets", "Plugins", "iOS", "Info.plist");

        [Test]
        public void The_android_manifest_exists_and_declares_the_emberfall_scheme()
        {
            Assert.That(File.Exists(AndroidManifest()), Is.True,
                "Assets/Plugins/Android/AndroidManifest.xml is missing. Without it the app registers "
                + "no URL scheme, so Google sign-in and the emailed sign-in LINK both hang for ever "
                + "with nothing logged. It has been deleted once already, as collateral of a folder "
                + "rename in an unrelated commit.");

            string manifest = File.ReadAllText(AndroidManifest());

            Assert.That(manifest, Does.Contain("android:scheme=\"emberfall\""),
                "The emberfall:// scheme is not declared. The OAuth and mail-link redirects have "
                + "nowhere to land.");
            Assert.That(manifest, Does.Contain("android:host=\"auth\""),
                "The scheme is declared without the auth host, so emberfall://auth does not match.");
            Assert.That(manifest, Does.Contain("android.intent.category.BROWSABLE"),
                "Without BROWSABLE the browser will not hand the redirect back to the app at all.");
        }

        [Test]
        public void The_ios_plist_exists_and_declares_the_emberfall_scheme()
        {
            Assert.That(File.Exists(IosPlist()), Is.True,
                "Assets/Plugins/iOS/Info.plist is missing. It was deleted alongside the Android "
                + "manifest by the same folder rename.");

            string plist = File.ReadAllText(IosPlist());

            Assert.That(plist, Does.Contain("CFBundleURLSchemes"),
                "No URL scheme block, so iOS registers nothing for the redirect to return through.");
            Assert.That(plist, Does.Contain("<string>emberfall</string>"),
                "The emberfall scheme is not in CFBundleURLSchemes.");
        }

        /// <summary>
        /// The client's configured redirect and the platform registration have to agree, and they are
        /// written in two files that nothing else joins.
        /// </summary>
        [Test]
        public void The_configured_redirect_url_matches_what_the_platforms_register()
        {
            string settings = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Session",
                "SupabaseAuthSettings.cs"));

            Assert.That(settings, Does.Contain("emberfall://auth"),
                "SupabaseAuthSettings' default redirect no longer matches the scheme and host the "
                + "manifests register. Supabase would redirect somewhere the device cannot open.");
        }
    }
}
