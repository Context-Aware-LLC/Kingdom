using System.Collections.Generic;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;
using Kingdom.Core.Serialization;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Interpreter
{
    /// <summary>
    /// L1 — the M2 Phase 0 grammar-frost rulings (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md tier a):
    /// (1) GAIN_MANA permanent/empty payload semantics (the Quicken / Seedling Rite ramp primitive),
    /// (2) explicit-choice (ANY_*) selectors require a player-chosen target at the PlayCard door too,
    /// (3) chosen targets must fit the selector's shape (ANY_MINION can't aim at a hero),
    /// (4) the interpreter throws on selectors it cannot resolve instead of silently mistargeting.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class GrammarFrostTests
    {
        // ── scenario scaffolding (mirrors InterpreterTests) ──────────────────────

        private static GameState MainPhase(int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL), ps0, ps1)
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            ps0.Mana.Max = ps0.Mana.Available = p0Mana;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static CardInstance Hand(GameState s, int seat, int defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance Board(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            ps.Board.Add(ci);
            return ci;
        }

        private static CardDefinition Minion(int id, int cost, int atk, int hp,
            IReadOnlyList<RuleDefinition>? rules = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, cost, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules ?? System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, int cost, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, cost, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static PlayCardIntent Play(GameState s, CardInstance card, params InstanceId[] targets)
        {
            var refs = new TargetRef[targets.Length];
            for (int i = 0; i < targets.Length; i++) refs[i] = new TargetRef(targets[i]);
            return new PlayCardIntent(s.Players[0].Id, card.Id, 0, refs);
        }

        // ── GAIN_MANA permanent / empty (dev-docs/specs/card-schema.md §5.2) ────────────────────

        [Test]
        public void GainMana_Permanent_Empty_Raises_Max_But_Not_Available()
        {
            // Seedling Rite: "Gain an empty Mana Crystal."
            var rite = Spell(FixtureCard.Id("rite"), cost: 0, TestRules.Rule("gain_mana", Trigger.ON_CAST,
                new GainManaAction(1, ManaGainKind.PERMANENT_EMPTY)));
            var db = new FakeCardDatabase().Register(rite);
            var s = MainPhase(p0Mana: 5);
            var card = Hand(s, 0, FixtureCard.Id("rite"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Mana.Max, Is.EqualTo(6), "Earned a permanent crystal.");
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(5), "The new crystal arrives empty this turn.");
            Assert.That(s.Players[0].Mana.Temporary, Is.EqualTo(0), "Permanent gain is not temporary mana.");
        }

        [Test]
        public void GainMana_Permanent_Filled_Raises_Max_And_Available()
        {
            var ramp = Spell(FixtureCard.Id("ramp"), cost: 0, TestRules.Rule("gain_mana", Trigger.ON_CAST,
                new GainManaAction(1, ManaGainKind.PERMANENT_FILLED)));
            var db = new FakeCardDatabase().Register(ramp);
            var s = MainPhase(p0Mana: 5);
            var card = Hand(s, 0, FixtureCard.Id("ramp"));

            new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(s.Players[0].Mana.Max, Is.EqualTo(6));
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(6), "A filled crystal is spendable immediately.");
        }

        [Test]
        public void GainMana_Permanent_Caps_At_Ten_And_Stays_Silent_When_Nothing_Gained()
        {
            var rite = Spell(FixtureCard.Id("rite"), cost: 0, TestRules.Rule("gain_mana", Trigger.ON_CAST,
                new GainManaAction(1, ManaGainKind.PERMANENT_EMPTY)));
            var db = new FakeCardDatabase().Register(rite);
            var s = MainPhase(p0Mana: 10); // already at the cap
            var card = Hand(s, 0, FixtureCard.Id("rite"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(s.Players[0].Mana.Max, Is.EqualTo(10), "Crystals beyond the cap don't exist.");
            foreach (var ev in r.Events)
                Assert.That(ev.Kind, Is.Not.EqualTo(GameEventKind.ManaGained),
                    "A gain that changed nothing emits no ManaGained event.");
        }

        [Test]
        public void GainMana_Default_Is_Temporary_Spark_Pattern()
        {
            var coin = Spell(FixtureCard.Id("coin"), cost: 0, TestRules.Rule("gain_mana", Trigger.ON_CAST,
                new GainManaAction(1, ManaGainKind.TEMPORARY)));
            var db = new FakeCardDatabase().Register(coin);
            var s = MainPhase(p0Mana: 5);
            var card = Hand(s, 0, FixtureCard.Id("coin"));

            new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(s.Players[0].Mana.Max, Is.EqualTo(5), "Temporary mana never raises Max.");
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(6));
            Assert.That(s.Players[0].Mana.Temporary, Is.EqualTo(1));
        }

        // ── ANY_* explicit-choice gating at the PlayCard door ────────────────────

        [Test]
        public void PlayCard_With_AnySelector_And_No_Target_Is_Rejected_With_Zero_Mutation()
        {
            var bolt = Spell(FixtureCard.Id("bolt"), cost: 1, TestRules.Rule("damage", Trigger.ON_CAST,
                new DamageAction(3, new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(bolt);
            var s = MainPhase();
            var card = Hand(s, 0, FixtureCard.Id("bolt"));
            string before = GameStateHasher.Hash(s);

            var r = new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(before), "Reject means zero mutation.");
        }

        [Test]
        public void AnyMinion_Selector_Rejects_A_Hero_Target()
        {
            // Undertow's ANY_MINION cannot bounce a hero.
            var undertow = Spell(FixtureCard.Id("bounce"), cost: 2, TestRules.Rule("return", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(undertow);
            var s = MainPhase();
            var card = Hand(s, 0, FixtureCard.Id("bounce"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card, s.Players[1].Hero.Id));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void AnyFriendlyMinion_Selector_Rejects_An_Enemy_Minion_Target()
        {
            var bloom = Spell(FixtureCard.Id("bloom"), cost: 1, TestRules.Rule("modify", Trigger.ON_CAST,
                new ModifyStatsAction(2, 2, new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1)))));
            var vanilla = Minion(FixtureCard.Id("vanilla"), cost: 1, atk: 1, hp: 1);
            var db = new FakeCardDatabase().Register(bloom).Register(vanilla);
            var s = MainPhase();
            var enemy = Board(s, 1, vanilla);
            var card = Hand(s, 0, FixtureCard.Id("bloom"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card, enemy.Id));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void AnyFriendlyMinion_Selector_Accepts_A_Friendly_Minion_Target()
        {
            var bloom = Spell(FixtureCard.Id("bloom"), cost: 1, TestRules.Rule("modify", Trigger.ON_CAST,
                new ModifyStatsAction(2, 2, new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1)))));
            var vanilla = Minion(FixtureCard.Id("vanilla"), cost: 1, atk: 1, hp: 1);
            var db = new FakeCardDatabase().Register(bloom).Register(vanilla);
            var s = MainPhase();
            var friendly = Board(s, 0, vanilla);
            var card = Hand(s, 0, FixtureCard.Id("bloom"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card, friendly.Id));

            Assert.That(r.Accepted, Is.True);
            Assert.That(friendly.CurrentAttack, Is.EqualTo(3), "+2/+2 landed on the chosen friendly minion.");
            Assert.That(friendly.CurrentHealth, Is.EqualTo(3));
        }

        // ── SELF and contextual selectors resolve through the shared seam ────────

        [Test]
        public void Self_Selector_On_A_Fired_Trigger_Buffs_The_Source()
        {
            // ON_FRIENDLY_SUMMON observer with SELF selector: the observer buffs itself, never
            // the verb-default. (Before the grammar frost SELF silently fell into DefaultTargets.)
            var cheer = Minion(FixtureCard.Id("cheer"), cost: 1, atk: 1, hp: 1, new[]
            {
                TestRules.Rule("modify_self", Trigger.ON_FRIENDLY_SUMMON,
                    new ModifyStatsAction(1, 0, new TargetSpec("SELF", TargetSelection.Of(1)))),
            });
            var vanilla = Minion(FixtureCard.Id("vanilla"), cost: 1, atk: 1, hp: 1);
            var db = new FakeCardDatabase().Register(cheer).Register(vanilla);
            var s = MainPhase();
            var observer = Board(s, 0, cheer);
            var card = Hand(s, 0, FixtureCard.Id("vanilla"));

            var r = new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(r.Accepted, Is.True);
            Assert.That(observer.CurrentAttack, Is.EqualTo(2), "SELF resolved to the observing source.");
        }

        [Test]
        public void Adjacent_On_A_Departed_Death_Source_Resolves_To_No_Targets()
        {
            // Once the source has left the board it has no live neighbors. The selector therefore
            // resolves to an empty set instead of falling through to the damage default.
            var trap = Minion(FixtureCard.Id("trap"), cost: 1, atk: 1, hp: 1, new[]
            {
                TestRules.Rule("damage_adjacent", Trigger.ON_DEATH,
                    new DamageAction(1, new TargetSpec("ADJACENT", TargetSelection.AllOf("ADJACENT")))),
            });
            var vanilla = Minion(FixtureCard.Id("vanilla"), cost: 1, atk: 1, hp: 1);
            var db = new FakeCardDatabase().Register(trap).Register(vanilla);
            var s = MainPhase();
            var doomed = Board(s, 1, trap);
            doomed.CurrentHealth = 0; // next Drain's DeathCheck fires the toll
            var card = Hand(s, 0, FixtureCard.Id("vanilla"));

            var result = new CommandProcessor(db).Execute(s, Play(s, card));

            Assert.That(result.Accepted, Is.True);
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(30));
        }
    }
}
