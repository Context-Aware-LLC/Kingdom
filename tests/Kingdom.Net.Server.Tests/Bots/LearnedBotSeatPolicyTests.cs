using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Bots;
using Kingdom.Policy;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// The fallback is the whole point of this class, so it is the thing under test.
    ///
    /// A degrade that works but says nothing is the failure mode worth guarding: every bot-fill
    /// match quietly becomes a Greedy match, the win rates look normal, and nobody learns the model
    /// has not served a decision in a week. These assert that a failure is COUNTED and ANNOUNCED,
    /// not merely survived.
    /// </summary>
    [TestFixture]
    public sealed class LearnedBotSeatPolicyTests
    {
        [Test]
        public async Task Falls_Back_To_Greedy_And_Counts_When_The_Learned_Policy_Throws()
        {
            var warnings = new List<string>();
            var fallbackIntent = new EndTurnIntent(new PlayerId("p0"));
            var policy = new LearnedBotSeatPolicy(
                new ThrowingLearnedPolicy(),
                new FixedSeatPolicy(fallbackIntent),
                warnings.Add);

            PlayerIntent? chosen = await policy.ChooseAsync(null!, new PlayerId("p0"));

            Assert.That(chosen, Is.EqualTo(fallbackIntent), "the seat must still produce a move");
            Assert.That(policy.FallbackDecisions, Is.EqualTo(1), "the fallback must be counted");
            Assert.That(warnings, Is.Not.Empty, "the first fallback must be announced");
            Assert.That(warnings[0], Does.Contain("falling back"));
        }

        [Test]
        public async Task Announces_Once_Not_Once_Per_Decision()
        {
            // A per-decision warning would bury the log and make operators filter it out, which is
            // how a loud failure becomes a silent one.
            var warnings = new List<string>();
            var policy = new LearnedBotSeatPolicy(
                new ThrowingLearnedPolicy(),
                new FixedSeatPolicy(new EndTurnIntent(new PlayerId("p0"))),
                warnings.Add);

            for (int i = 0; i < 20; i++)
                await policy.ChooseAsync(null!, new PlayerId("p0"));

            Assert.That(policy.FallbackDecisions, Is.EqualTo(20));
            Assert.That(warnings, Has.Count.EqualTo(1), "one warning, not twenty");
        }

        [Test]
        public async Task Keeps_The_Learned_Identity_After_Falling_Back()
        {
            // The identity names what this seat was CONFIGURED as. Relabelling mid-match would make
            // the degrade invisible in exactly the records used to audit it.
            var policy = new LearnedBotSeatPolicy(
                new ThrowingLearnedPolicy(),
                new FixedSeatPolicy(new EndTurnIntent(new PlayerId("p0"))),
                _ => { });

            await policy.ChooseAsync(null!, new PlayerId("p0"));

            Assert.That(policy.Identity, Is.EqualTo(ThrowingLearnedPolicy.LearnedIdentity));
            Assert.That(policy.Identity, Is.Not.EqualTo(GreedyAi.PolicyVersion));
        }

        [Test]
        public void Cancellation_Propagates_Instead_Of_Becoming_A_Greedy_Move()
        {
            // A cancelled match is shutting down. Answering it with a fallback move would inject an
            // intent nobody asked for and hide the shutdown.
            var policy = new LearnedBotSeatPolicy(
                new ThrowingLearnedPolicy(new OperationCanceledException()),
                new FixedSeatPolicy(new EndTurnIntent(new PlayerId("p0"))),
                _ => { });
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await policy.ChooseAsync(null!, new PlayerId("p0"), cancelled.Token));
            Assert.That(policy.FallbackDecisions, Is.Zero, "a shutdown is not a scorer failure");
        }

        private sealed class ThrowingLearnedPolicy : IObservedSeatPolicy
        {
            public const string LearnedIdentity = "checkpoint:deadbeef:argmax";
            private readonly Exception _failure;

            public ThrowingLearnedPolicy(Exception? failure = null)
                => _failure = failure ?? new InvalidOperationException("scorer is gone");

            public string Identity => LearnedIdentity;

            public ValueTask<PlayerIntent?> ChooseAsync(
                GameState state, PlayerId player, CancellationToken cancellationToken = default)
                => throw _failure;

            public void ObserveTransition(
                GameState before, IReadOnlyList<GameEvent> events, GameState after, PlayerId player)
            {
            }
        }

        private sealed class FixedSeatPolicy : ISeatPolicy
        {
            private readonly PlayerIntent _intent;
            public FixedSeatPolicy(PlayerIntent intent) => _intent = intent;
            public string Identity => GreedyAi.PolicyVersion;

            public ValueTask<PlayerIntent?> ChooseAsync(
                GameState state, PlayerId player, CancellationToken cancellationToken = default)
                => new ValueTask<PlayerIntent?>(_intent);
        }
    }
}
