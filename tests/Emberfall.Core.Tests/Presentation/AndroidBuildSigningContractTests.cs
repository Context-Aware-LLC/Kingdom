using System.IO;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class AndroidBuildSigningContractTests
    {
        [Test]
        public void Android_build_uses_unitys_default_debug_signing()
        {
            string buildSource = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot,
                "unity", "Assets", "Scripts", "Editor", "EmberfallBuild.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(buildSource, Does.Contain("PlayerSettings.Android.useCustomKeystore = false;"),
                    "Every Android build must clear the sticky custom-keystore PlayerSetting.");
                Assert.That(buildSource, Does.Not.Contain("PlayerSettings.Android.keystoreName"),
                    "The build must not install a repository keystore path.");
                Assert.That(buildSource, Does.Not.Contain("PlayerSettings.Android.keystorePass"),
                    "The build must not install custom signing credentials.");
                Assert.That(buildSource, Does.Not.Contain("PlayerSettings.Android.keyaliasName"),
                    "The build must not select a custom signing alias.");
                Assert.That(buildSource, Does.Not.Contain("PlayerSettings.Android.keyaliasPass"),
                    "The build must not install custom alias credentials.");
            });
        }
    }
}
