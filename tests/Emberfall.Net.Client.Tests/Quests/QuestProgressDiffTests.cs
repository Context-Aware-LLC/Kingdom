using System.Collections.Generic;
using System.Linq;
using Emberfall.Net.Client.Quests;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Quests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class QuestProgressDiffTests
    {
        private static QuestEntry Quest(string id, string day, int progress, int goal = 3)
            => new QuestEntry(id, id, goal, progress, 60, day, progress >= goal);

        private static QuestSnapshot Known(params QuestEntry[] quests)
            => QuestSnapshot.Known(new List<QuestEntry>(quests));

        [Test]
        public void Reports_only_the_quests_that_moved_with_where_they_stood_before()
        {
            QuestSnapshot before = Known(
                Quest("play-three", "2026-10-03", 1),
                Quest("win-two", "2026-10-03", 1, goal: 2),
                Quest("play-three", "2026-10-02", 2));
            QuestSnapshot after = Known(
                Quest("play-three", "2026-10-03", 2),
                Quest("win-two", "2026-10-03", 2, goal: 2),
                Quest("play-three", "2026-10-02", 2));

            IReadOnlyList<QuestAdvance> advanced = QuestProgressDiff.Advanced(before, after);

            Assert.Multiple(() =>
            {
                Assert.That(advanced.Select(a => a.Quest.Id), Is.EqualTo(new[] { "play-three", "win-two" }));
                Assert.That(advanced.Select(a => a.Quest.Day), Is.All.EqualTo("2026-10-03"),
                    "Yesterday's row shares an id with today's and did not move.");
                Assert.That(advanced[0].PreviousProgress, Is.EqualTo(1));
                Assert.That(advanced[0].CompletedNow, Is.False);
                Assert.That(advanced[1].CompletedNow, Is.True);
            });
        }

        [Test]
        public void A_quest_missing_from_the_baseline_started_from_zero()
        {
            IReadOnlyList<QuestAdvance> advanced = QuestProgressDiff.Advanced(
                Known(),
                Known(Quest("play-three", "2026-10-04", 1), Quest("win-two", "2026-10-04", 0)));

            Assert.That(advanced.Select(a => (a.Quest.Id, a.PreviousProgress)),
                Is.EqualTo(new[] { ("play-three", 0) }));
        }

        [Test]
        public void An_unknown_baseline_reports_nothing_rather_than_everything()
        {
            QuestSnapshot after = Known(Quest("play-three", "2026-10-03", 2));

            Assert.Multiple(() =>
            {
                Assert.That(QuestProgressDiff.Advanced(
                    QuestSnapshot.Failed(QuestFetchStatus.Offline, ""), after), Is.Empty);
                Assert.That(QuestProgressDiff.Advanced(null, after), Is.Empty);
                Assert.That(QuestProgressDiff.Advanced(
                    after, QuestSnapshot.Failed(QuestFetchStatus.Unavailable, "")), Is.Empty);
            });
        }
    }
}
