using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Support
{
    /// <summary>
    /// The ONE field-inventory manifest + per-field mutation list + hand-built seed state, shared
    /// between the canonical-serializer guards (<c>CanonicalSerializationTests</c>) and the A5 wire
    /// mutation sweep (<c>Kingdom.Net.Protocol.Tests</c>). Extracted per hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md
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
                "Id", "Mulligan0Done", "Mulligan1Done", "NextDeathSequence", "NextEventSequence",
                "NextInstanceCounter", "OngoingEffects", "Outcome", "PendingChoice", "Phase", "Players",
                "Rng", "StateVersion", "TurnNumber"
            },
            [typeof(OngoingEffectInstance)] = new[]
            {
                "Controller", "Id", "Lifetime", "RemainingUses", "RuleId", "SourceDefinitionId"
            },
            [typeof(PendingChoice)] = new[]
            {
                "ChoiceSequence", "Chooser", "Kind", "Options", "RemainingOffers", "Source"
            },
            [typeof(PlayerState)] = new[]
            {
                "Board", "Deck", "Graveyard", "Hand", "Hero", "HeroAttacksUsedThisTurn",
                "HeroDamageTakenThisTurn", "HeroPowerUsedThisTurn", "Id", "Mana", "NextTurnBackdraftLock", "Weapon"
            },
            // EXPERIMENT (effect-model fork): IsFrosted and FrostThawPending left this inventory because
            // they stopped being state — they are now derived properties over Effects, and a derived view
            // is not a canonical field. The inventory is fields, so it follows the state, not the reads.
            [typeof(HeroInstance)] = new[]
            {
                "Armor", "Class", "Controller", "CurrentHealth", "Effects", "HeroPowerId", "Id", "MaxHealth"
            },
            [typeof(CardInstance)] = new[]
            {
                "AttacksUsedThisTurn", "Controller", "CurrentAttack", "CurrentDurability", "CurrentHealth",
                "DefinitionId", "DiedAtSequence", "Effects", "Enchantments", "HasAegisShield",
                "HasUsedRekindle", "Id", "IsSilenced", "Keywords", "MaxHealth", "Position",
                "SummonedThisTurn", "Zone"
            },
            [typeof(EffectInstance)] = new[] { "Kind", "Scope", "Source", "TurnsRemaining" },
            [typeof(Enchantment)] = new[] { "AttackDelta", "GrantedKeyword", "HealthDelta", "IsAura", "Source" },
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
            ["GameState.NextDeathSequence"] = s => s.NextDeathSequence++,
            ["GameState.Mulligan0Done"] = s => s.Mulligan0Done = !s.Mulligan0Done,
            ["GameState.Mulligan1Done"] = s => s.Mulligan1Done = !s.Mulligan1Done,
            ["GameState.OngoingEffects"] = s => s.OngoingEffects.Clear(),
            ["GameState.PendingChoice"] = s => s.PendingChoice = null,   // present → absent flips the flag byte

            // OngoingEffectInstance (the seeded finite observer)
            ["OngoingEffectInstance.Id"] = s => Ongoing(s).Id = new InstanceId(81),
            ["OngoingEffectInstance.Controller"] = s => Ongoing(s).Controller = s.Players[1].Id,
            ["OngoingEffectInstance.SourceDefinitionId"] = s => Ongoing(s).SourceDefinitionId = FixtureCard.Id("ongoing_other"),
            ["OngoingEffectInstance.RuleId"] = s => Ongoing(s).RuleId = "other_rule",
            ["OngoingEffectInstance.Lifetime"] = s =>
            {
                Ongoing(s).Lifetime = OngoingEffectLifetime.ONE_SHOT;
                Ongoing(s).RemainingUses = 1;
            },
            ["OngoingEffectInstance.RemainingUses"] = s => Ongoing(s).RemainingUses = 3,

            // PendingChoice (seeded non-null so the canonical mutation sweep covers every field;
            // runtime behavior and transport order have dedicated Unearth tests).
            ["PendingChoice.Chooser"] = s => s.PendingChoice!.Chooser = s.Players[1].Id,
            ["PendingChoice.Kind"] = s => s.PendingChoice!.Kind = (PendingChoiceKind)1,   // no second member yet (Q1: Unearth-only); the cast perturbs the canonical int
            ["PendingChoice.Source"] = s => s.PendingChoice!.Source = new InstanceId(70),
            ["PendingChoice.Options"] = s => s.PendingChoice!.Options.Add(FixtureCard.Id("disc_seed_extra")),
            ["PendingChoice.ChoiceSequence"] = s => s.PendingChoice!.ChoiceSequence++,
            ["PendingChoice.RemainingOffers"] = s => s.PendingChoice!.RemainingOffers++,

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
            ["PlayerState.HeroDamageTakenThisTurn"] = s => s.Players[0].HeroDamageTakenThisTurn++,
            ["PlayerState.NextTurnBackdraftLock"] = s => s.Players[0].NextTurnBackdraftLock++,

            // HeroInstance (seat 0's hero)
            ["HeroInstance.Id"] = s => s.Players[0].Hero.Id = new InstanceId(77),
            ["HeroInstance.Controller"] = s => s.Players[0].Hero.Controller = s.Players[1].Id,
            ["HeroInstance.Class"] = s => s.Players[0].Hero.Class = CardClass.GRAVEKEEP,
            ["HeroInstance.CurrentHealth"] = s => s.Players[0].Hero.CurrentHealth--,
            ["HeroInstance.MaxHealth"] = s => s.Players[0].Hero.MaxHealth++,
            ["HeroInstance.Armor"] = s => s.Players[0].Hero.Armor += 3,
            ["HeroInstance.HeroPowerId"] = s => s.Players[0].Hero.HeroPowerId = 2,  // hp_other
            ["HeroInstance.Effects"] = s => s.Players[0].Hero.Effects.Add(
                new EffectInstance(EffectKind.Frost, new InstanceId(64), EffectScope.ControllerTurns, 1)),

            // CardInstance (the seeded hand card — has durability, keywords, enchantment)
            ["CardInstance.Id"] = s => HandCard(s).Id = new InstanceId(88),
            ["CardInstance.DefinitionId"] = s => HandCard(s).DefinitionId = FixtureCard.Id("card_other"),
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
            ["CardInstance.Effects"] = s => HandCard(s).Effects.Add(
                new EffectInstance(EffectKind.Frost, new InstanceId(62), EffectScope.ControllerTurns, 1)),
            ["CardInstance.IsSilenced"] = s => HandCard(s).IsSilenced = !HandCard(s).IsSilenced,
            ["CardInstance.SummonedThisTurn"] = s => HandCard(s).SummonedThisTurn = !HandCard(s).SummonedThisTurn,
            ["CardInstance.AttacksUsedThisTurn"] = s => HandCard(s).AttacksUsedThisTurn++,
            ["CardInstance.HasUsedRekindle"] = s => HandCard(s).HasUsedRekindle = !HandCard(s).HasUsedRekindle,
            ["CardInstance.Position"] = s => HandCard(s).Position++,
            ["CardInstance.DiedAtSequence"] = s => HandCard(s).DiedAtSequence++,

            // Enchantment (the seeded hand card's enchantment)
            ["Enchantment.Source"] = s => HandCard(s).Enchantments[0].Source = new InstanceId(66),
            ["Enchantment.AttackDelta"] = s => HandCard(s).Enchantments[0].AttackDelta++,
            ["Enchantment.HealthDelta"] = s => HandCard(s).Enchantments[0].HealthDelta++,
            ["Enchantment.GrantedKeyword"] = s => HandCard(s).Enchantments[0].GrantedKeyword = Keyword.SKIRMISH,
            ["Enchantment.IsAura"] = s => HandCard(s).Enchantments[0].IsAura = !HandCard(s).Enchantments[0].IsAura,

            // EffectInstance (the seeded hand card's effect). Each mutation perturbs one canonical
            // field of the instance rather than the presence of the list, which CardInstance.Effects
            // already covers.
            ["EffectInstance.Kind"] = s => SeededEffect(s).Kind = EffectKind.LethalDamagePreventionSpent,
            ["EffectInstance.Source"] = s => SeededEffect(s).Source = new InstanceId(67),
            ["EffectInstance.Scope"] = s => SeededEffect(s).Scope = EffectScope.Permanent,
            ["EffectInstance.TurnsRemaining"] = s => SeededEffect(s).TurnsRemaining++,

            // ManaPool (seat 0)
            ["ManaPool.Max"] = s => s.Players[0].Mana.Max++,
            ["ManaPool.Available"] = s => s.Players[0].Mana.Available++,
            ["ManaPool.Spent"] = s => s.Players[0].Mana.Spent++,
            ["ManaPool.Locked"] = s => s.Players[0].Mana.Locked++,
            ["ManaPool.Temporary"] = s => s.Players[0].Mana.Temporary++,
        };

        public static CardInstance HandCard(GameState s) => s.Players[0].Hand[0];

        /// <summary>The seeded hand card's effect instance — the per-field mutation target.</summary>
        public static EffectInstance SeededEffect(GameState s) => HandCard(s).Effects[0];

        public static CardInstance MakeCard(int id, PlayerId controller, Zone zone)
        {
            var c = new CardInstance(new InstanceId(id), FixtureCard.Id($"card_seed_{id}"), controller)
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
                HeroPowerId = 1,  // hp_ember
            });
            var p1Hero = new HeroInstance(new InstanceId(1), p1Id, CardClass.TIDECALLER, 30)
            {
                HeroPowerId = 2,  // hp_tide
            };
            p1Hero.Effects.Add(new EffectInstance(
                EffectKind.Frost, new InstanceId(0), EffectScope.ControllerTurns, 1));
            var p1 = new PlayerState(p1Id, p1Hero);

            // Seat 0: every zone populated, weapon equipped, a card with durability + keywords +
            // an enchantment — so every branch of the walk (incl. both nullable shapes) is exercised.
            p0.Deck.Add(MakeCard(2, p0Id, Zone.Deck));
            var handCard = MakeCard(3, p0Id, Zone.Hand);
            handCard.CurrentDurability = 2;
            handCard.Keywords.Add(Keyword.BULWARK);
            handCard.Keywords.Add(Keyword.SHROUD);
            handCard.Effects.Add(new EffectInstance(
                EffectKind.Frost, new InstanceId(4), EffectScope.ControllerTurns, 1));
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
            p0.Weapon.DefinitionId = FixtureCard.Id("weapon_seed");
            p0.Weapon.CurrentAttack = 3;
            p0.Weapon.CurrentDurability = 2;
            p0.Weapon.Keywords.Add(Keyword.LIFEDRAIN);
            p0.Mana = new ManaPool { Max = 5, Available = 3, Spent = 2, Locked = 1, Temporary = 1 };
            p0.HeroAttacksUsedThisTurn = 1;
            p0.HeroDamageTakenThisTurn = 3;
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
                NextInstanceCounter = 10,
                NextEventSequence = 42,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.Rng.NextUInt64();   // advance the cursor so RawState ≠ seed (proves the cursor is hashed)

            // Non-null PendingChoice with ≥2 options so the canonical count prefix and ordered
            // list walk are both exercised.
            s.PendingChoice = new PendingChoice
            {
                Chooser = p0Id,
                Kind = PendingChoiceKind.Unearth,
                Source = new InstanceId(5),
                Options = { FixtureCard.Id("disc_seed_a"), FixtureCard.Id("disc_seed_b") },
                ChoiceSequence = 11,
            };
            s.OngoingEffects.Add(OngoingEffectInstance.CreateFinite(new InstanceId(9), p0Id,
                FixtureCard.Id("ongoing_seed"), "turn_end_echo", 2));
            return s;
        }

        private static OngoingEffectInstance Ongoing(GameState s) => s.OngoingEffects[0];
    }
}
