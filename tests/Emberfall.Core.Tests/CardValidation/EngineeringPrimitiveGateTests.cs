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
    /// explicit-choice (<c>ANY_*</c>) selector. A Deathrattle fires with no player choosing — and
    /// 6 of 64 balance-matrix cells crashed, every matchup whose generated pool drew the row.</para>
    ///
    /// <para>Two guards had failed. C14 checked the selector without the trigger, so the row
    /// validated clean. And the flag both error messages told authors to reach for was inert:
    /// parsed, validated, reported, with no consumer — every pool filtered on <c>Collectible</c>
    /// alone, so taking that advice bought a green validator and an identical crash.</para>
    ///
    /// <para><b>Deliberately does not pin any specific card's id or printed text.</b> The set is
    /// early and volatile (Sam, 2026-08-03) — a test that hard-codes one row's exact wording
    /// breaks on the next content pass for a reason that has nothing to do with this defect.
    /// These assert the PROPERTY over whatever the live set currently holds, which is what
    /// should still be true after the set changes.</para>
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class EngineeringPrimitiveGateTests
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
                foreach (var fx in def.Effects)
                {
                    if (fx.Target == null) continue;            // DefaultTargets handles it
                    if (PlayerTargeted.Contains(fx.Trigger)) continue;
                    if (!Targeting.IsPlayerChosen(fx.Target)) continue;
                    offenders.Add($"{def.Id}: {fx.Trigger} {fx.Action} -> '{fx.Selector}'");
                }
            }

            Assert.That(offenders, Is.Empty,
                "Pool-eligible rows whose fired trigger declares a selector the interpreter cannot " +
                "auto-resolve — each is a live crash the moment it fires: " + string.Join("; ", offenders));
        }

        // ── C14b, the rule that let it through ───────────────────────────────────

        // A throwaway fixture, not a live card — these tests exercise the RULE, not any specific
        // row, so they stay valid no matter how the live set changes.
        private static CardDefinition FiredAnySelectorCard(Trigger trigger, bool gated) => new CardDefinition(
            id: "test_fired_any_selector", name: "Test", type: CardType.MINION, cost: 1,
            attack: 1, health: 1, durability: null,
            @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
            keywords: System.Array.Empty<Keyword>(),
            effects: new[]
            {
                new EffectDef(trigger, Action.BUFF, amount: 1, amount2: 1,
                    tokenId: null, grantedKeyword: null, vfxOverride: null, selector: "ANY_FRIENDLY_MINION"),
            },
            cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
            collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: gated);

        private static ValidationReport Validate(CardDefinition def)
            => new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));

        [Test]
        public void C14b_Rejects_An_Explicit_Choice_Selector_On_A_Fired_Trigger()
        {
            var report = Validate(FiredAnySelectorCard(Trigger.ON_DEATH, gated: false));

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
            var report = Validate(FiredAnySelectorCard(trigger, gated: false));
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
            var report = Validate(FiredAnySelectorCard(trigger, gated: false));
            Assert.That(report.Issues.Any(i => i.Code == "C14B-SELECTOR-NEEDS-PLAYER-TARGET"), Is.False,
                "C14b must not fire where the player supplies the target.");
            Assert.That(report.Ok, Is.True);
        }

        [Test]
        public void C14b_Is_Exempted_By_The_Primitive_Gate()
        {
            // The exemption is only honest because the gate now holds the row out of every pool.
            var report = Validate(FiredAnySelectorCard(Trigger.ON_DEATH, gated: true));
            Assert.That(report.Ok, Is.True,
                "requiresEngineeringPrimitive is the documented way to hold a row for a primitive.");
            Assert.That(report.RequiresEngineeringPrimitive, Is.True);
        }
    }
}
