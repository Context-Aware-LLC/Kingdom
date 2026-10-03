using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class AndroidBuildSigningContractTests
    {
        private static string BuildSource => File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot,
            "unity", "Assets", "Scripts", "Editor", "EmberfallBuild.cs"));

        /// <summary>Every right-hand side the build assigns to one <c>PlayerSettings.Android</c> field.</summary>
        private static string[] Assigned(string field)
            => Regex.Matches(BuildSource, @"PlayerSettings\.Android\." + field + @"\s*=\s*([^;]+);")
                    .Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToArray();

        [Test]
        public void Android_build_defaults_to_unitys_debug_signing()
        {
            Assert.That(BuildSource, Does.Contain("PlayerSettings.Android.useCustomKeystore = false;"),
                "Every Android build must clear the sticky custom-keystore PlayerSetting before choosing a signer.");
        }

        /// <summary>
        /// The Play release path signs with the upload key, so the build does write the signing
        /// fields — but only from the environment, and only until <c>finally</c> puts them back.
        /// What must never appear is a literal: Unity saves <c>keystoreName</c> into ProjectSettings,
        /// and a committed path or password is a build that fails, or leaks, everywhere else.
        /// </summary>
        [Test]
        public void Android_build_takes_custom_signing_only_from_the_environment_and_restores_it()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Assigned("useCustomKeystore"),
                    Is.EquivalentTo(new[] { "false", "true", "originalCustomKeystore" }),
                    "The custom-keystore flag must be restored to what the Keystore Manager had.");
                Assert.That(Assigned("keystoreName"),
                    Is.EquivalentTo(new[] { "play.KeystorePath", "originalKeystore" }),
                    "The build must not install a repository keystore path.");
                Assert.That(Assigned("keyaliasName"),
                    Is.EquivalentTo(new[] { "play.Alias", "originalAlias" }),
                    "The build must not select a hard-coded signing alias.");
                Assert.That(Assigned("keystorePass"),
                    Is.EquivalentTo(new[] { "play.Password", "\"\"" }),
                    "The keystore password must come from the environment and be cleared afterwards.");
                Assert.That(Assigned("keyaliasPass"),
                    Is.EquivalentTo(new[] { "play.Password", "\"\"" }),
                    "The alias password must come from the environment and be cleared afterwards.");
            });
        }
    }
}
