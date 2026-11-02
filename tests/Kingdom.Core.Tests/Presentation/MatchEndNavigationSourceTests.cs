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

        // Launch item 6: one tap from the result screen back into the queue.
        [Test]
        public void Play_Again_Requeues_The_Same_Deck_As_A_Result_View_Queue()
        {
            string sequenceSource = StripComments(PresentationSource("MatchEndSequence.cs"));
            string selectionSource = StripComments(PresentationSource("CharacterRosterController.cs"));
            string bootstrapSource = StripComments(PresentationSource("NetworkedGameBootstrap.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(sequenceSource, Does.Match(@"MatchDeckSelection\.Rematch\("),
                    "Play Again must go through the one rematch door.");
                Assert.That(selectionSource, Does.Match(
                        @"void Rematch\(string deckId\)\s*\{\s*Select\(deckId\);\s*" +
                        @"FirstSessionFunnel\.Queue\(FunnelQueueSource\.ResultView\);\s*" +
                        @"ScreenNav\.Go\(ScreenNav\.BoardNetworked\);"),
                    "A rematch arms the deck, records a result_view queue for the funnel, and loads the board.");
                Assert.That(bootstrapSource, Does.Contain("if (challenge) _rematchDeckId = \"\";"),
                    "A friend challenge must not offer Play Again: queueing would seat a stranger.");
                Assert.That(bootstrapSource, Does.Contain("rematchDeckId: _rematchDeckId"),
                    "The result screen needs the deck this match was queued with.");
            });
        }

        // Play Again reloads BoardNetworked from BoardNetworked. A music hold keyed on the scene's
        // name alone never sees that as a change, and the whole rematch plays silent.
        [Test]
        public void Music_Hold_Ends_On_Any_Scene_Load_So_A_Rematch_Is_Not_Silent()
        {
            string source = StripComments(PresentationSource("MusicDirector.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("SceneManager.sceneLoaded += OnSceneLoaded;"));
                Assert.That(source, Does.Match(
                    @"void OnSceneLoaded\(Scene scene, LoadSceneMode mode\)\s*\{[^}]*_heldScene = null;"));
            });
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//.*", " ");
        }
    }
}
