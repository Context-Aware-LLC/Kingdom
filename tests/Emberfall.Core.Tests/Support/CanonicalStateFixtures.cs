using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// The ONE field-inventory manifest + per-field mutation list + hand-built seed state, shared
    /// between the canonical-serializer guards (<c>CanonicalSerializationTests</c>) and the A5 wire
    /// mutation sweep (<c>Emberfall.Net.Protocol.Tests</c>). Extracted per hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md
    /// §6 (§12 finding 2): duplicating this list would re-create exactly the two-lists drift the
    /// bridge test exists to kill — every consumer references THIS list.
    /// </summary>
    public static class CanonicalStateFixtures
    {
        /// <summary>
        /// The committed manifest: every public instance field of every state type in the canonical
        /// graph, sorted ordinal. Changing a state type flips the inventory test red — update the
        /// manifest AND the serializer walk (AND the A5 wire walk) together.
        /// </summary>
        public static readonly IReadOnlyDictionary<Type, string[]> Manifest = new Dictionary<Type, string[]>
        {
            [typeof(GameState)] = new[]
            {
                "ActionResolutionsThisTurn", "ActivePlayer", "FatigueCounter0", "FatigueCounter1",
                "Id", "Mulligan0Done", "Mulligan1Done", "NextEventSequence", "NextInstanceCounter",
                "Outcome", "PendingChoice", "Phase", "Players", "Rng", "StateVersion", "TurnNumber"
            },
            [typeof(PendingChoice)] = new[]
            {
                "ChoiceSequence", "Chooser", "Kind", "Options", "Source"
            },
            [typeof(PlayerState)] = new[]
            {
                "Board", "Deck", "Graveyard", "Hand", "Hero", "HeroAttacksUsedThisTurn",
                "HeroPowerUsedThisTurn", "Id", "Mana", "NextTurnBackdraftLock", "Weapon"
            },
            [typeof(HeroInstance)] = new[]
            {
                "Armor", "Class", "Controller", "CurrentHealth", "HeroPowerId", "Id", "IsFrosted", "MaxHealth"
            },
            [typeof(CardInstance)] = new[]
            {
                "AttacksUsedThisTurn", "Controller", "CurrentAttack", "CurrentDurability", "CurrentHealth",
                "DefinitionId", "Enchantments", "HasAegisShield", "HasUsedRekindle", "Id", "IsFrosted", "IsSilenced",
                "Keywords", "MaxHealth", "Position", "SummonedThisTurn", "Zone"
            },
            [typeof(Enchantment)] = new[] { "AttackDelta", "GrantedKeyword", "HealthDelta", "Source" },
            [typeof(ManaPool)] = new[] { "Available", "Locked", "Max", "Spent", "Temporary" },
        };

        /// <summary>One digest-visible perturbation per manifest field, keyed "Type.Field".</summary>
        public static readonly IReadOnlyDictionary<string, Action<GameState>> Mutations = new Dictionary<string, Action<GameState>>
        {
            // GameState
            ["GameState.Id"] = s => s.Id = new MatchId("m-other"),
            ["GameState.StateVersion"] = s => s.StateVersion++,
            ["GameState.Rng"] = s => s.Rng.SetRawState(s.Rng.RawState + 1),
            ["GameState.ActivePlayer"] = s => s.ActivePlayer = s.Players[1].Id,
            ["GameState.Phase"] = s => s.Phase = TurnPhase.EndOfTurn,
            ["GameState.TurnNumber"] = s => s.TurnNumber++,
            ["GameState.Players"] = s => s.Players = new[] { s.Players[1], s.Players[0] },
            ["GameState.FatigueCounter0"] = s => s.FatigueCounter0++,
            ["GameState.FatigueCounter1"] = s => s.FatigueCounter1++,
            ["GameState.Outcome"] = s => s.Outcome = MatchOutcome.Player0Win,
            ["GameState.ActionResolutionsThisTurn"] = s => s.ActionResolutionsThisTurn++,
            ["GameState.NextInstanceCounter"] = s => s.NextInstanceCounter++,
            ["GameState.NextEventSequence"] = s => s.NextEventSequence++,
            ["GameState.Mulligan0Done"] = s => s.Mulligan0Done = !s.Mulligan0Done,
            ["GameState.Mulligan1Done"] = s => s.Mulligan1Done = !s.Mulligan1Done,
            ["GameState.PendingChoice"] = s => s.PendingChoice = null,   // present → absent flips the flag byte

            // PendingChoice (seeded non-null — M2 production never populates it, so the seed is
            // the ONLY coverage of the non-null walk branch until Phase 2; §7 ruling item 7)
            ["PendingChoice.Chooser"] = s => s.PendingChoice!.Chooser = s.Players[1].Id,
            ["PendingChoice.Kind"] = s => s.PendingChoice!.Kind = (PendingChoiceKind)1,   // no second member yet (Q1: Unearth-only); the cast perturbs the canonical int
            ["PendingChoice.Source"] = s => s.PendingChoice!.Source = new InstanceId(70),
            ["PendingChoice.Options"] = s => s.PendingChoice!.Options.Add("disc_seed_extra"),
            ["PendingChoice.ChoiceSequence"] = s => s.PendingChoice!.ChoiceSequence++,

            // PlayerState (seat 0)
            ["PlayerState.Id"] = s =>
            {
                // Also retarget dependents so ONLY the id field's bytes change intent, not validity.
                s.Players[0].Id = new PlayerId("p0x");
            },
            ["PlayerState.Hero"] = s => s.Players[0].Hero =
                new HeroInstance(new InstanceId(90), s.Players[0].Id, CardClass.VERDANT, 25),
            ["PlayerState.Deck"] = s => s.Players[0].Deck.Add(MakeCard(91, s.Players[0].Id, Zone.Deck)),
            ["PlayerState.Hand"] = s => s.Players[0].Hand.Add(MakeCard(92, s.Players[0].Id, Zone.Hand)),
            ["PlayerState.Board"] = s => s.Players[0].Board.Add(MakeCard(93, s.Players[0].Id, Zone.Board)),
            ["PlayerState.Weapon"] = s => s.Players[0].Weapon = null,   // present → absent flips the flag byte
            ["PlayerState.Graveyard"] = s => s.Players[0].Graveyard.Add(MakeCard(94, s.Players[0].Id, Zone.Graveyard)),
            ["PlayerState.Mana"] = s => s.Players[0].Mana = new ManaPool { Max = 9, Available = 9 },
            ["PlayerState.HeroPowerUsedThisTurn"] = s => s.Players[0].HeroPowerUsedThisTurn = !s.Players[0].HeroPowerUsedThisTurn,
            ["PlayerState.HeroAttacksUsedThisTurn"] = s => s.Players[0].HeroAttacksUsedThisTurn++,
            ["PlayerState.NextTurnBackdraftLock"] = s => s.Players[0].NextTurnBackdraftLock++,

            // HeroInstance (seat 0's hero)
            ["HeroInstance.Id"] = s => s.Players[0].Hero.Id = new InstanceId(77),
            ["HeroInstance.Controller"] = s => s.Players[0].Hero.Controller = s.Players[1].Id,
            ["HeroInstance.Class"] = s => s.Players[0].Hero.Class = CardClass.GRAVEKEEP,
            ["HeroInstance.CurrentHealth"] = s => s.Players[0].Hero.CurrentHealth--,
            ["HeroInstance.MaxHealth"] = s => s.Players[0].Hero.MaxHealth++,
            ["HeroInstance.Armor"] = s => s.Players[0].Hero.Armor += 3,
            ["HeroInstance.HeroPowerId"] = s => s.Players[0].Hero.HeroPowerId = "hp_other",
            ["HeroInstance.IsFrosted"] = s => s.Players[0].Hero.IsFrosted = !s.Players[0].Hero.IsFrosted,

            // CardInstance (the seeded hand card — has durability, keywords, enchantment)
            ["CardInstance.Id"] = s => HandCard(s).Id = new InstanceId(88),
            ["CardInstance.DefinitionId"] = s => HandCard(s).DefinitionId = "card_other",
            ["CardInstance.Controller"] = s => HandCard(s).Controller = s.Players[1].Id,
            ["CardInstance.Zone"] = s => HandCard(s).Zone = Zone.Graveyard,
            ["CardInstance.CurrentAttack"] = s => HandCard(s).CurrentAttack++,
            ["CardInstance.CurrentHealth"] = s => HandCard(s).CurrentHealth++,
            ["CardInstance.MaxHealth"] = s => HandCard(s).MaxHealth++,
            ["CardInstance.CurrentDurability"] = s => HandCard(s).CurrentDurability = null,
            ["CardInstance.Keywords"] = s => HandCard(s).Keywords.Add(Keyword.DOUBLESTRIKE),
            ["CardInstance.Enchantments"] = s => HandCard(s).Enchantments.Add(
                new Enchantment { Source = new InstanceId(60), AttackDelta = 2, HealthDelta = 2 }),
            ["CardInstance.HasAegisShield"] = s => HandCard(s).HasAegisShield = !HandCard(s).HasAegisShield,
            ["CardInstance.IsFrosted"] = s => HandCard(s).IsFrosted = !HandCard(s).IsFrosted,
            ["CardInstance.IsSilenced"] = s => HandCard(s).IsSilenced = !HandCard(s).IsSilenced,
            ["CardInstance.SummonedThisTurn"] = s => HandCard(s).SummonedThisTurn = !HandCard(s).SummonedThisTurn,
            ["CardInstance.AttacksUsedThisTurn"] = s => HandCard(s).AttacksUsedThisTurn++,
            ["CardInstance.HasUsedRekindle"] = s => HandCard(s).HasUsedRekindle = !HandCard(s).HasUsedRekindle,
            ["CardInstance.Position"] = s => HandCard(s).Position++,

            // Enchantment (the seeded hand card's enchantment)
            ["Enchantment.Source"] = s => HandCard(s).Enchantments[0].Source = new InstanceId(66),
            ["Enchantment.AttackDelta"] = s => HandCard(s).Enchantments[0].AttackDelta++,
            ["Enchantment.HealthDelta"] = s => HandCard(s).Enchantments[0].HealthDelta++,
            ["Enchantment.GrantedKeyword"] = s => HandCard(s).Enchantments[0].GrantedKeyword = Keyword.SKIRMISH,

            // ManaPool (seat 0)
            ["ManaPool.Max"] = s => s.Players[0].Mana.Max++,
            ["ManaPool.Available"] = s => s.Players[0].Mana.Available++,
            ["ManaPool.Spent"] = s => s.Players[0].Mana.Spent++,
            ["ManaPool.Locked"] = s => s.Players[0].Mana.Locked++,
            ["ManaPool.Temporary"] = s => s.Players[0].Mana.Temporary++,
        };

        public static CardInstance HandCard(GameState s) => s.Players[0].Hand[0];

        public static CardInstance MakeCard(int id, PlayerId controller, Zone zone)
        {
            var c = new CardInstance(new InstanceId(id), $"card_seed_{id}", controller)
            {
                Zone = zone,
                CurrentAttack = 2,
                CurrentHealth = 3,
                MaxHealth = 3,
            };
            return c;
        }

        /// <summary>Hand-built state exercising every walk branch incl. both nullable shapes.</summary>
        public static GameState BuildSeedState()
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(0), p0Id, CardClass.EMBERWARD, 30)
            {
                CurrentHealth = 27,
                Armor = 2,
                HeroPowerId = "hp_ember",
            });
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(1), p1Id, CardClass.TIDECALLER, 30)
            {
                HeroPowerId = "hp_tide",
                IsFrosted = true,
            });

            // Seat 0: every zone populated, weapon equipped, a card with durability + keywords +
            // an enchantment — so every branch of the walk (incl. both nullable shapes) is exercised.
            p0.Deck.Add(MakeCard(2, p0Id, Zone.Deck));
            var handCard = MakeCard(3, p0Id, Zone.Hand);
            handCard.CurrentDurability = 2;
            handCard.Keywords.Add(Keyword.BULWARK);
            handCard.Keywords.Add(Keyword.SHROUD);
            handCard.Enchantments.Add(new Enchantment
            {
                Source = new InstanceId(4),
                AttackDelta = 1,
                HealthDelta = 1,
                GrantedKeyword = Keyword.FERVOR,
            });
            p0.Hand.Add(handCard);
            var boardCard = MakeCard(5, p0Id, Zone.Board);
            boardCard.SummonedThisTurn = true;
            boardCard.Position = 0;
            p0.Board.Add(boardCard);
            p0.Graveyard.Add(MakeCard(6, p0Id, Zone.Graveyard));
            p0.Weapon = MakeCard(7, p0Id, Zone.Weapon);
            p0.Weapon.DefinitionId = "weapon_seed";
            p0.Weapon.CurrentAttack = 3;
            p0.Weapon.CurrentDurability = 2;
            p0.Weapon.Keywords.Add(Keyword.LIFEDRAIN);
            p0.Mana = new ManaPool { Max = 5, Available = 3, Spent = 2, Locked = 1, Temporary = 1 };
            p0.HeroAttacksUsedThisTurn = 1;
            p0.NextTurnBackdraftLock = 2;

            // Seat 1: sparse on purpose (empty zones + null weapon are also canonical branches).
            p1.Deck.Add(MakeCard(8, p1Id, Zone.Deck));
            p1.Mana = new ManaPool { Max = 4, Available = 4 };
            p1.HeroPowerUsedThisTurn = true;

            var s = new GameState(new MatchId("m-canonical-vector"), new DeterministicRandom(0xE0BE5EEDUL), p0, p1)
            {
                StateVersion = 7,
                Phase = TurnPhase.Main,
                TurnNumber = 3,
                FatigueCounter0 = 1,
                FatigueCounter1 = 0,
                ActionResolutionsThisTurn = 4,
                NextInstanceCounter = 9,
                NextEventSequence = 42,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.Rng.NextUInt64();   // advance the cursor so RawState ≠ seed (proves the cursor is hashed)

            // A2b: non-null PendingChoice — M2 production always leaves this null, so this seed is
            // the only exercise of WalkPendingChoice's non-null branch (≥2 options so the count
            // prefix + list loop are both real; §7 ruling 2026-07-15 item 7).
            s.PendingChoice = new PendingChoice
            {
                Chooser = p0Id,
                Kind = PendingChoiceKind.Unearth,
                Source = new InstanceId(5),
                Options = { "disc_seed_a", "disc_seed_b" },
                ChoiceSequence = 11,
            };
            return s;
        }
    }
}
