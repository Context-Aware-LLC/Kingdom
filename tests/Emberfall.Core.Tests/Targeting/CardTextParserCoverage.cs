using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// How much of the shipped corpus can have its rules DERIVED from its text — and proof that
    /// everything derived is exactly right.
    ///
    /// <para><b>Sam ruled on 2026-08-20 that rules are not authored</b>: "any form of rules that are
    /// required are derived from card text by the engine or a pre-processor". This fixture is the
    /// oracle for that work, and it exists BEFORE the parser is finished on purpose. Coverage is a
    /// number printed from a run, never a claim in a commit message, because "the parser handles
    /// most cards" is exactly the sentence this repo has learned not to trust.</para>
    ///
    /// <para><b>The gate is a ROUND TRIP, which is what makes a partial parser safe.</b> Every card
    /// the parser accepts is re-rendered through <c>CardTextGenerator</c> and required to produce the
    /// authored sentence byte for byte. So the two assertions below are asymmetric on purpose:
    /// exactness is absolute and non-negotiable, while coverage is a ratchet that starts low. A
    /// parser that guesses would produce a card quietly doing something other than what it says;
    /// refusing is always allowed, being wrong never is.</para>
    ///
    /// <para><b>Why the round trip is a real oracle here and not circular.</b> The rules it compares
    /// against are the AUTHORED ones, and C26 independently holds those to the same renderer over
    /// all 261 rows. So "parsed text re-renders to the authored text" plus "authored rules render to
    /// the authored text" pins the parsed rules to the authored rules through a function already
    /// proven total and exact on this corpus.</para>
    ///
    /// <para><b>Its one blind spot, stated because a green run otherwise implies more than it
    /// shows.</b> Where two different specs render identically the round trip cannot tell them
    /// apart — a unique selector reads the same under <c>all</c> and under <c>player: 1</c>, since
    /// "all" of one thing is still it. The parser resolves that by preferring <c>all</c>, which is
    /// what all 26 such rows author, and no assertion here can catch it being wrong.</para>
    /// </summary>
    [TestFixture]
    public sealed class CardTextParserCoverage
    {
        // RETIRED 2026-08-25: `private const int MinimumCardsDerived`.
        //
        // It was a count floor — raise it as the parser improves, never lower it — and it was the
        // right guard while coverage was PARTIAL. It stopped being right the moment coverage went
        // total, because the number it guards is a COUNT OF CARDS and the corpus is content: the
        // Clockwright draw pass turned two cards into vanillas, rule-carrying cards fell 201 -> 198,
        // and the floor failed a run in which the parser had not regressed by one card.
        //
        // The invariant below is what the floor was approximating and is strictly stronger: the
        // parser refuses NOTHING. A real regression still fails it — a card the parser can no longer
        // read lands in `refused` — while a card losing its rules to a balance edit changes the
        // denominator and nothing else. No number to keep in sync, and nothing that can be quietly
        // lowered to make a run pass, which is the failure the old comment was written to prevent.
        //
        // RETIREMENT TRIGGER for the replacement: the first card the parser is ALLOWED to refuse.
        // If a shape is ever deliberately left unparsable, this assertion is what says so out loud —
        // do not weaken it to an allowlist without recording why here.

        [Test]
        public void Every_card_the_parser_accepts_round_trips_exactly()
        {
            List<CardDefinition> cards = ShippedRuleCards();
            var names = new CardNameIndex(CorpusBehaviourSweep.ShippedCards());
            var derived = new List<int>();
            var wrong = new List<int>();
            var refused = new List<(int Card, string Reason)>();

            foreach (CardDefinition card in cards)
            {
                bool ok;
                IReadOnlyList<RuleDefinition> rules;
                string reason;
                try
                {
                    ok = CardTextParser.TryParse(card, names, out rules, out reason);
                }
                catch (Exception e)
                {
                    // A throw is a refusal that forgot to say so — reported as a refusal rather than
                    // allowed to fail the run, but never counted as coverage.
                    refused.Add((card.Id, $"<{e.GetType().Name}> {e.Message}"));
                    continue;
                }

                if (!ok) { refused.Add((card.Id, reason)); continue; }

                string rerendered;
                try
                {
                    rerendered = CardTextGenerator.Generate(Rebuilt(card, rules), names);
                }
                catch (Exception e)
                {
                    wrong.Add(FixtureCard.Id($"{card.Id}: parsed rules do not render — <{e.GetType().Name}> {e.Message}"));
                    continue;
                }

                if (string.Equals(rerendered, card.Text, StringComparison.Ordinal)) derived.Add(card.Id);
                else wrong.Add(FixtureCard.Id($"{card.Id}\n      authored  \"{card.Text}\"\n      derived   \"{rerendered}\""));
            }

            TestContext.Out.WriteLine($"card-name index: colliding printed names = {names.CollidingNames()}");
            Report(cards.Count, derived, wrong, refused);

            Assert.That(wrong, Is.Empty,
                "a card was DERIVED WRONG. Refusing to parse is always acceptable; deriving rules " +
                "that do not say what the card says is the one outcome this work must never ship.");
            Assert.That(refused, Is.Empty,
                $"the parser refused {refused.Count} of {cards.Count} rule-carrying cards. Coverage " +
                "has been total since 2026-08-25, so a refusal is a REGRESSION, not a backlog item — " +
                "either a card was authored in a shape the grammar does not cover, or a change to " +
                "the renderer moved a sentence the parser was reading. The reasons are printed above.");
        }

        /// <summary>
        /// The parser must not invent rules for a card it cannot read. A refusal returns no rules,
        /// so a caller that ignores the return value gets an empty list rather than a wrong one.
        /// </summary>
        [Test]
        public void A_refused_card_yields_no_rules()
        {
            foreach (CardDefinition card in ShippedRuleCards())
            {
                var names = new CardNameIndex(CorpusBehaviourSweep.ShippedCards());
                if (CardTextParser.TryParse(card, names, out IReadOnlyList<RuleDefinition> rules, out _)) continue;
                Assert.That(rules, Is.Empty, $"{card.Id}: refused but handed back {rules.Count} rules.");
            }
        }

        private static void Report(int total, List<int> derived, List<int> wrong,
            List<(int Card, string Reason)> refused)
        {
            var report = new List<string>
            {
                $"card-text parser — {derived.Count}/{total} rule-carrying cards derived and round-tripped",
                $"  derived exactly : {derived.Count}",
                $"  derived WRONG   : {wrong.Count}",
                $"  refused         : {refused.Count}",
                "",
            };

            if (wrong.Count > 0)
            {
                report.Add("derived wrong:");
                report.AddRange(wrong.Select(w => "  " + w));
                report.Add("");
            }

            report.Add("refusals by reason — this is the work queue, most common first:");
            foreach (IGrouping<string, (int Card, string Reason)> group in refused
                         .GroupBy(r => Generalise(r.Reason))
                         .OrderByDescending(g => g.Count())
                         .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                report.Add($"  {group.Count(),4}  {group.Key}");
                report.Add($"        e.g. {group.First().Card} — {group.First().Reason}");
            }

            string text = string.Join("\n", report);
            File.WriteAllText(Path.Combine(RepoPaths.RepoRoot, "card-text-parser-coverage.txt"), text);
            // Every refusal verbatim, one per line, ordered by kind then card. The grouped report
            // above is the SHAPE of the work; this file is the work itself, and it is what you read
            // when picking the next frame to add.
            File.WriteAllLines(Path.Combine(RepoPaths.RepoRoot, "card-text-parser-refusals.txt"),
                refused.OrderBy(r => Generalise(r.Reason), StringComparer.Ordinal)
                    .ThenBy(r => r.Card)
                    .Select(r => Generalise(r.Reason) + "  |  " + r.Card + "  |  " + r.Reason));

            TestContext.Out.WriteLine(text);
        }

        /// <summary>Collapses a refusal to its kind, so the report is a work queue rather than a list
        /// of every distinct sentence the parser has not learned.</summary>
        private static string Generalise(string reason)
        {
            if (reason.StartsWith("no frame matches", StringComparison.Ordinal)) return "no frame matches the verb";
            if (reason.StartsWith("no target spec", StringComparison.Ordinal)) return "target phrase not recognised";
            if (reason.StartsWith("a folded damage run", StringComparison.Ordinal))
                return "folded damage run — two readings the sentence cannot separate";
            return reason;
        }

        private static CardDefinition Rebuilt(CardDefinition card, IReadOnlyList<RuleDefinition> rules)
            => new CardDefinition(
                id: card.Id, handle: card.Handle, name: card.Name, type: card.Type, cost: card.Cost,
                attack: card.Attack, health: card.Health, durability: card.Durability,
                @class: card.Class, rarity: card.Rarity,
                keywords: card.Keywords, rules: rules,
                flavorText: card.FlavorText, artId: card.ArtId, setId: card.SetId,
                collectible: card.Collectible, tribe: card.Tribe, factionId: card.FactionId,
                text: card.Text);

        private static List<CardDefinition> ShippedRuleCards()
            => CorpusBehaviourSweep.ShippedCards().Where(c => c.Rules.Count > 0).ToList();
    }
}
