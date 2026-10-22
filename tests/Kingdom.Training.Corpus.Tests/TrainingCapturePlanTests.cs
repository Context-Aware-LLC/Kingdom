using System;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Tools.NetSoak;
using NUnit.Framework;

namespace Kingdom.Training.Corpus.Tests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class TrainingCapturePlanTests
    {
        [Test]
        public void Plan_canonicalizes_pairs_and_alternates_seats_with_unique_stable_ids()
        {
            TrainingCapturePlan plan = TrainingCapturePlan.Create(4, new[]
            {
                CardClass.TIDECALLER,
                CardClass.EMBERWARD,
                CardClass.GRAVEKEEP,
            });
            TrainingCapturePlan reordered = TrainingCapturePlan.Create(4, new[]
            {
                CardClass.GRAVEKEEP,
                CardClass.EMBERWARD,
                CardClass.TIDECALLER,
            });

            Assert.That(plan.Pairings, Has.Count.EqualTo(3));
            Assert.That(plan.Games, Has.Count.EqualTo(12));
            Assert.Multiple(() =>
            {
                Assert.That(plan.Pairings[0].FirstClass, Is.EqualTo(CardClass.EMBERWARD));
                Assert.That(plan.Pairings[0].SecondClass, Is.EqualTo(CardClass.TIDECALLER));
                Assert.That(plan.Games[0].P0Class, Is.EqualTo(CardClass.EMBERWARD));
                Assert.That(plan.Games[1].P0Class, Is.EqualTo(CardClass.TIDECALLER));
                Assert.That(plan.Games[0].MatchId,
                    Is.EqualTo("training-emberward-vs-tidecaller-000000"));
                Assert.That(plan.Games[4].MatchId,
                    Is.EqualTo("training-emberward-vs-gravekeep-000000"));
                Assert.That(plan.Games[0].Seed, Is.Not.EqualTo(plan.Games[4].Seed));
                Assert.That(reordered.Games.Select(g => g.MatchId),
                    Is.EqualTo(plan.Games.Select(g => g.MatchId)));
                Assert.That(reordered.Games.Select(g => g.Seed),
                    Is.EqualTo(plan.Games.Select(g => g.Seed)));
                Assert.That(TrainingCapturePlan.Create(
                    2, TrainingCapturePlan.AllPlayableClasses()).Pairings,
                    Has.Count.EqualTo(28));
            });
            Assert.That(plan.Games.Select(g => g.MatchId).Distinct().Count(),
                Is.EqualTo(plan.Games.Count));
        }

        [TestCase("0")]
        [TestCase("-2")]
        [TestCase("not-a-number")]
        public async Task Matrix_command_rejects_invalid_match_counts(string value)
        {
            int exit = await Kingdom.Tools.NetSoak.Program.Main(new[]
            {
                "dataset-matrix", value, "unused-output", "--all-classes",
            });

            Assert.That(exit, Is.EqualTo(2));
        }

        [Test]
        public async Task Matrix_command_rejects_malformed_or_duplicate_options()
        {
            string[][] invalid =
            {
                new[] { "dataset-matrix", "2", "--all-classes" },
                new[] { "dataset-matrix", "2", "out", "--unknown" },
                new[] { "dataset-matrix", "2", "out", "--classes", "EMBERWARD,,TIDECALLER" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--all-classes" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--deck-dir" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--deck-dir", "" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--game-seed-offset" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--game-seed-offset", "-1" },
                new[] { "dataset-matrix", "2", "out", "--all-classes", "--game-seed-offset", "1",
                    "--game-seed-offset", "2" },
                new[] { "dataset-matrix", "2", "out", "--classes", "EMBERWARD,TIDECALLER",
                    "--classes", "GRAVEKEEP,VERDANT" },
            };

            foreach (string[] arguments in invalid)
                Assert.That(await Kingdom.Tools.NetSoak.Program.Main(arguments), Is.EqualTo(2));
        }

        [Test]
        public void Plan_rejects_invalid_or_duplicate_class_sets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(() => TrainingCapturePlan.Create(0, new[]
                {
                    CardClass.EMBERWARD, CardClass.TIDECALLER,
                }), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => TrainingCapturePlan.Create(3, new[]
                {
                    CardClass.EMBERWARD, CardClass.TIDECALLER,
                }), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => TrainingCapturePlan.Create(2, new[]
                {
                    CardClass.EMBERWARD, CardClass.EMBERWARD,
                }), Throws.TypeOf<ArgumentException>());
                Assert.That(() => TrainingCapturePlan.Create(2, new[]
                {
                    CardClass.NEUTRAL, CardClass.EMBERWARD,
                }), Throws.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void Plan_can_offset_game_seeds_without_changing_the_balanced_schedule()
        {
            TrainingCapturePlan baseline = TrainingCapturePlan.Create(4, new[]
            {
                CardClass.EMBERWARD,
                CardClass.TIDECALLER,
                CardClass.GRAVEKEEP,
            });
            TrainingCapturePlan holdout = TrainingCapturePlan.Create(4, new[]
            {
                CardClass.EMBERWARD,
                CardClass.TIDECALLER,
                CardClass.GRAVEKEEP,
            }, gameSeedOffset: 1_000_000);

            Assert.Multiple(() =>
            {
                Assert.That(holdout.Pairings, Is.EqualTo(baseline.Pairings));
                Assert.That(holdout.Games.Select(game => game.MatchId),
                    Is.EqualTo(baseline.Games.Select(game => game.MatchId)));
                Assert.That(holdout.Games.Select(game => game.P0Class),
                    Is.EqualTo(baseline.Games.Select(game => game.P0Class)));
                Assert.That(holdout.Games.Select(game => game.Seed),
                    Is.EqualTo(baseline.Games.Select(game => game.Seed + 1_000_000)));
                Assert.That(holdout.Games.Select(game => game.Seed)
                    .Intersect(baseline.Games.Select(game => game.Seed)), Is.Empty);
            });
        }

        [Test]
        public void Only_keeps_the_named_slots_unchanged_in_schedule_order_and_refuses_a_stranger()
        {
            TrainingCapturePlan plan = TrainingCapturePlan.Create(4, new[]
            {
                CardClass.EMBERWARD,
                CardClass.TIDECALLER,
                CardClass.GRAVEKEEP,
            }, gameSeedOffset: 200_000_000);

            var only = plan.Only(new[] { plan.Games[9].MatchId, plan.Games[2].MatchId, plan.Games[9].MatchId });

            Assert.That(only, Is.EqualTo(new[] { plan.Games[2], plan.Games[9] }));
            Assert.Throws<ArgumentException>(
                () => plan.Only(new[] { plan.Games[0].MatchId, "training-emberward-vs-tidecaller-000099" }));
        }
    }
}
