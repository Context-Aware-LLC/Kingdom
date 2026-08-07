using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardValidation
{
    /// <summary>
    /// EF-032a — C14b keeps the pool free of fired effects that still require a player choice.
    ///
    /// <para>The defect these tests exist for: a card declared an <c>ON_DEATH</c> effect on an
    /// explicit-choice (<c>ANY_*</c>) selector. A Toll fires with no player choosing — and
    /// 6 of 64 balance-matrix cells crashed, every matchup whose generated pool drew the row.</para>
    ///
    /// <para>Two guards had failed. C14 checked the selector without the trigger, so the row
    /// validated clean. And the flag both error messages told authors to reach for was inert:
    /// parsed, validated, reported, with no consumer — every pool filtered on <c>Collectible</c>
    /// alone, so taking that advice bought a green validator and an identical crash.</para>
    ///
    /// <para><b>Renamed from <c>EngineeringPrimitiveGateTests</c> in NOR-224.</b> The quarantine
    /// flag that named the old fixture is gone; C14b, the rule it exempted, is permanent and is
    /// what this fixture was always really testing. There is no exemption now — a row the engine
    /// cannot resolve is rejected, and <c>chosenBy: RANDOM</c> is the only way to author an
    /// <c>ANY_*</c> selector on a fired trigger.</para>
    ///
    /// <para><b>Deliberately does not pin any specific card's id or printed text.</b> The set is
    /// early and volatile (Sam, 2026-08-03) — a test that hard-codes one row's exact wording
    /// breaks on the next content pass for a reason that has nothing to do with this defect.
    /// These assert the PROPERTY over whatever the live set currently holds, which is what
    /// should still be true after the set changes.</para>
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class FiredTriggerSelectorTests
    {
        private static readonly HashSet<Trigger> PlayerTargeted = new HashSet<Trigger>
        {
            Trigger.ON_PLAY, Trigger.ON_CAST,
        };

        private static readonly string[] SetFiles = { "M1.cards.json", "M2.cards.json", "M3.cards.json" };

        private static IReadOnlyList<CardDefinition> LiveCards()
        {
            var all = new List<CardDefinition>();
            foreach (var file in SetFiles)
            {
                string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", file);
                Assert.That(File.Exists(path), Is.True, $"Live card set not found at {path}");
                all.AddRange(CardJson.DeserializeArray(File.ReadAllText(path)));
            }
            return all;
        }

        // ── The pool it protects ─────────────────────────────────────────────────

        [Test]
        public void No_Pool_Eligible_Row_Can_Throw_On_A_Fired_Trigger()
        {
            // The invariant the whole ticket is about: every row that can be DRAWN resolves its
            // targets without reaching the interpreter's `default:` throw. This is the guard that
            // survives a full re-author of the set — it names no card, it just re-checks the
            // property against whatever the set currently contains.
            var offenders = new List<string>();
            foreach (var def in LiveCards())
            {
                if (!def.PoolEligible) continue;
                foreach (var rule in def.Rules)
                {
                    TargetSpec? target = RuleSemantics.TargetOf(rule.Action);
                    if (target == null) continue;
                    Trigger trigger = RuleSemantics.TriggerOf(rule);
                    if (PlayerTargeted.Contains(trigger)) continue;
                    if (!Targeting.IsPlayerChosen(target)) continue;
                    offenders.Add($"{def.Id}: {trigger} {rule.Action.GetType().Name} -> '{target.Selector}'");
                }
            }

            Assert.That(offenders, Is.Empty,
                "Pool-eligible rows whose fired trigger declares a selector the interpreter cannot " +
                "auto-resolve — each is a live crash the moment it fires: " + string.Join("; ", offenders));
        }

        // ── C14b, the rule that let it through ───────────────────────────────────

        // A throwaway fixture, not a live card — these tests exercise the RULE, not any specific
        // row, so they stay valid no matter how the live set changes.
        private static CardDefinition FiredAnySelectorCard(Trigger trigger, TargetChosenBy? chosenBy = null)
            => new CardDefinition(
                id: "test_fired_any_selector", name: "Test",
                type: trigger == Trigger.ON_CAST ? CardType.SPELL : CardType.MINION, cost: 1,
                attack: trigger == Trigger.ON_CAST ? null : (int?)1,
                health: trigger == Trigger.ON_CAST ? null : (int?)1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(),
                rules: new[]
                {
                    TestRules.Rule("rule_1", trigger,
                        new ModifyStatsAction(1, 1, new TargetSpec("ANY_FRIENDLY_MINION", chosenBy))),
                },
                flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static ValidationReport Validate(CardDefinition def)
            => new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));

        [Test]
        public void C14b_Rejects_An_Explicit_Choice_Selector_On_A_Fired_Trigger()
        {
            var report = Validate(FiredAnySelectorCard(Trigger.ON_DEATH));

            Assert.That(report.Ok, Is.False, "An ANY_* selector on a fired trigger must not validate clean.");
            Assert.That(report.Issues.Any(i => i.Code == "C14B-SELECTOR-NEEDS-PLAYER-TARGET"
                                            && i.Severity == Severity.Error), Is.True,
                "Expected C14B; got: " + string.Join(", ", report.Issues.Select(i => i.Code)));
        }

        [TestCase(Trigger.ON_DEATH)]
        [TestCase(Trigger.ON_TURN_END)]
        [TestCase(Trigger.ON_DAMAGED)]
        [TestCase(Trigger.ON_FRIENDLY_SUMMON)]
        [TestCase(Trigger.ON_SPELL_CAST)]
        [TestCase(Trigger.ON_ATTACK)]
        public void C14b_Covers_Every_Trigger_That_Fires_Without_A_Player(Trigger trigger)
        {
            var report = Validate(FiredAnySelectorCard(trigger));
            Assert.That(report.Issues.Any(i => i.Code == "C14B-SELECTOR-NEEDS-PLAYER-TARGET"), Is.True,
                $"{trigger} passes Array.Empty for chosen targets, so an ANY_* selector throws there too.");
        }

        [TestCase(Trigger.ON_PLAY)]
        [TestCase(Trigger.ON_CAST)]
        public void C14b_Leaves_Player_Targeted_Triggers_Alone(Trigger trigger)
        {
            // ON_PLAY / ON_CAST are the only RaiseSelf call sites that carry the player's targets
            // (CommandProcessor.cs:346/354/386/454), so ANY_* is correct grammar there — and plenty
            // of shipping battlecries and spells depend on it.
            var report = Validate(FiredAnySelectorCard(trigger));
            Assert.That(report.Issues.Any(i => i.Code == "C14B-SELECTOR-NEEDS-PLAYER-TARGET"), Is.False,
                "C14b must not fire where the player supplies the target.");
            Assert.That(report.Ok, Is.True);
        }

        [Test]
        public void A_Random_Choice_Is_The_Only_Escape_On_A_Fired_Trigger()
        {
            // What replaced the quarantine. `chosenBy: RANDOM` makes the selector auto-resolvable
            // — the engine picks — so C14b has nothing to object to. This is the escape the error
            // message now names, and Memorial Wisp is the shipping row that takes it. Retiring
            // requiresEngineeringPrimitive removed the OTHER escape, which parked a broken row
            // rather than fixing it; this one produces a card that actually runs.
            var report = Validate(FiredAnySelectorCard(Trigger.ON_DEATH, TargetChosenBy.RANDOM));

            Assert.That(report.Issues.Any(i => i.Code == "C14B-SELECTOR-NEEDS-PLAYER-TARGET"), Is.False,
                "A randomly-chosen target needs no player: " + string.Join(", ", report.Issues.Select(i => i.Code)));
            Assert.That(report.Ok, Is.True);
        }
    }
}
