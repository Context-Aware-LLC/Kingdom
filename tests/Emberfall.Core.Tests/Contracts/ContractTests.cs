using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Contracts
{
    /// <summary>
    /// L2 — asserts the Emberfall.Core.Contracts surface itself, so a refactor that silently breaks the
    /// public API fails before any consumer team is affected (test-strategy.md §2, L2).
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
        public void VfxEvent_Vocabulary_Is_The_Closed_D7_Set_Of_22()
        {
            // game-state-and-actions.md §5.2 lists exactly 22 closed D7 events.
            Assert.That(Enum.GetValues(typeof(VfxEvent)).Length, Is.EqualTo(22));
        }

        [Test]
        public void Keyword_Enum_Has_The_18_Canonical_Keywords()
        {
            // card-schema.md §4 v1.1 added OVERWHELM + VANGUARD (GDD §1e, requiresEngineeringPrimitive lifted by step 2)
            Assert.That(Enum.GetValues(typeof(Keyword)).Length, Is.EqualTo(18));
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
            // interfaces.md §4.6 — the dev-only spawn door must not exist in a release/server compile.
            var t = typeof(PlayerIntent).Assembly.GetType("Emberfall.Core.Contracts.DebugSpawnIntent");
            Assert.That(t, Is.Null, "DebugSpawnIntent must not compile without the EMBERFALL_DEV symbol.");
        }

        [Test]
        public void DeterministicRandom_And_GameState_Are_In_The_Contracts_Assembly()
        {
            // interfaces.md §1 — consumers bind to these as part of the contract surface.
            var asm = typeof(GameState).Assembly.GetName().Name;
            Assert.That(asm, Is.EqualTo("Emberfall.Core.Contracts"));
            Assert.That(typeof(DeterministicRandom).Assembly.GetName().Name, Is.EqualTo("Emberfall.Core.Contracts"));
        }
    }
}
