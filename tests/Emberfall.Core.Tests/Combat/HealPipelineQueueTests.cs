using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Combat
{
    /// <summary>
    /// EF-015 — <see cref="DamagePipeline.ApplyHeal"/> raises the healed minion's ON_HEAL onto the
    /// interpreter's queue, <b>enqueue-only</b>, exactly as EF-013/EF-014 did for ON_DAMAGED at the sibling
    /// site. These pin the four properties of that raise which the golden fixture cannot reach on its own:
    /// <list type="number">
    /// <item>a real heal fires the healed minion's own ON_HEAL, once;</item>
    /// <item>an <b>ineffective</b> heal (target already at max) fires nothing — the no-op is not the event,
    ///   the same precedent as an Aegis-absorbed hit not counting as "damaged";</item>
    /// <item>it is a <b>SELF</b> trigger, not an observer sweep: a bystander minion subscribed to ON_HEAL
    ///   does not react to somebody else being healed;</item>
    /// <item>the <b>reentrant LIFEDRAIN heal inside ApplyDamage</b> raises nothing, because its target is
    ///   always a hero and heroes carry no card effects.</item>
    /// </list>
    /// Driven through the public <c>CommandProcessor.Execute</c> door; no golden fixtures. Cross-target
    /// ordering — every heal in a sweep landing before any of the reactions — is pinned by the
    /// <c>on_heal_wardsong</c> golden's event stream instead.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HealPipelineQueueTests
    {
        // ── scenario scaffolding (mirrors DamagePipelineQueueTests) ──────────────

        private static GameState MainPhase()
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
            ps0.Mana.Max = ps0.Mana.Available = 10;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static CardInstance Hand(GameState s, int seat, string defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance Board(GameState s, int seat, CardDefinition def, int? currentHealth = null)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = currentHealth ?? def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            ci.HasAegisShield = ci.Keywords.Contains(Keyword.AEGIS);
            ps.Board.Add(ci);
            return ci;
        }

        private static RuleDefinition Damage(Trigger trigger, int amount, string? selector = null)
            => TestRules.Rule("rule_1", trigger, TestRules.Damage(trigger, amount, selector));

        private static RuleDefinition Heal(Trigger trigger, int amount, string? selector = null)
            => TestRules.Rule("rule_1", trigger, TestRules.Heal(trigger, amount, selector));

        private static CardDefinition Minion(string id, int atk, int hp, System.Collections.Generic.IReadOnlyList<RuleDefinition>? rules = null,
            System.Collections.Generic.IReadOnlyList<Keyword>? keywords = null)
            => new CardDefinition(id, id, CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), rules ?? System.Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE, true, null, "");

        private static CardDefinition Spell(string id, params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + id, SetId.CORE, true, null, "");

        /// <summary>A minion that pings the enemy hero for 3 whenever THIS minion is healed.</summary>
        private static CardDefinition Wardsong() => Minion("wardsong", 1, 10,
            rules: new[] { Damage(Trigger.ON_HEAL, amount: 3, selector: "ENEMY_HERO") });

        private static CommandProcessor Processor(FakeCardDatabase db) => new CommandProcessor(db);

        // ── tests ────────────────────────────────────────────────────────────────

        [Test]
        public void Effective_Heal_Fires_The_Healed_Minions_ON_HEAL_Once()
        {
            // The raise exists. A wounded minion is topped up by one spell and pings back exactly once —
            // not zero times (no raise site), not once per drain step.
            var db = new FakeCardDatabase()
                .Register(Spell("salve", Heal(Trigger.ON_CAST, amount: 4)))
                .Register(Wardsong());
            var s = MainPhase();
            var ward = Board(s, 0, db.Get("wardsong"), currentHealth: 6);   // friendly, wounded 6/10
            var salve = Hand(s, 0, "salve");

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, salve.Id, 0, new[] { new TargetRef(ward.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(ward.CurrentHealth, Is.EqualTo(10), "the salve restored all 4 missing health.");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27),
                "ON_HEAL fired exactly once: a single 3-damage ping at the healed minion's enemy hero.");
        }

        [Test]
        public void Heal_Is_Clamped_And_A_Full_Health_Target_Raises_Nothing()
        {
            // The no-op guard (DamagePipeline.ApplyHeal's `healed <= 0` early return) sits ABOVE the raise.
            // Same card as the test above, this time at full health: the spell is legal and resolves, but
            // nothing was restored — so nothing was healed, and no ping comes back. This is the heal-side
            // twin of "an Aegis-absorbed hit is not damaged", and the only place it is enforced.
            var db = new FakeCardDatabase()
                .Register(Spell("salve", Heal(Trigger.ON_CAST, amount: 4)))
                .Register(Wardsong());
            var s = MainPhase();
            var ward = Board(s, 0, db.Get("wardsong"));   // friendly, untouched 10/10
            var salve = Hand(s, 0, "salve");

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, salve.Id, 0, new[] { new TargetRef(ward.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(ward.CurrentHealth, Is.EqualTo(10), "clamped to max — no overheal above MaxHealth.");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30),
                "ON_HEAL did NOT fire: zero health restored is not a heal.");
        }

        [Test]
        public void ON_HEAL_Is_A_Self_Trigger_Not_An_Observer_Sweep()
        {
            // EF-015's shape decision, enforced. Two wardsongs are on the board and only ONE is healed.
            // A self trigger pings once; an observer sweep (the RaiseObservers shape ON_SPELL_CAST and
            // ON_TURN_END use) would ping twice, because the bystander subscribes to the same trigger.
            // Rewriting the raise as RaiseObservers fails here and nowhere else.
            var db = new FakeCardDatabase()
                .Register(Spell("salve", Heal(Trigger.ON_CAST, amount: 4)))
                .Register(Wardsong());
            var s = MainPhase();
            var healed = Board(s, 0, db.Get("wardsong"), currentHealth: 6);
            var bystander = Board(s, 0, db.Get("wardsong"), currentHealth: 6);
            var salve = Hand(s, 0, "salve");

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, salve.Id, 0, new[] { new TargetRef(healed.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(healed.CurrentHealth, Is.EqualTo(10), "the targeted wardsong was topped up…");
            Assert.That(bystander.CurrentHealth, Is.EqualTo(6), "…and the bystander was not touched.");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27),
                "EXACTLY ONE ping. ON_HEAL is 'whenever THIS minion is healed', not 'whenever anything is healed'.");
        }

        [Test]
        public void Lifedrain_Heal_Inside_ApplyDamage_Raises_No_ON_HEAL()
        {
            // The reentrant path the EF-015 brief flags: ApplyHeal is called from INSIDE ApplyDamage when
            // the damage source has LIFEDRAIN. Under self semantics that heal targets a HERO, and heroes
            // carry no card effects — so the reentrancy raises nothing at all today. Pinned explicitly
            // rather than left implied: it is the difference between "deliberately silent" and "forgot".
            //
            // Setup: P0's hero is wounded so the drain has something to restore; a friendly wardsong
            // watches from the board and must stay silent, proving no ON_HEAL leaked out of the hero heal.
            var db = new FakeCardDatabase()
                .Register(Spell("drainbolt", Damage(Trigger.ON_CAST, amount: 4)))
                .Register(Minion("vampire", 1, 10, keywords: new[] { Keyword.LIFEDRAIN }))
                .Register(Wardsong());
            var s = MainPhase();
            s.Players[0].Hero.CurrentHealth = 20;
            var vampire = Board(s, 0, db.Get("vampire"));
            var watcher = Board(s, 0, db.Get("wardsong"), currentHealth: 6);
            var victim = Board(s, 1, db.Get("vampire"));

            // The spell itself is sourceless of keywords; drive LIFEDRAIN through the minion instead by
            // attacking with it — that is the only path that carries attacker keywords into ApplyDamage.
            vampire.SummonedThisTurn = false;
            var r = Processor(db).Execute(s, new AttackIntent(s.Players[0].Id, vampire.Id, new TargetRef(victim.Id)));

            Assert.That(r.Accepted, Is.True, $"attack rejected: {r.Reason}");
            Assert.That(victim.CurrentHealth, Is.EqualTo(9), "the vampire's 1 attack landed.");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(21),
                "LIFEDRAIN restored 1 to the attacker's hero — the reentrant ApplyHeal really ran.");
            Assert.That(watcher.CurrentHealth, Is.EqualTo(6),
                "precondition: the watcher was never healed itself.");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30),
                "no ON_HEAL escaped the hero heal: a hero has no card effects, and the raise is self-only.");
        }
    }
}
