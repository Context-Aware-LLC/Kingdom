using System.Linq;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class TablePropRulesTests
    {
        [Test]
        public void Crown_Needs_Repeated_Taps_And_Cannot_Spam_The_Herald()
        {
            var toys = new TablePropRules();
            Assert.That(toys.TapCrown(0), Is.False);
            Assert.That(toys.TapCrown(1), Is.False);
            Assert.That(toys.TapCrown(2), Is.True);
            for (int i = 0; i < 70; i++) Assert.That(toys.TapCrown(2.1 + i * 0.1), Is.False);
            Assert.That(toys.TapCrown(11), Is.False);
            Assert.That(toys.TapCrown(12), Is.False);
            Assert.That(toys.TapCrown(13), Is.True);
        }

        [Test]
        public void Isolated_Crown_Taps_Do_Not_Accumulate_A_Warning()
        {
            var toys = new TablePropRules();
            foreach (int time in new[] { 0, 5, 10, 15 }) Assert.That(toys.TapCrown(time), Is.False);
        }

        [Test]
        public void Exactly_One_Of_Twenty_Equally_Likely_Suggestion_Draws_Returns_A_Receipt()
            => Assert.That(Enumerable.Range(0, 20).Count(TablePropRules.ReturnsSuggestionReceipt), Is.EqualTo(1));

        [Test]
        public void Every_Physical_Orb_Has_A_Distinct_Playable_Note()
        {
            float[] pitches = Enumerable.Range(0, 20).Select(TablePropRules.OrbPitch).ToArray();
            Assert.That(pitches.Distinct().Count(), Is.EqualTo(20));
            Assert.That(pitches, Is.All.InRange(0.5f, 2f));
            Assert.That(pitches[9], Is.EqualTo(1f));
        }
    }
}
