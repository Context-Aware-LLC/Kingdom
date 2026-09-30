using System;
using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
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
        public void Shared_Result_Banner_Returns_To_MainMenu_After_Three_Real_Time_Seconds()
        {
            string bannerSource = StripComments(PresentationSource("MatchEndBanner.cs"));
            string returnSource = StripComments(PresentationSource("MatchEndMenuReturn.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(bannerSource, Does.Contain("root.AddComponent<MatchEndMenuReturn>()"),
                    "Showing the shared result banner must also schedule the menu return.");
                Assert.That(returnSource, Does.Match(@"ReturnDelaySeconds\s*=\s*3f"),
                    "The board-approved result-screen duration is three seconds.");
                Assert.That(returnSource, Does.Contain("new WaitForSecondsRealtime(ReturnDelaySeconds)"),
                    "The automatic return must not stall when scaled game time is paused.");
                Assert.That(returnSource, Does.Match(@"ScreenNav\.Go\(ScreenNav\.MainMenu\)"),
                    "The scheduled destination must be the shared MainMenu scene constant.");
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
