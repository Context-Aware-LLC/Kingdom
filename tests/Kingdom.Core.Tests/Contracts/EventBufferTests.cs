using System;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Contracts
{
    [TestFixture]
    public sealed class EventBufferTests
    {
        [Test]
        public void Emit_Validates_Before_Advancing_The_Global_Sequence()
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            var buffer = new EventBuffer(state);
            ulong before = state.NextEventSequence;

            Assert.That(
                () => buffer.Emit((GameEventKind)int.MaxValue),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.Multiple(() =>
            {
                Assert.That(state.NextEventSequence, Is.EqualTo(before));
                Assert.That(buffer.Events, Is.Empty);
                Assert.That(buffer.Visibility, Is.Empty);
            });
        }

        [Test]
        public void Emit_Rejects_Data_That_Is_Not_Valid_For_The_Semantic_Kind()
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            var buffer = new EventBuffer(state);
            ulong before = state.NextEventSequence;

            Assert.Multiple(() =>
            {
                Assert.That(
                    () => buffer.Emit(GameEventKind.CardPlayed, amount: 3),
                    Throws.TypeOf<ArgumentException>());
                Assert.That(
                    () => buffer.Emit(GameEventKind.CardPlayed, flags: EventFlags.Lethal),
                    Throws.TypeOf<ArgumentException>());
                Assert.That(
                    () => buffer.Emit(GameEventKind.CardPlayed, cueOverride: (PresentationCue)int.MaxValue),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            });

            Assert.Multiple(() =>
            {
                Assert.That(state.NextEventSequence, Is.EqualTo(before));
                Assert.That(buffer.Events, Is.Empty);
                Assert.That(buffer.Visibility, Is.Empty);
            });
        }

        [Test]
        public void Emit_Assigns_Sequence_And_Captures_Parallel_Visibility()
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            var buffer = new EventBuffer(state);
            ulong before = state.NextEventSequence;

            buffer.Emit(
                GameEventKind.DamageApplied,
                source: state.Players[0].Hero.Id,
                target: state.Players[1].Hero.Id,
                amount: 4,
                flags: EventFlags.Lethal,
                cueOverride: PresentationCue.Frost);
            buffer.Emit(GameEventKind.TurnEnded, source: state.Players[0].Hero.Id);

            Assert.Multiple(() =>
            {
                Assert.That(buffer.Events, Has.Count.EqualTo(2));
                Assert.That(buffer.Visibility, Has.Count.EqualTo(2));
                Assert.That(buffer.Events[0].Sequence, Is.EqualTo(before));
                Assert.That(buffer.Events[1].Sequence, Is.EqualTo(before + 1));
                Assert.That(state.NextEventSequence, Is.EqualTo(before + 2));
            });
        }
    }
}
