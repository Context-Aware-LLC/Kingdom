using System;
using System.Collections.Generic;
using System.Linq;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Achievements
{
    /// <summary>
    /// The Findings that read who dealt a blow, driven through the real command processor: the
    /// observer infers attribution from event sources, so a test that fabricated the events would
    /// only prove it agrees with itself.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FindingsObserverTests
    {
        private static readonly int FaceBolt = FixtureCard.Id("findings_face_bolt");
        private static readonly int SelfBolt = FixtureCard.Id("findings_self_bolt");
        private static readonly int Zap = FixtureCard.Id("findings_zap");
        private static readonly int Bounce = FixtureCard.Id("findings_bounce");
        private static readonly int Steal = FixtureCard.Id("findings_steal");
        private static readonly int Giant = FixtureCard.Id("findings_giant");
        private static readonly int TollBomb = FixtureCard.Id("findings_toll_bomb");
        private static readonly int Wall = FixtureCard.Id("findings_wall");
        private static readonly int Squire = FixtureCard.Id("findings_squire");

        [Test]
        public void A_Hero_Felled_By_Its_Own_Spell_Fell_To_Its_Own_Side()
        {
            var t = new Table();
            int me = t.Me;
            t.State.Players[me].Hero.CurrentHealth = 5;
            t.Play(SelfBolt);

            FinalBlow fell = t.Finish()[me].FellTo;
            Assert.That(fell, Is.EqualTo(FinalBlow.Spell | FinalBlow.OwnSide));
        }

        [Test]
        public void A_Hero_Power_Finish_Is_Credited_To_The_Hero_Power()
        {
            var t = new Table();
            int me = t.Me;
            t.State.Players[1 - me].Hero.CurrentHealth = 5;
            t.State.Players[me].Mana.Available = 10;
            t.Do(new HeroPowerIntent(t.State.ActivePlayer, Array.Empty<TargetRef>()));

            Assert.That(t.Finish()[1 - me].FellTo, Is.EqualTo(FinalBlow.HeroPower));
        }

        [Test]
        public void A_Toll_Finish_Is_A_Toll_And_Not_The_Spell_That_Rang_It()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId bomb = t.Play(TollBomb);
            t.State.Players[1 - me].Hero.CurrentHealth = 5;
            t.Play(Zap, bomb);

            Assert.That(t.Finish()[1 - me].FellTo, Is.EqualTo(FinalBlow.Toll));
        }

        [Test]
        public void A_Stolen_Minions_Toll_Finishes_Its_Owner_As_A_Stolen_Minion()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId bomb = t.Play(TollBomb);
            t.EndTurn();
            t.Play(Steal, bomb);
            t.State.Players[1 - me].Hero.CurrentHealth = 5;
            t.Play(Zap, bomb);

            Assert.That(t.Finish()[1 - me].FellTo, Is.EqualTo(FinalBlow.Toll | FinalBlow.StolenMinion));
        }

        [Test]
        public void Fatigue_Is_The_Blow_When_An_Empty_Deck_Finishes_A_Hero()
        {
            var t = new Table();
            int me = t.Me;
            t.State.Players[1 - me].Deck.Clear();
            t.State.Players[1 - me].Hero.CurrentHealth = 1;
            t.EndTurn();

            Assert.That(t.Finish()[1 - me].FellTo, Is.EqualTo(FinalBlow.Fatigue));
        }

        [Test]
        public void A_Hero_Still_Standing_Fell_To_Nothing()
        {
            var t = new Table();
            int me = t.Me;
            t.Play(SelfBolt);
            t.Play(SelfBolt);

            SeatOutcome mine = t.Finish()[me];
            Assert.Multiple(() =>
            {
                Assert.That(mine.FellTo, Is.EqualTo(FinalBlow.None));
                Assert.That(mine.SelfDamage, Is.EqualTo(20), "Self-Starter counts damage your own cards dealt you");
            });
        }

        [Test]
        public void Ten_Damage_To_A_One_Health_Minion_Is_An_Overkill_For_Whoever_Dealt_It()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId squire = t.Play(Squire);
            t.EndTurn();
            t.Play(Zap, squire);

            SeatOutcome[] seats = t.Finish();
            Assert.That(seats[me].Overkills, Is.EqualTo(1));
            Assert.That(seats[1 - me].Overkills, Is.Zero);
        }

        [Test]
        public void Ten_Damage_To_A_Healthier_Minion_Is_No_Overkill()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId giant = t.Play(Giant);
            t.Play(Zap, giant);

            Assert.That(t.Finish()[me].Overkills, Is.Zero);
        }

        [Test]
        public void An_Eight_Cost_Minion_Returned_To_Hand_Is_Its_Owners_Hubris()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId giant = t.Play(Giant);
            t.Play(Bounce, giant);

            Assert.That(t.Finish()[me].BigMinionsBounced, Is.EqualTo(1));
        }

        [Test]
        public void A_Legendary_Taken_By_The_Other_Side_Is_Lost_By_Its_Owner()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId giant = t.Play(Giant);
            t.EndTurn();
            t.Play(Steal, giant);

            SeatOutcome[] seats = t.Finish();
            Assert.That(seats[1 - me].LegendariesLost, Is.EqualTo(1));
            Assert.That(seats[me].LegendariesLost, Is.Zero);
        }

        [Test]
        public void Seven_Bulwarks_On_The_Board_Are_Counted()
        {
            var t = new Table();
            int me = t.Me;
            for (int i = 0; i < 7; i++) t.Play(Wall);

            Assert.That(t.Finish()[me].MostBulwarks, Is.EqualTo(7));
        }

        // ── scaffolding ───────────────────────────────────────────────────────────

        /// <summary>A two-seat match on synthetic cards, every command observed the way
        /// <c>MatchSession</c> observes it.</summary>
        private sealed class Table
        {
            private readonly LocalMatchHost _host;
            private readonly InMemoryTelemetrySink _sink = new InMemoryTelemetrySink();
            private readonly MatchTelemetryRecorder _recorder;

            public Table()
            {
                var db = new FakeCardDatabase()
                    .Register(Spell(FaceBolt, new DamageAction(10, Auto("ENEMY_HERO"))))
                    .Register(Spell(SelfBolt, new DamageAction(10, Auto("FRIENDLY_HERO"))))
                    .Register(Spell(Zap, new DamageAction(10, new TargetSpec("ANY_MINION", TargetSelection.Of(1)))))
                    .Register(Spell(Bounce, new ReturnToHandAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))))
                    .Register(Spell(Steal, new TakeControlAction(new TargetSpec("ANY_ENEMY_MINION", TargetSelection.Of(1)))))
                    .Register(Minion(Giant, cost: 8, health: 8, Rarity.LEGENDARY))
                    .Register(Minion(Wall, cost: 0, health: 5, Rarity.COMMON, keyword: Keyword.BULWARK))
                    .Register(Minion(Squire, cost: 0, health: 1, Rarity.COMMON))
                    .Register(Minion(TollBomb, cost: 0, health: 1, Rarity.COMMON,
                        toll: new RuleDefinition(new RuleId("toll"), new TriggeredActivation(Trigger.ON_DEATH),
                            AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                            new DamageAction(10, Auto("ENEMY_HERO")))));

                var deck = FakeCardDatabase.Deck(1001, 20);
                var setup = new MatchSetup(CardClass.EMBERWARD, deck, CardClass.TIDECALLER, deck);
                _host = new LocalMatchHost(new CommandProcessor(db), db, setup);
                var id = new MatchId("m-findings");
                State = _host.BeginMatch(id, new PlayerId("p0"), new PlayerId("p1"), 7UL);
                foreach (PlayerState ps in State.Players)
                    db.Register(new CardDefinition(
                        id: ps.Hero.HeroPowerId, handle: $"hp{ps.Hero.HeroPowerId}", name: "hp",
                        type: CardType.HERO_POWER, cost: 2, attack: null, health: null, durability: null,
                        @class: CardClass.NEUTRAL, rarity: Rarity.COMMON, keywords: Array.Empty<Keyword>(),
                        rules: new[] { Cast(new DamageAction(10, Auto("ENEMY_HERO"))) },
                        flavorText: "", artId: "art/hp", setId: SetId.CORE,
                        collectible: false, tribe: null, factionId: ""));

                _recorder = new MatchTelemetryRecorder(id, setup, _sink,
                    new MatchAttribution(SessionHarness.AccountFor(0), SessionHarness.AccountFor(1), TelemetrySource.Ladder),
                    db);
                _recorder.AfterDeal(State);
                Do(new MulliganIntent(State.Players[0].Id, Array.Empty<InstanceId>()));
                Do(new MulliganIntent(State.Players[1].Id, Array.Empty<InstanceId>()));
            }

            public GameState State { get; }
            public int Me => State.SeatOf(State.ActivePlayer);

            public void Do(PlayerIntent intent)
            {
                MatchTelemetryRecorder.Pending pending = _recorder.BeforeSubmit(State, intent);
                CommandResult result = _host.Submit(intent);
                Assert.That(result.Accepted, Is.True, $"{intent.GetType().Name} rejected: {result.Reason}");
                _recorder.AfterAccepted(State, pending, result.Events);
            }

            public void EndTurn() => Do(new EndTurnIntent(State.ActivePlayer));

            /// <summary>Plays a fresh copy of a card for the active seat; returns the minion it put
            /// on the board, or default for a spell.</summary>
            public InstanceId Play(int cardId, params InstanceId[] targets)
            {
                PlayerState ps = State.Players[Me];
                ps.Mana.Available = 10;
                var card = new CardInstance(State.NextInstanceId(), cardId, ps.Id) { Zone = Zone.Hand };
                ps.Hand.Add(card);
                Do(new PlayCardIntent(ps.Id, card.Id, ps.Board.Count,
                    targets.Select(target => new TargetRef(target)).ToArray()));
                return ps.Board.FirstOrDefault(m => m.Id == card.Id)?.Id ?? default;
            }

            public SeatOutcome[] Finish()
            {
                _recorder.Complete(State, DateTimeOffset.UnixEpoch, MatchEndReason.Win);
                MatchCompleteEvent e = _sink.Completes.Single();
                return new[] { e.P0, e.P1 };
            }
        }

        private static TargetSpec Auto(string selector) => new TargetSpec(selector, TargetSelection.AutomaticOf(1));

        private static RuleDefinition Cast(RuleAction action)
            => new RuleDefinition(new RuleId("cast"), new TriggeredActivation(Trigger.ON_CAST),
                AlwaysCondition.Instance, OneShotLifetime.Instance, action);

        private static CardDefinition Spell(int id, RuleAction action)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                type: CardType.SPELL, cost: 0, attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON, keywords: Array.Empty<Keyword>(),
                rules: new[] { Cast(action) },
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static CardDefinition Minion(int id, int cost, int health, Rarity rarity,
            Keyword? keyword = null, RuleDefinition? toll = null)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                type: CardType.MINION, cost: cost, attack: 1, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: keyword is Keyword k ? new[] { k } : Array.Empty<Keyword>(),
                rules: toll != null ? new[] { toll } : Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");
    }
}
