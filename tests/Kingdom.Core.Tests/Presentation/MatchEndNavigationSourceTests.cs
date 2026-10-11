using System;
using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class MatchEndNavigationSourceTests
    {
        private static string PresentationSource(string fileName)
            => File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot,
                "unity",
                "Assets",
                "Scripts",
                "Presentation",
                fileName));

        [Test]
        public void Shared_Result_Banner_Returns_To_Deck_Selection_On_Real_Time()
        {
            string bannerSource = StripComments(PresentationSource("MatchEndBanner.cs"));
            string sequenceSource = StripComments(PresentationSource("MatchEndSequence.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(bannerSource, Does.Contain("root.AddComponent<MatchEndSequence>()"),
                    "Showing the shared result banner must also schedule the return.");
                Assert.That(sequenceSource, Does.Contain("new WaitForSecondsRealtime(LingerSeconds)"),
                    "The automatic return must not stall when scaled game time is paused.");
                Assert.That(sequenceSource, Does.Not.Match(@"WaitForSeconds\("),
                    "No beat of the result screen may run on scaled game time.");
                Assert.That(sequenceSource, Does.Match(@"MatchDeckSelection\.OpenPicker\(\)"),
                    "The scheduled destination must be deck selection, by the same door Find Game uses.");
            });
        }

        // GameBootstrap.cs went with the local match path (66ab8294): BoardNetworked is the only
        // shipped scene, so the networked bootstrap is the only match-end path left to check.
        [TestCase("NetworkedGameBootstrap.cs")]
        public void Every_Match_End_Path_Uses_The_Shared_Result_Banner(string bootstrapFile)
        {
            string source = StripComments(PresentationSource(bootstrapFile));

            Assert.That(source, Does.Contain("MatchEndBanner.Show("),
                $"{bootstrapFile} must enter the shared result-and-return path.");
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//.*", " ");
        }
    }
}
