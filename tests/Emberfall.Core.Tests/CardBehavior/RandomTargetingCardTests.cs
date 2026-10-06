using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardBehavior
{
    [TestFixture]
    [Category("Unit")]
    public sealed class RandomTargetingCardTests
    {
        private static JsonCardDatabase _db = null!;

        [OneTimeSetUp]
        public void LoadLiveCards()
        {
            _db = new JsonCardDatabase(ShippedCards.Cards);
        }

        private static GameState State(ulong seed = 42)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("random-targets"), new DeterministicRandom(seed),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.TEMPEST, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.NIGHTVEIL, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardInstance Board(GameState state, int seat, int id, int health = 10)
        {
            CardDefinition def = _db.Get(id);
            var card = new CardInstance(state.NextInstanceId(), id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 1,
                CurrentHealth = health,
                MaxHealth = health,
                Position = state.Players[seat].Board.Count,
            };
            foreach (Keyword keyword in def.Keywords) card.Keywords.Add(keyword);
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CardInstance Hand(GameState state, int id)
        {
            var card = new CardInstance(state.NextInstanceId(), id, state.Players[0].Id) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            return card;
        }

        private static PlayCardIntent Play(GameState state, CardInstance card, params InstanceId[] targets)
            => new PlayCardIntent(state.Players[0].Id, card.Id, 0,
                targets.Select(id => new TargetRef(id)).ToArray());

        [Test]
        public void ChainSpark_Hits_Two_Distinct_Random_Enemies()
        {
            var state = State(seed: 7);
            state.Players[0].Hero.HeroPowerId = 8;  // hero_power_chain_spark
            CardInstance a = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance b = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            int heroBefore = state.Players[1].Hero.CurrentHealth;

            var result = new CommandProcessor(_db).Execute(state,
                new HeroPowerIntent(state.Players[0].Id, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            int[] losses =
            {
                heroBefore - state.Players[1].Hero.CurrentHealth,
                10 - a.CurrentHealth,
                10 - b.CurrentHealth,
            };
            Assert.That(losses.Sum(), Is.EqualTo(2));
            Assert.That(losses.Count(loss => loss == 1), Is.EqualTo(2), "'another' forbids a repeat hit.");
        }

        // Death Mark's random Venom grant was cut 2026-08-25 (Sam, the Nightveil Venom pass) and
        // its test went with it: the card is "Destroy a minion." now, and no row in the corpus
        // pairs a random selection with GRANT_KEYWORD any more. Random MODIFY_STATS is still
        // covered by Stomp below, random DAMAGE by Chain Lightning and Chain Spark above.

        [Test]
        public void Stomp_Hits_All_Enemies_Then_Buffs_One_Random_Friendly_Minion()
        {
            var state = State(seed: 13);
            CardInstance allyA = Board(state, 0, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance allyB = Board(state, 0, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance enemyA = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance enemyB = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance spell = Hand(state, ShippedCards.Id("warden_stomp"));

            var result = new CommandProcessor(_db).Execute(state, Play(state, spell));

            Assert.That(result.Accepted, Is.True);
            Assert.That(new[] { enemyA.CurrentHealth, enemyB.CurrentHealth }, Is.EqualTo(new[] { 8, 8 }));
            Assert.That(new[] { allyA.MaxHealth, allyB.MaxHealth }.OrderBy(x => x),
                Is.EqualTo(new[] { 6, 10 }));
        }

        [TestCase("storm_crackle", 4)]
        [TestCase("storm_static_shock", 3)]
        public void SingleTarget_Random_Damage_Cards_Hit_Exactly_One_Enemy(
            string handle, int damage)
        {
            int cardId = ShippedCards.Id(handle);
            var state = State(seed: 19);
            CardInstance a = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance b = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance spell = Hand(state, cardId);
            int heroBefore = state.Players[1].Hero.CurrentHealth;

            var result = new CommandProcessor(_db).Execute(state, Play(state, spell));

            Assert.That(result.Accepted, Is.True);
            int[] losses =
            {
                heroBefore - state.Players[1].Hero.CurrentHealth,
                10 - a.CurrentHealth,
                10 - b.CurrentHealth,
            };
            Assert.That(losses.Sum(), Is.EqualTo(damage));
            Assert.That(losses.Count(loss => loss == damage), Is.EqualTo(1));
        }

        [Test]
        public void MemorialWisp_Is_Live_And_Reinforces_One_Random_Friendly_Minion_On_Death()
        {
            var state = State(seed: 23);
            CardDefinition wispDef = _db.Get(ShippedCards.Id("grave_memorial_wisp"));
            // The Wisp was the last row held behind requiresEngineeringPrimitive; it was released
            // when the seeded RANDOM target primitive landed, and the flag itself retired in
            // NOR-224. PoolEligible is the surviving witness that this row reaches live pools.
            Assert.That(wispDef.PoolEligible, Is.True);
            CardInstance allyA = Board(state, 0, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance allyB = Board(state, 0, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance wisp = Board(state, 0, wispDef.Id, health: 1);
            wisp.CurrentHealth = 0;

            var result = new CommandProcessor(_db).Execute(state,
                new EndTurnIntent(state.Players[0].Id));

            Assert.That(result.Accepted, Is.True);
            Assert.That(new[] { allyA.CurrentAttack, allyB.CurrentAttack }.OrderBy(x => x),
                Is.EqualTo(new[] { 1, 3 }));
            Assert.That(new[] { allyA.MaxHealth, allyB.MaxHealth }.OrderBy(x => x),
                Is.EqualTo(new[] { 6, 10 }));
        }

        [Test]
        public void FinishTheJob_Only_Accepts_A_Damaged_Minion()
        {
            var state = State();
            CardInstance target = Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
            CardInstance spell = Hand(state, ShippedCards.Id("night_finish_the_job"));
            var processor = new CommandProcessor(_db);

            var undamaged = processor.Execute(state, Play(state, spell, target.Id));

            Assert.That(undamaged.Accepted, Is.False);
            Assert.That(undamaged.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(undamaged.Events, Is.Empty);
            target.CurrentHealth--;

            var damaged = processor.Execute(state, Play(state, spell, target.Id));

            Assert.That(damaged.Accepted, Is.True);
            Assert.That(state.Players[1].Board, Is.Empty);
        }

        [Test]
        public void Random_Targeting_Uses_The_Canonical_State_Rng_And_Replays_Identically()
        {
            (string hash, ulong rng) Run()
            {
                var state = State(seed: 29);
                Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
                Board(state, 1, ShippedCards.Id("neutral_stoneback_sentry"));
                CardInstance spell = Hand(state, ShippedCards.Id("storm_chain_lightning"));
                new CommandProcessor(_db).Execute(state, Play(state, spell));
                return (GameStateHasher.Hash(state), state.Rng.RawState);
            }

            var first = Run();
            var second = Run();

            Assert.That(second, Is.EqualTo(first));
            Assert.That(first.rng, Is.Not.EqualTo(29UL), "Each RANDOM selection must advance GameState.Rng.");
        }
    }
}
