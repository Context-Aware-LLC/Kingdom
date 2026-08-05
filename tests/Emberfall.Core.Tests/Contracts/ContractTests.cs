using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Contracts
{
    /// <summary>
    /// L2 — asserts the Emberfall.Core.Contracts surface itself, so a refactor that silently breaks the
    /// public API fails before any consumer team is affected (dev-docs/quality/testing-and-ci.md:21).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class ContractTests
    {
        [Test]
        public void CommandResult_Reason_Is_Null_Iff_Accepted()
        {
            var ok = CommandResult.Ok(Array.Empty<GameEvent>(), 5);
            Assert.That(ok.Accepted, Is.True);
            Assert.That(ok.Reason, Is.Null);

            var rej = CommandResult.Rejected(RejectReason.NotYourTurn, 5);
            Assert.That(rej.Accepted, Is.False);
            Assert.That(rej.Reason, Is.EqualTo(RejectReason.NotYourTurn));
            Assert.That(rej.Events, Is.Empty, "A rejected command emits zero events.");
        }

        [Test]
        public void GameEventKind_Vocabulary_Is_The_Closed_Semantic_Set()
        {
            var expected = new[]
            {
                "CardPlayed", "CardSummoned", "AttackDeclared", "DamageApplied",
                "HealthRestored", "CardDestroyed", "StatsModified", "KeywordApplied",
                "CardDrawn", "CardCreatedInHand", "CardSilenced", "FrostApplied",
                "CardTransformed", "CardBounced", "ManaGained", "WeaponEquipped",
                "UnearthOffered", "TurnStarted", "TurnEnded", "HeroPowerUsed",
                "FatigueDamage", "MatchWon", "MatchEnded", "ArmorGained", "ControlChanged",
            };

            Assert.That(Enum.GetNames(typeof(GameEventKind)), Is.EqualTo(expected));
        }

        [Test]
        public void PresentationCue_Vocabulary_Is_Separate_From_Semantic_Events()
        {
            var expected = new[]
            {
                "Play", "Summon", "Attack", "Damage", "Heal", "Death", "Buff",
                "Draw", "Silence", "Frost", "Transform", "Return", "ManaGain",
                "Equip", "Unearth", "TurnStart", "TurnEnd", "HeroPower", "Fatigue",
                "GameWin", "GameOver", "ArmorGain", "ControlChange",
            };

            Assert.That(Enum.GetNames(typeof(PresentationCue)), Is.EqualTo(expected));
        }

        [Test]
        public void Every_Semantic_Event_Has_An_Explicit_Default_Presentation_Cue()
        {
            foreach (GameEventKind kind in Enum.GetValues(typeof(GameEventKind)))
                Assert.That(PresentationCueDefaults.For(kind), Is.TypeOf<PresentationCue>());

            Assert.Multiple(() =>
            {
                Assert.That(PresentationCueDefaults.For(GameEventKind.StatsModified), Is.EqualTo(PresentationCue.Buff));
                Assert.That(PresentationCueDefaults.For(GameEventKind.KeywordApplied), Is.EqualTo(PresentationCue.Buff));
                Assert.That(PresentationCueDefaults.For(GameEventKind.CardDrawn), Is.EqualTo(PresentationCue.Draw));
                Assert.That(PresentationCueDefaults.For(GameEventKind.CardCreatedInHand), Is.EqualTo(PresentationCue.Draw));
                Assert.That(PresentationCueDefaults.For(GameEventKind.ArmorGained), Is.EqualTo(PresentationCue.ArmorGain));
                Assert.That(PresentationCueDefaults.For(GameEventKind.ControlChanged), Is.EqualTo(PresentationCue.ControlChange));
            });
        }

        [Test]
        public void Legacy_Vfx_Event_And_Sink_Contracts_Are_Removed()
        {
            var assembly = typeof(GameEvent).Assembly;

            Assert.Multiple(() =>
            {
                Assert.That(assembly.GetType("Emberfall.Core.Contracts.VfxEvent"), Is.Null);
                Assert.That(assembly.GetType("Emberfall.Core.Contracts.IGameEventSink"), Is.Null);
            });
        }

        [Test]
        public void Keyword_Enum_Has_The_16_Carrier_Behaviour_Keywords()
        {
            // dev-docs/specs/card-schema.md §4 v1.3: ECHO formally cut by M2 decision 2 (signed 2026-07-12).
            // REKINDLING set (M3) adds REINFORCE. Herald and Toll are derived trigger labels,
            // leaving 16 actual carrier behaviours.
            Assert.That(Enum.GetValues(typeof(Keyword)).Length, Is.EqualTo(16));
            Assert.That(Enum.GetNames(typeof(Keyword)), Does.Not.Contain("ECHO"),
                "ECHO must not quietly return — re-adding it is a charter-level decision.");
            Assert.That(Enum.GetNames(typeof(Keyword)), Does.Not.Contain("HERALD"));
            Assert.That(Enum.GetNames(typeof(Keyword)), Does.Not.Contain("TOLL"));
            Assert.Multiple(() =>
            {
                Assert.That((int)Keyword.LIFEDRAIN, Is.EqualTo(6));
                Assert.That((int)Keyword.DOUBLESTRIKE, Is.EqualTo(7));
                Assert.That((int)Keyword.SHROUD, Is.EqualTo(8));
                Assert.That((int)Keyword.VENOM, Is.EqualTo(9));
                Assert.That((int)Keyword.ARCANE_SURGE, Is.EqualTo(10));
                Assert.That((int)Keyword.SILENCE, Is.EqualTo(11));
                Assert.That((int)Keyword.FROST, Is.EqualTo(12));
                Assert.That((int)Keyword.BACKDRAFT, Is.EqualTo(13));
                Assert.That((int)Keyword.REKINDLE, Is.EqualTo(14));
                Assert.That((int)Keyword.OVERWHELM, Is.EqualTo(15));
                Assert.That((int)Keyword.VANGUARD, Is.EqualTo(16));
                Assert.That((int)Keyword.REINFORCE, Is.EqualTo(17));
            });
        }

        [Test]
        public void Every_PlayerIntent_Subtype_Has_A_Unique_Stable_ReplayTag()
        {
            var intentTypes = typeof(PlayerIntent).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(PlayerIntent).IsAssignableFrom(t))
                .ToArray();

            Assert.That(intentTypes, Is.Not.Empty);

            var tags = new System.Collections.Generic.List<string>();
            foreach (var t in intentTypes)
            {
                var attr = t.GetCustomAttribute<ReplayTagAttribute>();
                Assert.That(attr, Is.Not.Null, $"{t.Name} is missing [ReplayTag] — fixtures serialize by tag (§6.3).");
                tags.Add(attr!.Tag);
            }
            Assert.That(tags.Distinct().Count(), Is.EqualTo(tags.Count), "ReplayTags must be unique.");
        }

        [Test]
        public void DebugSpawnIntent_Is_Absent_Without_EMBERFALL_DEV()
        {
            // hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.6 — the dev-only spawn door must not exist in a release/server compile.
            var t = typeof(PlayerIntent).Assembly.GetType("Emberfall.Core.Contracts.DebugSpawnIntent");
            Assert.That(t, Is.Null, "DebugSpawnIntent must not compile without the EMBERFALL_DEV symbol.");
        }

        [Test]
        public void DeterministicRandom_And_GameState_Are_In_The_Contracts_Assembly()
        {
            // dev-docs/architecture/system-overview.md:9 — consumers bind to these as part of the contract surface.
            var asm = typeof(GameState).Assembly.GetName().Name;
            Assert.That(asm, Is.EqualTo("Emberfall.Core.Contracts"));
            Assert.That(typeof(DeterministicRandom).Assembly.GetName().Name, Is.EqualTo("Emberfall.Core.Contracts"));
        }
    }
}
