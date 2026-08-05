using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Projection
{
    /// <summary>
    /// §7.1 test 5 — the default-deny totality tripwire (dev-docs/architecture/determinism-and-projection.md:35), mirror of the
    /// A2a serializer-manifest discipline: every field reachable from <see cref="GameState"/> must
    /// be pinned below WITH its §5.2 redaction class, and every <see cref="GameEventKind"/> member must
    /// be pinned as OBL-8-classified. Adding a Contracts field or event kind without updating this
    /// manifest (and the projector, and the spec row) fails CI — an unclassified field is REDACTED
    /// by omission in the clone walk, but it must never be UNCLASSIFIED silently.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ProjectionManifestTests
    {
        // The pinned classification manifest (dev-docs/architecture/determinism-and-projection.md:35).
        private static readonly Dictionary<Type, Dictionary<string, string>> Manifest = new()
        {
            [typeof(GameState)] = new()
            {
                ["Id"] = "PUBLIC",
                ["StateVersion"] = "PUBLIC",
                ["Rng"] = "REDACTED (all-zero cursor)",
                ["ActivePlayer"] = "PUBLIC",
                ["Phase"] = "PUBLIC",
                ["TurnNumber"] = "PUBLIC",
                ["Players"] = "per-field",
                ["FatigueCounter0"] = "PUBLIC",
                ["FatigueCounter1"] = "PUBLIC",
                ["Outcome"] = "PUBLIC",
                ["ActionResolutionsThisTurn"] = "PUBLIC",
                ["NextInstanceCounter"] = "REDACTED (normalized max-visible+1)",
                ["NextEventSequence"] = "PUBLIC",
                ["Mulligan0Done"] = "PUBLIC",
                ["Mulligan1Done"] = "PUBLIC",
                ["PendingChoice"] = "per-field (OBL-1)",
            },
            [typeof(PlayerState)] = new()
            {
                ["Id"] = "PUBLIC",
                ["Hero"] = "PUBLIC (all leaves)",
                ["Deck"] = "REDACTED for BOTH viewers (card-backs, count survives)",
                ["Hand"] = "OWNER (opponent gets card-backs)",
                ["Board"] = "PUBLIC",
                ["Weapon"] = "PUBLIC",
                ["Graveyard"] = "PUBLIC",
                ["Mana"] = "PUBLIC",
                ["HeroPowerUsedThisTurn"] = "PUBLIC",
                ["HeroAttacksUsedThisTurn"] = "PUBLIC",
                ["NextTurnBackdraftLock"] = "PUBLIC",
            },
            [typeof(HeroInstance)] = new()
            {
                ["Id"] = "PUBLIC",
                ["Controller"] = "PUBLIC",
                ["Class"] = "PUBLIC",
                ["CurrentHealth"] = "PUBLIC",
                ["MaxHealth"] = "PUBLIC",
                ["Armor"] = "PUBLIC",
                ["HeroPowerId"] = "PUBLIC",
                ["IsFrosted"] = "PUBLIC",
            },
            [typeof(CardInstance)] = new()
            {
                ["Id"] = "PUBLIC in visible zones (sentinel in card-backs)",
                ["DefinitionId"] = "PUBLIC in visible zones (empty in card-backs)",
                ["Controller"] = "PUBLIC (preserved in card-backs)",
                ["Zone"] = "PUBLIC (preserved in card-backs)",
                ["CurrentAttack"] = "PUBLIC in visible zones (0 in card-backs)",
                ["CurrentHealth"] = "PUBLIC in visible zones (0 in card-backs)",
                ["MaxHealth"] = "PUBLIC in visible zones (0 in card-backs)",
                ["CurrentDurability"] = "PUBLIC in visible zones (null in card-backs)",
                ["Keywords"] = "PUBLIC in visible zones (empty in card-backs)",
                ["Enchantments"] = "PUBLIC in visible zones (empty in card-backs)",
                ["HasAegisShield"] = "PUBLIC in visible zones (false in card-backs)",
                ["IsFrosted"] = "PUBLIC in visible zones (false in card-backs)",
                ["IsSilenced"] = "PUBLIC in visible zones (false in card-backs)",
                ["SummonedThisTurn"] = "PUBLIC in visible zones (false in card-backs)",
                ["AttacksUsedThisTurn"] = "PUBLIC in visible zones (0 in card-backs)",
                ["HasUsedRekindle"] = "PUBLIC in visible zones (false in card-backs)",
                ["Position"] = "PUBLIC (board only; 0 elsewhere)",
            },
            [typeof(Enchantment)] = new()
            {
                ["Source"] = "PUBLIC, resolution-guarded (sentinel when referent hidden/unresolvable — design §4)",
                ["AttackDelta"] = "PUBLIC",
                ["HealthDelta"] = "PUBLIC",
                ["GrantedKeyword"] = "PUBLIC",
            },
            [typeof(ManaPool)] = new()
            {
                ["Max"] = "PUBLIC",
                ["Available"] = "PUBLIC",
                ["Spent"] = "PUBLIC",
                ["Locked"] = "PUBLIC",
                ["Temporary"] = "PUBLIC",
            },
            [typeof(PendingChoice)] = new()
            {
                ["Chooser"] = "UNREDACTED",
                ["Kind"] = "UNREDACTED",
                ["Source"] = "chooser-only (sentinel for non-chooser)",
                ["Options"] = "chooser-only (EMPTY for non-chooser — count undisclosed)",
                ["ChoiceSequence"] = "chooser-only (0 for non-chooser)",
            },
        };

        [Test]
        public void Every_Reachable_Contracts_Field_Has_A_Redaction_Row()
        {
            foreach (var (type, rows) in Manifest)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                var fieldNames = new HashSet<string>();
                foreach (var f in fields) fieldNames.Add(f.Name);

                foreach (var f in fieldNames)
                    Assert.That(rows.ContainsKey(f), Is.True,
                        $"UNCLASSIFIED FIELD: {type.Name}.{f} has no redaction row. Default-deny: " +
                        "classify it in dev-docs/architecture/determinism-and-projection.md:35, implement it in " +
                        "GameStateProjector, then pin it here — in that order.");

                foreach (var r in rows.Keys)
                    Assert.That(fieldNames.Contains(r), Is.True,
                        $"STALE ROW: {type.Name}.{r} is pinned but no longer exists in Contracts.");
            }
        }

        [Test]
        public void Every_Semantic_Event_Kind_Has_An_OBL8_Row()
        {
            // Pinned OBL-8 vocabulary (dev-docs/architecture/determinism-and-projection.md:24). A new enum member without a
            // table row (and a projector rule) must fail here, not leak silently.
            var classified = new HashSet<string>
            {
                "CardPlayed", "CardSummoned", "AttackDeclared", "DamageApplied", "HealthRestored",
                "CardDestroyed", "StatsModified", "KeywordApplied", "CardDrawn", "CardCreatedInHand",
                "CardSilenced", "FrostApplied", "CardTransformed", "CardBounced", "ManaGained",
                "WeaponEquipped", "UnearthOffered", "TurnStarted", "TurnEnded", "HeroPowerUsed",
                "FatigueDamage", "MatchWon", "MatchEnded",
            };

            var actual = Enum.GetNames(typeof(GameEventKind));
            Assert.That(actual, Has.Length.EqualTo(classified.Count),
                "GameEventKind vocabulary changed — author the OBL-8 row before extending this pin.");
            foreach (var name in actual)
                Assert.That(classified.Contains(name), Is.True,
                    $"UNCLASSIFIED EVENT KIND: GameEventKind.{name} has no OBL-8 row.");
        }
    }
}
