using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// The anti-divergence guards for the canonical serializer (A2a — a2-serializer-design.md §2.1/§5).
    /// The hand-written <see cref="GameStateSerializer"/> walk is the ONE canonical field order; these
    /// tests keep the state types and the walk locked together (M2-KICKOFF.md §5 Risk 3):
    ///  1. <b>Field-inventory lock</b> — reflection over the state types vs. a committed manifest.
    ///     Adding/removing/renaming a state field goes red until the author updates BOTH the manifest
    ///     and the walk. (Reflection lives HERE, in a CI guard — never in the encode path, §4.1.)
    ///  2. <b>Per-field mutation totality</b> — every manifest field, when perturbed, must change the
    ///     digest. The inventory proves the walk-owner knows a field exists; this proves it reaches
    ///     the digest.
    ///  3. <b>Pinned known-vector</b> — one hand-built state, one hardcoded digest constant. Catches an
    ///     accidental encoding change even if every fixture were (wrongly) rebaselined.
    ///  4. <b>Stability</b> — repeat and rebuild invariance.
    /// </summary>
    [TestFixture]
    [Category("Firewall")]
    public sealed class CanonicalSerializationTests
    {
        // ── 1. field-inventory lock ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The committed manifest: every public instance field of every state type in the canonical
        /// graph, sorted ordinal. Changing a state type flips this red — update the manifest AND the
        /// serializer walk together (that is the point). PendingChoice landed here via the A2b
        /// §7-reviewed Contracts change (engineering/a2b-scoped-review-2026-07-15.md).
        /// </summary>
        private static readonly Dictionary<Type, string[]> Manifest = new Dictionary<Type, string[]>
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
                "HeroPowerUsedThisTurn", "Id", "Mana", "NextTurnOverloadLock", "Weapon"
            },
            [typeof(HeroInstance)] = new[]
            {
                "Armor", "Class", "Controller", "CurrentHealth", "HeroPowerId", "Id", "IsFrozen", "MaxHealth"
            },
            [typeof(CardInstance)] = new[]
            {
                "AttacksUsedThisTurn", "Controller", "CurrentAttack", "CurrentDurability", "CurrentHealth",
                "DefinitionId", "Enchantments", "HasAegisShield", "HasUsedReborn", "Id", "IsFrozen",
                "Keywords", "MaxHealth", "Position", "SummonedThisTurn", "Zone"
            },
            [typeof(Enchantment)] = new[] { "AttackDelta", "GrantedKeyword", "HealthDelta", "Source" },
            [typeof(WeaponInstance)] = new[] { "Attack", "DefinitionId", "Durability", "Id" },
            [typeof(ManaPool)] = new[] { "Available", "Locked", "Max", "Spent", "Temporary" },
        };

        [Test]
        public void State_Type_Fields_Match_Committed_Manifest()
        {
            foreach (var (type, expected) in Manifest.Select(kv => (kv.Key, kv.Value)))
            {
                var actual = type
                    .GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                Assert.That(actual, Is.EqualTo(expected),
                    $"{type.Name}'s public fields diverged from the canonical manifest. If you added/" +
                    "removed/renamed a state field: update GameStateSerializer's walk AND this manifest " +
                    "together (a2-serializer-design.md §2.1). A GameState field addition is hash-canonical " +
                    "— it needs the §7 scoped review + fixture rebaseline.");
            }
        }

        // ── 2. per-field mutation totality ──────────────────────────────────────────────────────

        /// <summary>One digest-visible perturbation per manifest field, keyed "Type.Field".</summary>
        private static readonly Dictionary<string, Action<GameState>> Mutations = new Dictionary<string, Action<GameState>>
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
            ["PendingChoice.Kind"] = s => s.PendingChoice!.Kind = (PendingChoiceKind)1,   // no second member yet (Q1: Discover-only); the cast perturbs the canonical int
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
            ["PlayerState.NextTurnOverloadLock"] = s => s.Players[0].NextTurnOverloadLock++,

            // HeroInstance (seat 0's hero)
            ["HeroInstance.Id"] = s => s.Players[0].Hero.Id = new InstanceId(77),
            ["HeroInstance.Controller"] = s => s.Players[0].Hero.Controller = s.Players[1].Id,
            ["HeroInstance.Class"] = s => s.Players[0].Hero.Class = CardClass.GRAVEKEEP,
            ["HeroInstance.CurrentHealth"] = s => s.Players[0].Hero.CurrentHealth--,
            ["HeroInstance.MaxHealth"] = s => s.Players[0].Hero.MaxHealth++,
            ["HeroInstance.Armor"] = s => s.Players[0].Hero.Armor += 3,
            ["HeroInstance.HeroPowerId"] = s => s.Players[0].Hero.HeroPowerId = "hp_other",
            ["HeroInstance.IsFrozen"] = s => s.Players[0].Hero.IsFrozen = !s.Players[0].Hero.IsFrozen,

            // CardInstance (the seeded hand card — has durability, keywords, enchantment)
            ["CardInstance.Id"] = s => HandCard(s).Id = new InstanceId(88),
            ["CardInstance.DefinitionId"] = s => HandCard(s).DefinitionId = "card_other",
            ["CardInstance.Controller"] = s => HandCard(s).Controller = s.Players[1].Id,
            ["CardInstance.Zone"] = s => HandCard(s).Zone = Zone.Graveyard,
            ["CardInstance.CurrentAttack"] = s => HandCard(s).CurrentAttack++,
            ["CardInstance.CurrentHealth"] = s => HandCard(s).CurrentHealth++,
            ["CardInstance.MaxHealth"] = s => HandCard(s).MaxHealth++,
            ["CardInstance.CurrentDurability"] = s => HandCard(s).CurrentDurability = null,
            ["CardInstance.Keywords"] = s => HandCard(s).Keywords.Add(Keyword.WINDFURY),
            ["CardInstance.Enchantments"] = s => HandCard(s).Enchantments.Add(
                new Enchantment { Source = new InstanceId(60), AttackDelta = 2, HealthDelta = 2 }),
            ["CardInstance.HasAegisShield"] = s => HandCard(s).HasAegisShield = !HandCard(s).HasAegisShield,
            ["CardInstance.IsFrozen"] = s => HandCard(s).IsFrozen = !HandCard(s).IsFrozen,
            ["CardInstance.SummonedThisTurn"] = s => HandCard(s).SummonedThisTurn = !HandCard(s).SummonedThisTurn,
            ["CardInstance.AttacksUsedThisTurn"] = s => HandCard(s).AttacksUsedThisTurn++,
            ["CardInstance.HasUsedReborn"] = s => HandCard(s).HasUsedReborn = !HandCard(s).HasUsedReborn,
            ["CardInstance.Position"] = s => HandCard(s).Position++,

            // Enchantment (the seeded hand card's enchantment)
            ["Enchantment.Source"] = s => HandCard(s).Enchantments[0].Source = new InstanceId(66),
            ["Enchantment.AttackDelta"] = s => HandCard(s).Enchantments[0].AttackDelta++,
            ["Enchantment.HealthDelta"] = s => HandCard(s).Enchantments[0].HealthDelta++,
            ["Enchantment.GrantedKeyword"] = s => HandCard(s).Enchantments[0].GrantedKeyword = Keyword.RUSH,

            // WeaponInstance (seat 0's equipped weapon)
            ["WeaponInstance.Id"] = s => s.Players[0].Weapon!.Id = new InstanceId(55),
            ["WeaponInstance.DefinitionId"] = s => s.Players[0].Weapon!.DefinitionId = "weapon_other",
            ["WeaponInstance.Attack"] = s => s.Players[0].Weapon!.Attack++,
            ["WeaponInstance.Durability"] = s => s.Players[0].Weapon!.Durability++,

            // ManaPool (seat 0)
            ["ManaPool.Max"] = s => s.Players[0].Mana.Max++,
            ["ManaPool.Available"] = s => s.Players[0].Mana.Available++,
            ["ManaPool.Spent"] = s => s.Players[0].Mana.Spent++,
            ["ManaPool.Locked"] = s => s.Players[0].Mana.Locked++,
            ["ManaPool.Temporary"] = s => s.Players[0].Mana.Temporary++,
        };

        [Test]
        public void Every_Manifest_Field_Has_A_Mutation_Case()
        {
            var expectedKeys = Manifest
                .SelectMany(kv => kv.Value.Select(f => $"{kv.Key.Name}.{f}"))
                .OrderBy(k => k, StringComparer.Ordinal);

            Assert.That(Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(expectedKeys),
                "Mutation coverage diverged from the manifest — every state field needs a perturbation " +
                "case proving it reaches the digest (a2-serializer-design.md §2.1).");
        }

        [TestCaseSource(nameof(MutationKeys))]
        public void Perturbing_Field_Changes_The_Digest(string key)
        {
            string baseline = GameStateHasher.Hash(BuildSeedState());

            var mutated = BuildSeedState();
            Mutations[key](mutated);

            Assert.That(GameStateHasher.Hash(mutated), Is.Not.EqualTo(baseline),
                $"Mutating {key} did NOT change the canonical digest — the field never reaches the walk. " +
                "This is exactly the silent divergence Risk 3 warns about: fix GameStateSerializer.");
        }

        private static IEnumerable<string> MutationKeys() => Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal);

        // ── 3. pinned known-vector ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Digest of <see cref="BuildSeedState"/>, pinned by hand. Independent of fixtures and card
        /// data: if this moves and you didn't intend an encoding change, the serializer broke; if you
        /// DID intend one, that is a hash-canonical change (§7 review + full fixture rebaseline) and
        /// this constant is re-pinned in the same reviewed commit.
        /// </summary>
        // Re-pinned 2026-07-15 (A2b, §7-reviewed): walk gained the PendingChoice tail + the seed
        // state now exercises it non-null. Old pin: abb617ccf5e1c67c7697f4b52b1658c29e63045abda9e990f2561b1bfca439db
        private const string PinnedSeedDigest = "23b592020596174461142909891e0b33b412929f98e47b3e7ae6d641c3030009";

        [Test]
        public void Known_Vector_Digest_Is_Pinned()
        {
            Assert.That(GameStateHasher.Hash(BuildSeedState()), Is.EqualTo(PinnedSeedDigest),
                "The canonical encoding changed (or the seed-state builder did). If unintentional: the " +
                "serializer broke — fix it. If intentional: this is hash-canonical; convene §7, rebaseline " +
                "fixtures, and re-pin this constant in the same commit.");
        }

        // ── 4. stability ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Hash_Is_Repeatable_And_Rebuild_Invariant()
        {
            var s = BuildSeedState();
            string first = GameStateHasher.Hash(s);
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(first), "Repeat hash drifted.");
            Assert.That(GameStateHasher.Hash(BuildSeedState()), Is.EqualTo(first),
                "Two identically-built states hash differently — hidden nondeterminism in the walk.");
        }

        // ── seed-state builder (hand-built; exercises every walk branch incl. nullables) ────────

        private static CardInstance HandCard(GameState s) => s.Players[0].Hand[0];

        private static CardInstance MakeCard(int id, PlayerId controller, Zone zone)
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

        private static GameState BuildSeedState()
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
                IsFrozen = true,
            });

            // Seat 0: every zone populated, weapon equipped, a card with durability + keywords +
            // an enchantment — so every branch of the walk (incl. both nullable shapes) is exercised.
            p0.Deck.Add(MakeCard(2, p0Id, Zone.Deck));
            var handCard = MakeCard(3, p0Id, Zone.Hand);
            handCard.CurrentDurability = 2;
            handCard.Keywords.Add(Keyword.TAUNT);
            handCard.Keywords.Add(Keyword.STEALTH);
            handCard.Enchantments.Add(new Enchantment
            {
                Source = new InstanceId(4),
                AttackDelta = 1,
                HealthDelta = 1,
                GrantedKeyword = Keyword.CHARGE,
            });
            p0.Hand.Add(handCard);
            var boardCard = MakeCard(5, p0Id, Zone.Board);
            boardCard.SummonedThisTurn = true;
            boardCard.Position = 0;
            p0.Board.Add(boardCard);
            p0.Graveyard.Add(MakeCard(6, p0Id, Zone.Graveyard));
            p0.Weapon = new WeaponInstance { Id = new InstanceId(7), DefinitionId = "weapon_seed", Attack = 3, Durability = 2 };
            p0.Mana = new ManaPool { Max = 5, Available = 3, Spent = 2, Locked = 1, Temporary = 1 };
            p0.HeroAttacksUsedThisTurn = 1;
            p0.NextTurnOverloadLock = 2;

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
                Kind = PendingChoiceKind.Discover,
                Source = new InstanceId(5),
                Options = { "disc_seed_a", "disc_seed_b" },
                ChoiceSequence = 11,
            };
            return s;
        }
    }
}
