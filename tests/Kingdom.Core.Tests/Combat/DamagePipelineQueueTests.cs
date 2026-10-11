using System.Collections.Generic;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Combat
{
    /// <summary>
    /// EF-013 — <see cref="DamagePipeline"/> now holds the <see cref="EffectInterpreter"/> (the queue) and
    /// raises a damaged minion's ON_DAMAGED onto it, <b>enqueue-only</b> (no drain between hits — see the
    /// decision recorded in <c>DamagePipeline.cs</c>). These tests pin three things about that:
    /// (1) an on-damage trigger actually fires on a normal hit, (2) an <b>Aegis-absorbed</b> hit fires nothing
    /// — the pipeline's two damage sites, EF-014 — and (3) a mutual damage→counter-damage cycle
    /// <b>terminates</b>, bounded by <see cref="MatchEngine.ActionResolutionCap"/> in the interpreter's single
    /// <see cref="EffectInterpreter.Drain"/> loop rather than recursing without bound.
    /// Driven through the public <c>CommandProcessor.Execute</c> door; no golden fixtures.
    /// The cross-hit ordering these leave untested — both volley hits landing before any retaliation —
    /// is pinned by the <c>on_damaged_thorns</c> golden's event stream (EF-014).
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DamagePipelineQueueTests
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
            // Mirror the real summon path (RulesEngine.cs:67), which derives the shield from the
            // keyword rather than leaving it false — otherwise a AEGIS def boards unshielded
            // and an Aegis test would silently pass for the wrong reason.
            ci.HasAegisShield = ci.Keywords.Contains(Keyword.AEGIS);
            ps.Board.Add(ci);
            return ci;
        }

        /// <summary>
        /// <paramref name="selection"/> defaults to <c>Of(1)</c>, which on the unique selectors used
        /// here (ENEMY_HERO) is the same as naming every candidate -- nobody chooses. On a selector
        /// that can yield more than one it is the CONTROLLER choosing, and a reaction trigger has no
        /// controller to ask, so the effect resolves to nothing. Say AllOf for those.
        /// </summary>
        private static RuleDefinition Damage(Trigger trigger, int amount, string? selector = null,
            TargetSelection? selection = null)
            => TestRules.Rule("rule_1", trigger,
                TestRules.Damage(trigger, amount, selection ?? TargetSelection.Of(1), selector));

        private static CardDefinition Minion(int id, int atk, int hp, IReadOnlyList<RuleDefinition>? rules = null,
            IReadOnlyList<Keyword>? keywords = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), rules ?? System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CommandProcessor Processor(FakeCardDatabase db) => new CommandProcessor(db);

        // ── tests ────────────────────────────────────────────────────────────────

        [Test]
        public void Normal_Hit_Fires_The_Targets_ON_DAMAGED_Once()
        {
            // A minion whose ON_DAMAGED pings the enemy hero. One spell hit → exactly one retaliatory ping,
            // proving the raise is wired and fires once per hit (not per drain, not zero times).
            var db = new FakeCardDatabase()
                .Register(Spell(FixtureCard.Id("poke"), Damage(Trigger.ON_CAST, amount: 1)))
                .Register(Minion(FixtureCard.Id("stinger"), 1, 10,
                    rules: new[] { Damage(Trigger.ON_DAMAGED, amount: 3, selector: "ENEMY_HERO") }));
            var s = MainPhase();
            var stinger = Board(s, 1, db.Get(FixtureCard.Id("stinger")));   // enemy minion, stinger's enemy hero = P0
            var poke = Hand(s, 0, FixtureCard.Id("poke"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, poke.Id, 0, new[] { new TargetRef(stinger.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(stinger.CurrentHealth, Is.EqualTo(9), "stinger took the 1-damage poke.");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(27),
                "ON_DAMAGED fired exactly once: a single 3-damage ping back at the poking hero.");
        }

        [Test]
        public void Aegis_Absorbed_Hit_Raises_No_ON_DAMAGED()
        {
            // EF-014 site 2 — the Aegis early return (DamagePipeline.cs:60-65). The SAME minion as the test
            // above, plus AEGIS: the shield eats the hit, so the minion was never "damaged" and its
            // ON_DAMAGED must not fire (G0 ruling R-2(c), "no on-hit riders" on an absorbed hit).
            //
            // This is the only place that decision is enforced rather than merely commented. Deleting the
            // early return, or moving the raise above it, makes the enemy hero take 3 and fails here.
            var db = new FakeCardDatabase()
                .Register(Spell(FixtureCard.Id("poke"), Damage(Trigger.ON_CAST, amount: 1)))
                .Register(Minion(FixtureCard.Id("wardstinger"), 1, 10,
                    rules: new[] { Damage(Trigger.ON_DAMAGED, amount: 3, selector: "ENEMY_HERO") },
                    keywords: new[] { Keyword.AEGIS }));
            var s = MainPhase();
            var warded = Board(s, 1, db.Get(FixtureCard.Id("wardstinger")));   // enemy minion; its enemy hero = P0
            var poke = Hand(s, 0, FixtureCard.Id("poke"));

            Assert.That(warded.HasAegisShield, Is.True, "precondition: it boarded with its shield up.");

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, poke.Id, 0, new[] { new TargetRef(warded.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(warded.CurrentHealth, Is.EqualTo(10), "Aegis absorbed the whole hit — no health lost.");
            Assert.That(warded.HasAegisShield, Is.False, "…and the shield is spent.");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(30),
                "ON_DAMAGED did NOT fire: an absorbed hit is not 'damaged', so no 3-damage ping came back.");
        }

        [Test]
        public void CounterDamage_Loop_Terminates_At_The_Action_Resolution_Cap()
        {
            // Two minions, one per side, each of which retaliates ON_DAMAGED by dealing 1 to the OTHER
            // (ENEMY_MINIONS). A single poke lights the fuse: X hurt → hits Y → Y hurt → hits X → … forever.
            // Health (200) is set high enough that neither dies before the cap, so if this loop terminates it
            // can only be because EffectInterpreter.Drain stopped enqueuing at ActionResolutionCap — the one
            // recursion bound in the engine. Without it this test would recurse/spin without end.
            const int cap = MatchEngine.ActionResolutionCap;
            var db = new FakeCardDatabase()
                .Register(Spell(FixtureCard.Id("poke"), Damage(Trigger.ON_CAST, amount: 1)))
                .Register(Minion(FixtureCard.Id("echo"), 1, 200,
                    rules: new[] { Damage(Trigger.ON_DAMAGED, amount: 1, selector: "ANY_ENEMY_MINION",
                        selection: TargetSelection.AllOf("ANY_ENEMY_MINION")) }));
            var s = MainPhase();
            var x = Board(s, 0, db.Get(FixtureCard.Id("echo")));
            var y = Board(s, 1, db.Get(FixtureCard.Id("echo")));
            var poke = Hand(s, 0, FixtureCard.Id("poke"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, poke.Id, 0, new[] { new TargetRef(y.Id) }));

            // Reaching here at all is the headline: the command RETURNED — the loop did not run forever.
            Assert.That(r.Accepted, Is.True);
            Assert.That(s.ActionResolutionsThisTurn, Is.EqualTo(cap + 1),
                "the drain broke out on the resolution that first exceeded the cap.");
            Assert.That(x.CurrentHealth, Is.GreaterThan(0), "termination was by the cap, not by X dying.");
            Assert.That(y.CurrentHealth, Is.GreaterThan(0), "termination was by the cap, not by Y dying.");
            Assert.That(x.CurrentHealth + y.CurrentHealth, Is.LessThan(400),
                "the loop really ran: both minions bled before the cap fired.");
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.InProgress), "nobody was killed by the runaway.");
        }
    }
}
