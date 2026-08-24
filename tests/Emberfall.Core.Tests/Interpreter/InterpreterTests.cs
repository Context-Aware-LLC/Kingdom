using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.GoldenReplay;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Interpreter
{
    /// <summary>
    /// L1 — the effect interpreter (M1 Phase B step 1) driven through the real public
    /// <c>CommandProcessor.Execute</c> door: PlayCard → herald/cast → DeathCheck → toll cascade.
    /// Constructs a Main-phase <see cref="GameState"/> directly so a known card sits in hand, then asserts
    /// on the public state + the ordered semantic event stream.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class InterpreterTests
    {
        // ── scenario scaffolding ─────────────────────────────────────────────────

        private static GameState MainPhase(FakeCardDatabase db, int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL), ps0, ps1)
            {
                NextInstanceCounter = 3,   // heroes consumed 1,2 (0 is the reserved sentinel, D-P1)
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
            if (ci.Keywords.Contains(Keyword.AEGIS)) ci.HasAegisShield = true;
            ps.Board.Add(ci);
            return ci;
        }

        private static RuleDefinition Damage(Trigger trigger, int amount)
            => TestRules.Rule("rule_1", trigger, TestRules.Damage(trigger, amount, TargetSelection.Of(1)));

        private static RuleDefinition Summon(Trigger trigger, int count, int cardId)
            => TestRules.Rule("rule_1", trigger, new SummonAction(cardId, count));

        private static RuleDefinition ModifyStats(Trigger trigger, int attack, int health)
            => TestRules.Rule("rule_1", trigger, TestRules.ModifyStats(trigger, attack, health, TargetSelection.Of(1)));

        private static RuleDefinition GrantKeyword(Trigger trigger, Keyword keyword)
            => TestRules.Rule("rule_1", trigger, new GrantKeywordAction(keyword,
                new TargetSpec(trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_MINION" : "SELF", TargetSelection.Of(1))));

        private static CardDefinition Minion(int id, int cost, int atk, int hp,
            IReadOnlyList<Keyword>? keywords = null, IReadOnlyList<RuleDefinition>? rules = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, cost, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), rules ?? System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, int cost, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, cost, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CommandProcessor Processor(FakeCardDatabase db) => new CommandProcessor(db);

        // ── tests ────────────────────────────────────────────────────────────────

        [Test]
        public void Play_Minion_Enters_Board_And_Pays_Mana()
        {
            var db = new FakeCardDatabase().Register(Minion(FixtureCard.Id("vanilla"), cost: 3, atk: 2, hp: 4));
            var s = MainPhase(db, p0Mana: 5);
            var card = Hand(s, 0, FixtureCard.Id("vanilla"));

            var r = Processor(db).Execute(s, new PlayCardIntent(s.Players[0].Id, card.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1));
            Assert.That(s.Players[0].Board[0].CurrentAttack, Is.EqualTo(2));
            Assert.That(s.Players[0].Board[0].CurrentHealth, Is.EqualTo(4));
            Assert.That(s.Players[0].Hand, Is.Empty);
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(2), "5 mana − 3 cost.");
        }

        [Test]
        public void NotEnoughMana_Rejects_With_Zero_Mutation()
        {
            var db = new FakeCardDatabase().Register(Minion(FixtureCard.Id("big"), cost: 8, atk: 8, hp: 8));
            var s = MainPhase(db, p0Mana: 3);
            var card = Hand(s, 0, FixtureCard.Id("big"));
            string before = GameStateHasher.Hash(s);

            var r = Processor(db).Execute(s, new PlayCardIntent(s.Players[0].Id, card.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.NotEnoughMana));
            Assert.That(r.Events, Is.Empty);
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(before));
        }

        [Test]
        public void Herald_DealDamage_To_Chosen_Target_Kills_Minion()
        {
            var db = new FakeCardDatabase().Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1,
                rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }));
            var s = MainPhase(db);
            var victim = Board(s, 1, Minion(FixtureCard.Id("dummy"), 1, 2, 2));   // enemy 2/2
            var card = Hand(s, 0, FixtureCard.Id("bolt"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(victim.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Board, Is.Empty, "3 damage to a 2/2 kills it.");
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(1));
        }

        [Test]
        public void Toll_Summons_Token_When_Minion_Dies()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }))
                .Register(Minion(FixtureCard.Id("rattler"), 1, 1, 1,
                    rules: new[] { Summon(Trigger.ON_DEATH, 1, FixtureCard.Id("token_imp")) }));
            var s = MainPhase(db);
            var victim = Board(s, 1, db.Get(FixtureCard.Id("rattler")));
            var card = Hand(s, 0, FixtureCard.Id("bolt"));

            Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(victim.Id) }));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "Toll summoned a token.");
            Assert.That(s.Players[1].Board[0].DefinitionId, Is.EqualTo(FixtureCard.Id("token_imp")));
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(1));
        }

        [Test]
        public void Toll_Cascade_Effect_Then_Death_Then_Effect()
        {
            // bolt(P0) kills enemy 'pinger'; its toll DEAL_DAMAGE defaults to the dying minion's enemy
            // hero = P0. Tests effect → death → toll-effect chaining through one Drain.
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }))
                .Register(Minion(FixtureCard.Id("pinger"), 1, 1, 1,
                    rules: new[] { Damage(Trigger.ON_DEATH, amount: 2) }));
            var s = MainPhase(db);
            var victim = Board(s, 1, db.Get(FixtureCard.Id("pinger")));
            var card = Hand(s, 0, FixtureCard.Id("bolt"));

            Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(victim.Id) }));

            Assert.That(s.Players[1].Board, Is.Empty);
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(28), "Toll pinged the caster's hero for 2.");
        }

        [Test]
        public void Two_Simultaneous_Deaths_Both_Fire_Tolls_In_Board_Order()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("vanilla"), 0, 1, 1))
                .Register(Minion(FixtureCard.Id("rattler"), 1, 1, 1,
                    rules: new[] { Summon(Trigger.ON_DEATH, 1, FixtureCard.Id("token_imp")) }));
            var s = MainPhase(db);
            // Two enemy rattlers already at lethal — the next command must settle BOTH as one batch.
            var a = Board(s, 1, db.Get(FixtureCard.Id("rattler")));
            var b = Board(s, 1, db.Get(FixtureCard.Id("rattler")));
            a.CurrentHealth = 0;
            b.CurrentHealth = 0;
            var card = Hand(s, 0, FixtureCard.Id("vanilla"));

            var r = Processor(db).Execute(s, new PlayCardIntent(s.Players[0].Id, card.Id, 0, System.Array.Empty<TargetRef>()));

            // Both originals dead and replaced by their summoned tokens.
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(2));
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(2));
            foreach (var m in s.Players[1].Board)
                Assert.That(m.DefinitionId, Is.EqualTo(FixtureCard.Id("token_imp")));

            // CardDestroyed facts present and in ascending sequence (deterministic order).
            ulong lastDeathSeq = 0; int deaths = 0;
            foreach (var e in r.Events)
                if (e.Kind == GameEventKind.CardDestroyed) { Assert.That(e.Sequence, Is.GreaterThan(lastDeathSeq)); lastDeathSeq = e.Sequence; deaths++; }
            Assert.That(deaths, Is.EqualTo(2));
        }

        [Test]
        public void DivineShield_Absorbs_The_First_Hit_Only()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }));
            var s = MainPhase(db);
            var shielded = Board(s, 1, Minion(FixtureCard.Id("guard"), 1, 2, 2, keywords: new[] { Keyword.AEGIS }));
            var card = Hand(s, 0, FixtureCard.Id("bolt"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(shielded.Id) }));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "Aegis absorbs the hit; the minion survives.");
            Assert.That(s.Players[1].Board[0].HasAegisShield, Is.False, "Shield is consumed.");
            Assert.That(s.Players[1].Board[0].CurrentHealth, Is.EqualTo(2), "No health lost.");
            bool absorbed = false;
            foreach (var e in r.Events)
                if (e.Kind == GameEventKind.DamageApplied && (e.Flags & EventFlags.AegisAbsorbed) != 0) absorbed = true;
            Assert.That(absorbed, Is.True);
        }

        [Test]
        public void Buff_Herald_Raises_Target_Stats()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("buffer"), 1, 1, 1, rules: new[] { ModifyStats(Trigger.ON_PLAY, 2, 3) }));
            var s = MainPhase(db);
            var ally = Board(s, 0, Minion(FixtureCard.Id("ally"), 1, 1, 1));
            var card = Hand(s, 0, FixtureCard.Id("buffer"));

            Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 1, new[] { new TargetRef(ally.Id) }));

            Assert.That(ally.CurrentAttack, Is.EqualTo(3));
            Assert.That(ally.CurrentHealth, Is.EqualTo(4));
            Assert.That(ally.MaxHealth, Is.EqualTo(4));
            Assert.That(ally.Enchantments, Has.Count.EqualTo(1));
        }

        [Test]
        public void GiveKeyword_DivineShield_Grants_A_Live_Shield()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("blesser"), 1, 1, 1,
                    rules: new[] { GrantKeyword(Trigger.ON_PLAY, Keyword.AEGIS) }));
            var s = MainPhase(db);
            var ally = Board(s, 0, Minion(FixtureCard.Id("ally"), 1, 1, 1));
            var card = Hand(s, 0, FixtureCard.Id("blesser"));

            Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 1, new[] { new TargetRef(ally.Id) }));

            Assert.That(ally.Keywords, Does.Contain(Keyword.AEGIS));
            Assert.That(ally.HasAegisShield, Is.True);
        }

        [TestCase(Keyword.BACKDRAFT)]
        [TestCase(Keyword.REINFORCE)]
        [TestCase(Keyword.SILENCE)]
        public void GiveKeyword_Fails_Closed_For_Action_Only_Markers(Keyword marker)
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("marker_granter"), 1, 1, 1,
                    rules: new[] { GrantKeyword(Trigger.ON_PLAY, marker) }));
            GameState state = MainPhase(db);
            CardInstance ally = Board(state, 0, Minion(FixtureCard.Id("ally"), 1, 1, 1));
            CardInstance source = Hand(state, 0, FixtureCard.Id("marker_granter"));

            CommandResult result = Processor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, source.Id, 1, new[] { new TargetRef(ally.Id) }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(ally.Keywords, Does.Not.Contain(marker));
            Assert.That(ally.Enchantments, Is.Empty);
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.KeywordApplied), Is.False);
        }

        [TestCase(Keyword.BACKDRAFT)]
        [TestCase(Keyword.REINFORCE)]
        [TestCase(Keyword.SILENCE)]
        public void Printed_Action_Markers_Do_Not_Enter_Minion_Runtime_State(Keyword marker)
        {
            CardDefinition definition = Minion(FixtureCard.Id("legacy_marker_minion"), 1, 1, 1,
                keywords: new[] { marker });
            var db = new FakeCardDatabase().Register(definition);
            GameState state = MainPhase(db);
            CardInstance source = Hand(state, 0, definition.Id);

            CommandResult result = Processor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, source.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Board.Single().Keywords, Does.Not.Contain(marker));
        }

        [Test]
        public void BoardFull_Rejects_Minion_Play()
        {
            var db = new FakeCardDatabase().Register(Minion(FixtureCard.Id("vanilla"), 0, 1, 1));
            var s = MainPhase(db);
            for (int i = 0; i < MatchEngine.BoardCap; i++) Board(s, 0, db.Get(FixtureCard.Id("vanilla")));
            var card = Hand(s, 0, FixtureCard.Id("vanilla"));

            var r = Processor(db).Execute(s, new PlayCardIntent(s.Players[0].Id, card.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.BoardFull));
        }

        [Test]
        public void Lethal_Herald_Ends_The_Match()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("nuke"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 30) }));
            var s = MainPhase(db);
            var card = Hand(s, 0, FixtureCard.Id("nuke"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }

        [Test]
        public void IllegalTarget_On_Enemy_Shroud_Is_Rejected()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }));
            var s = MainPhase(db);
            var hidden = Board(s, 1, Minion(FixtureCard.Id("sneak"), 1, 1, 1, keywords: new[] { Keyword.SHROUD }));
            var card = Hand(s, 0, FixtureCard.Id("bolt"));

            var r = Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(hidden.Id) }));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void Spell_Cast_Resolves_Its_Effect_And_Goes_To_Graveyard()
        {
            var db = new FakeCardDatabase()
                .Register(Spell(FixtureCard.Id("zap"), 1, Damage(Trigger.ON_CAST, amount: 5)));
            var s = MainPhase(db);
            var card = Hand(s, 0, FixtureCard.Id("zap"));

            Processor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(25));
            Assert.That(s.Players[0].Graveyard, Has.Count.EqualTo(1));
        }

        [Test]
        public void Same_Inputs_Produce_Identical_State_Hash()
        {
            string Run()
            {
                var db = new FakeCardDatabase()
                    .Register(Minion(FixtureCard.Id("bolt"), 1, 1, 1, rules: new[] { Damage(Trigger.ON_PLAY, amount: 3) }))
                    .Register(Minion(FixtureCard.Id("rattler"), 1, 1, 1,
                        rules: new[] { Summon(Trigger.ON_DEATH, 1, FixtureCard.Id("token_imp")) }));
                var s = MainPhase(db);
                var victim = Board(s, 1, db.Get(FixtureCard.Id("rattler")));
                var card = Hand(s, 0, FixtureCard.Id("bolt"));
                Processor(db).Execute(s, new PlayCardIntent(
                    s.Players[0].Id, card.Id, 0, new[] { new TargetRef(victim.Id) }));
                return GameStateHasher.Hash(s);
            }
            string first = Run();
            string second = Run();
            Assert.That(second, Is.EqualTo(first));
        }
    }
}
