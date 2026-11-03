using System;
using System.Collections.Generic;
using System.Linq;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Achievements
{
    /// <summary>
    /// The exact per-seat numbers the King's commissions are scored from (#421). These decide prizes,
    /// so each is driven through the real command processor and checked against the value a player
    /// would compute by hand, including the cases a replayed estimate gets wrong.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CommissionTallyTests
    {
        private static readonly int Zap3 = FixtureCard.Id("tally_zap3");
        private static readonly int Zap7 = FixtureCard.Id("tally_zap7");
        private static readonly int FaceBolt = FixtureCard.Id("tally_face_bolt");
        private static readonly int BuffThenHit = FixtureCard.Id("tally_buff_then_hit");
        private static readonly int Steal = FixtureCard.Id("tally_steal");
        private static readonly int Squire = FixtureCard.Id("tally_squire");
        private static readonly int Trio = FixtureCard.Id("tally_trio");
        private static readonly int Five = FixtureCard.Id("tally_five");
        private static readonly int TollBomb = FixtureCard.Id("tally_toll_bomb");
        private static readonly int Giant = FixtureCard.Id("tally_giant");
        private static readonly int Colossus = FixtureCard.Id("tally_colossus");
        private static readonly int Grow2 = FixtureCard.Id("tally_grow2");
        private static readonly int Grow5 = FixtureCard.Id("tally_grow5");

        // ── Overkill ──────────────────────────────────────────────────────────────

        [Test]
        public void Overkill_is_exact_on_a_minion_buffed_in_the_same_command()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            t.Play(Trio);                // 3 Health, theirs
            t.EndTurn();
            t.Play(BuffThenHit);         // +2 Health, then 9: 9 - 5 = 4

            Assert.That(t.Tally(me).OverkillBest, Is.EqualTo(4));
            Assert.That(t.Tally(1 - me).OverkillBest, Is.Zero);
        }

        [Test]
        public void Overkill_ignores_a_hit_that_does_not_kill()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId five = t.Play(Five);
            t.EndTurn();
            t.Play(Zap3, five);

            Assert.That(t.Tally(me).OverkillBest, Is.Zero);
        }

        [Test]
        public void Overkill_keeps_the_best_hit()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId a = t.Play(Squire);
            InstanceId b = t.Play(Squire);
            InstanceId c = t.Play(Squire);
            t.EndTurn();
            t.Play(Zap3, a);             // 2 beyond 1 Health
            t.Play(Zap7, b);             // 6
            t.Play(Zap3, c);             // 2 again: neither the last hit nor the sum

            Assert.That(t.Tally(me).OverkillBest, Is.EqualTo(6));
        }

        // ── Hero damage by kind ───────────────────────────────────────────────────

        [Test]
        public void Armor_soaked_hero_damage_still_counts_as_dealt()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId bomb = t.Play(TollBomb);
            t.State.Players[1 - me].Hero.Armor = 10;
            t.Play(Zap3, bomb);          // its Toll hits their hero for 4, all of it soaked

            Assert.That(t.State.Players[1 - me].Hero.CurrentHealth, Is.EqualTo(30), "precondition: armor took it all");
            Assert.That(t.Tally(me).TollDamage, Is.EqualTo(4));
            Assert.That(t.Tally(me).StolenDamage, Is.Zero, "the bomb was always mine");
            Assert.That(t.Tally(1 - me).TollDamage, Is.Zero);
        }

        [Test]
        public void Toll_damage_sums_every_toll_not_only_the_fatal_one()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId first = t.Play(TollBomb);
            InstanceId second = t.Play(TollBomb);
            t.Play(Zap3, first);
            t.Play(Zap3, second);
            t.Play(FaceBolt);            // not a Toll

            Assert.That(t.Tally(me).TollDamage, Is.EqualTo(8));
        }

        [Test]
        public void A_stolen_minions_damage_to_its_owner_counts_as_stolen()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId bomb = t.Play(TollBomb);
            t.EndTurn();
            t.Play(Steal, bomb);
            t.Play(Zap3, bomb);          // their own bomb's Toll, now mine, hits them for 4

            Assert.Multiple(() =>
            {
                Assert.That(t.Tally(me).StolenDamage, Is.EqualTo(4));
                Assert.That(t.Tally(me).TollDamage, Is.EqualTo(4));
                Assert.That(t.Tally(1 - me).StolenDamage, Is.Zero);
            });
        }

        [Test]
        public void A_toll_attacker_that_dies_in_its_own_trade_still_counts_its_toll()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId bomb = t.Play(TollBomb);        // 1/1, Toll: 4 to the enemy hero
            t.EndTurn();
            InstanceId five = t.Play(Five);             // 1/5, theirs
            t.EndTurn();
            t.Attack(bomb, five);                       // the bomb dies to retaliation, then its Toll rings

            Assert.That(t.State.Players[1 - me].Hero.CurrentHealth, Is.EqualTo(26), "precondition: the Toll landed");
            Assert.That(t.Tally(me).TollDamage, Is.EqualTo(4));
        }

        [Test]
        public void A_toll_minions_own_attack_hit_is_not_toll()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId bomb = t.Play(TollBomb);
            t.EndTurn();
            t.EndTurn();
            t.Attack(bomb, t.State.Players[1 - me].Hero.Id);

            Assert.That(t.State.Players[1 - me].Hero.CurrentHealth, Is.EqualTo(29), "precondition: the attack landed");
            Assert.That(t.Tally(me).TollDamage, Is.Zero);
        }

        [Test]
        public void Hero_power_damage_counts_a_hit_on_an_enemy_minion()
        {
            var t = new Table(Cast(new DamageAction(4, Auto("ANY_ENEMY_MINION"))));
            int me = t.Me;
            t.EndTurn();
            t.Play(Five);
            t.EndTurn();
            t.HeroPower();               // 4 to their minion
            t.Play(FaceBolt);            // 5 to their hero, not the Hero Power

            Assert.That(t.Tally(me).HeroPowerDamage, Is.EqualTo(4));
            Assert.That(t.Tally(1 - me).HeroPowerDamage, Is.Zero);
        }

        [Test]
        public void Hero_power_damage_to_your_own_side_scores_zero()
        {
            var t = new Table(
                Cast(new DamageAction(3, Auto("FRIENDLY_HERO")), "self"),
                Cast(new DamageAction(4, Auto("ANY_FRIENDLY_MINION")), "own_minion"));
            int me = t.Me;
            t.Play(Five);
            t.HeroPower();               // 3 to my hero, 4 to my minion

            Assert.That(t.State.Players[me].Hero.CurrentHealth, Is.EqualTo(27), "precondition: the Hero Power hit me");
            Assert.That(t.Tally(me).HeroPowerDamage, Is.Zero);
            Assert.That(t.Tally(me).SelfDamage, Is.EqualTo(3), "Self-Starter is where self-harm scores");
        }

        [Test]
        public void Hero_power_damage_counts_a_hit_on_the_enemy_hero_for_its_caster_only()
        {
            var t = new Table(Cast(new DamageAction(2, Auto("ENEMY_HERO"))));
            int me = t.Me;
            t.HeroPower();               // 2 to their hero
            t.Play(FaceBolt);            // not the Hero Power
            t.EndTurn();
            t.HeroPower();               // theirs: 2 to my hero, credited to them

            Assert.That(t.Tally(me).HeroPowerDamage, Is.EqualTo(2));
            Assert.That(t.Tally(1 - me).HeroPowerDamage, Is.EqualTo(2));
        }

        // ── Comeback: the furthest behind on Health ───────────────────────────────

        [Test]
        public void Health_deficit_keeps_the_furthest_behind_after_it_is_erased()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            for (int i = 0; i < 4; i++) t.Play(FaceBolt);   // theirs: 20 to me, 10 against 30
            t.EndTurn();
            for (int i = 0; i < 4; i++) t.Play(FaceBolt);   // mine: 20 back, 10 against 10

            Assert.That(t.State.Players[me].Hero.CurrentHealth, Is.EqualTo(t.State.Players[1 - me].Hero.CurrentHealth),
                "precondition: level again");
            Assert.That(t.Tally(me).MostHealthDeficit, Is.EqualTo(20));
            Assert.That(t.Tally(1 - me).MostHealthDeficit, Is.Zero, "never behind; a lead is not a negative deficit");
        }

        [Test]
        public void Health_deficit_is_measured_against_the_enemy_heros_Health_not_a_full_bar()
        {
            var t = new Table();
            int me = t.Me;
            for (int i = 0; i < 2; i++) t.Play(FaceBolt);   // mine: they are at 20
            t.EndTurn();
            for (int i = 0; i < 5; i++) t.Play(FaceBolt);   // theirs: I am at 5

            Assert.That(t.Tally(me).MostHealthDeficit, Is.EqualTo(15), "5 against their 20, not 5 against 30");
            Assert.That(t.Tally(1 - me).MostHealthDeficit, Is.EqualTo(10), "they were 20 against my 30 first");
        }

        [Test]
        public void Armor_is_not_Health_for_the_deficit()
        {
            var t = new Table();
            int me = t.Me;
            t.State.Players[me].Hero.Armor = 20;
            t.EndTurn();
            for (int i = 0; i < 4; i++) t.Play(FaceBolt);   // all 20 soaked

            Assert.That(t.State.Players[me].Hero.CurrentHealth, Is.EqualTo(30), "precondition: the Armor took it all");
            Assert.That(t.Tally(me).MostHealthDeficit, Is.Zero);
        }

        // ── Overgrown: the largest minion on the board ────────────────────────────

        [Test]
        public void Largest_minion_is_attack_plus_health_on_the_board()
        {
            var t = new Table();
            int me = t.Me;
            t.Play(Squire);              // 1/1
            t.Play(Giant);               // 4/6

            Assert.That(t.Tally(me).LargestMinion, Is.EqualTo(10));
            Assert.That(t.Tally(1 - me).LargestMinion, Is.Zero);
        }

        [Test]
        public void A_buff_grows_the_largest_minion_and_later_damage_does_not_shrink_the_record()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId giant = t.Play(Giant);   // 4/6: 10
            t.Play(Grow5, giant);               // 9/11: 20
            t.Play(Zap7, giant);                // 9/4: 13

            Assert.That(t.Tally(me).LargestMinion, Is.EqualTo(20));
        }

        [Test]
        public void Largest_minion_reads_current_Health_so_damage_taken_first_stays_off()
        {
            var t = new Table();
            int me = t.Me;
            InstanceId giant = t.Play(Giant);   // 4/6: 10
            t.Play(Zap3, giant);                // 4/3: 7
            t.Play(Grow2, giant);               // 6/5: 11, not 4+6+2+2

            Assert.That(t.Tally(me).LargestMinion, Is.EqualTo(11));
        }

        [Test]
        public void A_stolen_minion_counts_for_whoever_controls_it_at_the_time()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurn();
            InstanceId colossus = t.Play(Colossus);   // 10/10, theirs: 20 for them
            t.EndTurn();
            t.Play(Steal, colossus);                  // mine now: 20 for me
            t.Play(Grow5, colossus);                  // 15/15 under me: 30 for me, not them

            Assert.Multiple(() =>
            {
                Assert.That(t.Tally(me).LargestMinion, Is.EqualTo(30));
                Assert.That(t.Tally(1 - me).LargestMinion, Is.EqualTo(20));
            });
        }

        [Test]
        public void Minions_in_hand_or_deck_never_count()
        {
            var t = new Table();
            int me = t.Me;
            PlayerState ps = t.State.Players[me];
            ps.Hand.Add(new CardInstance(t.State.NextInstanceId(), Colossus, ps.Id)
            { Zone = Zone.Hand, CurrentAttack = 10, CurrentHealth = 10, MaxHealth = 10 });
            ps.Deck.Add(new CardInstance(t.State.NextInstanceId(), Colossus, ps.Id)
            { Zone = Zone.Deck, CurrentAttack = 10, CurrentHealth = 10, MaxHealth = 10 });
            t.Play(Squire);              // 1/1, the only minion on a board

            Assert.That(t.Tally(me).LargestMinion, Is.EqualTo(2));
        }

        // ── Turn-end and draw totals ──────────────────────────────────────────────

        [Test]
        public void Unspent_mana_sums_across_the_seats_own_turns()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurnWith(3);
            t.EndTurnWith(5);            // theirs
            t.EndTurnWith(4);

            Assert.That(t.Tally(me).UnspentMana, Is.EqualTo(7));
            Assert.That(t.Tally(1 - me).UnspentMana, Is.EqualTo(5));
        }

        [Test]
        public void Fatigue_damage_sums_the_fatigue_taken()
        {
            var t = new Table();
            int me = t.Me;
            t.State.Players[1 - me].Deck.Clear();
            t.EndTurn();                 // they draw: 1
            t.EndTurn();
            t.EndTurn();                 // they draw: 2

            Assert.That(t.Tally(1 - me).FatigueDamage, Is.EqualTo(3));
            Assert.That(t.Tally(me).FatigueDamage, Is.Zero);
        }

        // ── Carried to the facts ──────────────────────────────────────────────────

        [Test]
        public void The_tally_reaches_the_seat_outcome_and_the_achievement_facts()
        {
            var t = new Table();
            int me = t.Me;
            t.EndTurnWith(0);
            InstanceId squire = t.Play(Squire);
            t.EndTurn();
            InstanceId bomb = t.Play(TollBomb);
            t.Play(Zap7, squire);
            t.Play(Zap3, bomb);
            t.HeroPower();
            t.Play(Giant);
            t.EndTurnWith(6);
            for (int i = 0; i < 4; i++) t.Play(FaceBolt);   // theirs: me at 10

            FindingsObserver.Tally tally = t.Tally(me);
            MatchCompleteEvent complete = t.Finish();
            SeatOutcome seat = me == 0 ? complete.P0 : complete.P1;
            AchievementMatchFacts facts = AchievementMatchFacts.ForSeat(complete, me);

            Assert.Multiple(() =>
            {
                Assert.That(tally.OverkillBest, Is.EqualTo(6));
                Assert.That(tally.TollDamage, Is.EqualTo(4));
                Assert.That(tally.HeroPowerDamage, Is.EqualTo(2));
                Assert.That(tally.UnspentMana, Is.EqualTo(6));
                Assert.That(tally.MostHealthDeficit, Is.EqualTo(14), "their 24, after my Toll and Hero Power, against my 10");
                Assert.That(tally.LargestMinion, Is.EqualTo(10));

                Assert.That(seat.OverkillBest, Is.EqualTo(tally.OverkillBest));
                Assert.That(seat.UnspentMana, Is.EqualTo(tally.UnspentMana));
                Assert.That(seat.FatigueDamage, Is.EqualTo(tally.FatigueDamage));
                Assert.That(seat.TollDamage, Is.EqualTo(tally.TollDamage));
                Assert.That(seat.StolenDamage, Is.EqualTo(tally.StolenDamage));
                Assert.That(seat.HeroPowerDamage, Is.EqualTo(tally.HeroPowerDamage));
                Assert.That(seat.SelfDamage, Is.EqualTo(tally.SelfDamage));
                Assert.That(seat.CardsBurned, Is.EqualTo(tally.CardsBurned));
                Assert.That(seat.MostHealthDeficit, Is.EqualTo(tally.MostHealthDeficit));
                Assert.That(seat.LargestMinion, Is.EqualTo(tally.LargestMinion));

                Assert.That(facts.OverkillBest, Is.EqualTo(tally.OverkillBest));
                Assert.That(facts.UnspentMana, Is.EqualTo(tally.UnspentMana));
                Assert.That(facts.FatigueDamage, Is.EqualTo(tally.FatigueDamage));
                Assert.That(facts.TollDamage, Is.EqualTo(tally.TollDamage));
                Assert.That(facts.StolenDamage, Is.EqualTo(tally.StolenDamage));
                Assert.That(facts.HeroPowerDamage, Is.EqualTo(tally.HeroPowerDamage));
                Assert.That(facts.MostHealthDeficit, Is.EqualTo(tally.MostHealthDeficit));
                Assert.That(facts.LargestMinion, Is.EqualTo(tally.LargestMinion));
            });
        }

        // ── scaffolding ───────────────────────────────────────────────────────────

        /// <summary>A two-seat match on synthetic cards, every command observed the way
        /// <c>MatchSession</c> observes it.</summary>
        private sealed class Table
        {
            private readonly LocalMatchHost _host;
            private readonly InMemoryTelemetrySink _sink = new InMemoryTelemetrySink();
            private readonly MatchTelemetryRecorder _recorder;

            /// <param name="heroPower">Both heroes' Hero Power; by default 2 damage to the enemy hero.</param>
            public Table(params RuleDefinition[] heroPower)
            {
                if (heroPower.Length == 0) heroPower = new[] { Cast(new DamageAction(2, Auto("ENEMY_HERO"))) };
                var db = new FakeCardDatabase()
                    .Register(Spell(Zap3, Cast(new DamageAction(3, new TargetSpec("ANY_MINION", TargetSelection.Of(1))))))
                    .Register(Spell(Zap7, Cast(new DamageAction(7, new TargetSpec("ANY_MINION", TargetSelection.Of(1))))))
                    .Register(Spell(FaceBolt, Cast(new DamageAction(5, Auto("ENEMY_HERO")))))
                    .Register(Spell(BuffThenHit,
                        Cast(new ModifyStatsAction(0, 2, Auto("ANY_ENEMY_MINION")), "buff"),
                        Cast(new DamageAction(9, Auto("ANY_ENEMY_MINION")), "hit")))
                    .Register(Spell(Steal, Cast(new TakeControlAction(new TargetSpec("ANY_ENEMY_MINION", TargetSelection.Of(1))))))
                    .Register(Spell(Grow2, Cast(new ModifyStatsAction(2, 2, new TargetSpec("ANY_MINION", TargetSelection.Of(1))))))
                    .Register(Spell(Grow5, Cast(new ModifyStatsAction(5, 5, new TargetSpec("ANY_MINION", TargetSelection.Of(1))))))
                    .Register(Minion(Giant, health: 6, attack: 4))
                    .Register(Minion(Colossus, health: 10, attack: 10))
                    .Register(Minion(Squire, health: 1))
                    .Register(Minion(Trio, health: 3))
                    .Register(Minion(Five, health: 5))
                    .Register(Minion(TollBomb, health: 1,
                        toll: new RuleDefinition(new RuleId("toll"), new TriggeredActivation(Trigger.ON_DEATH),
                            AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                            new DamageAction(4, Auto("ENEMY_HERO")))));

                var deck = FakeCardDatabase.Deck(1001, 20);
                var setup = new MatchSetup(CardClass.EMBERWARD, deck, CardClass.TIDECALLER, deck);
                _host = new LocalMatchHost(new CommandProcessor(db), db, setup);
                var id = new MatchId("m-tally");
                State = _host.BeginMatch(id, new PlayerId("p0"), new PlayerId("p1"), 7UL);
                foreach (PlayerState ps in State.Players)
                    db.Register(new CardDefinition(
                        id: ps.Hero.HeroPowerId, handle: $"hp{ps.Hero.HeroPowerId}", name: "hp",
                        type: CardType.HERO_POWER, cost: 2, attack: null, health: null, durability: null,
                        @class: CardClass.NEUTRAL, rarity: Rarity.COMMON, keywords: Array.Empty<Keyword>(),
                        rules: heroPower,
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

            public FindingsObserver.Tally Tally(int seat) => _recorder.TallyFor(seat);

            public void Do(PlayerIntent intent)
            {
                MatchTelemetryRecorder.Pending pending = _recorder.BeforeSubmit(State, intent);
                CommandResult result = _host.Submit(intent);
                Assert.That(result.Accepted, Is.True, $"{intent.GetType().Name} rejected: {result.Reason}");
                _recorder.AfterAccepted(State, pending, result.Events);
            }

            public void EndTurn() => Do(new EndTurnIntent(State.ActivePlayer));

            public void EndTurnWith(int unspent)
            {
                State.Players[Me].Mana.Available = unspent;
                EndTurn();
            }

            public void Attack(InstanceId attacker, InstanceId defender)
                => Do(new AttackIntent(State.ActivePlayer, attacker, new TargetRef(defender)));

            public void HeroPower()
            {
                State.Players[Me].Mana.Available = 10;
                Do(new HeroPowerIntent(State.ActivePlayer, Array.Empty<TargetRef>()));
            }

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

            public MatchCompleteEvent Finish()
            {
                _recorder.Complete(State, DateTimeOffset.UnixEpoch, MatchEndReason.Win);
                return _sink.Completes.Single();
            }
        }

        private static TargetSpec Auto(string selector) => new TargetSpec(selector, TargetSelection.AutomaticOf(1));

        private static RuleDefinition Cast(RuleAction action, string id = "cast")
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(Trigger.ON_CAST),
                AlwaysCondition.Instance, OneShotLifetime.Instance, action);

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                type: CardType.SPELL, cost: 0, attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON, keywords: Array.Empty<Keyword>(),
                rules: rules,
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static CardDefinition Minion(int id, int health, RuleDefinition? toll = null, int attack = 1)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                type: CardType.MINION, cost: 0, attack: attack, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON, keywords: Array.Empty<Keyword>(),
                rules: toll != null ? new[] { toll } : Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");
    }
}
