using System;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.CardBehavior
{
    /// <summary>
    /// REANIMATE moves a graveyard minion onto the board as itself -- same instance id, printed
    /// stats, summoning-sick, via the same <c>RulesEngine.EnterBoard</c> door every other arrival
    /// uses. See <see cref="ActionHandlers.ReanimateHandler"/> for why a full board stops the loop
    /// rather than skipping past it.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ReanimateTests
    {
        private static CardDefinition Minion(int id, int attack, int health, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 0, attack, health,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("reanimate"), new DeterministicRandom(23UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
                NextInstanceCounter = 3,
            };
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardInstance ToHand(GameState s, int seat, int definitionId)
        {
            var card = new CardInstance(s.NextInstanceId(), definitionId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(card);
            return card;
        }

        private static CardInstance InGraveyard(GameState s, int seat, CardDefinition def, int damageTaken = 0)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id)
            {
                Zone = Zone.Graveyard,
                CurrentAttack = def.Attack ?? 0,
                MaxHealth = def.Health ?? 0,
                CurrentHealth = (def.Health ?? 0) - damageTaken,
                // Call order IS death order here, same clock RulesEngine.EnterGraveyard stamps in a
                // real match -- see GameState.NextDeathSequence.
                DiedAtSequence = s.NextDeathSequence++,
            };
            s.Players[seat].Graveyard.Add(card);
            return card;
        }

        private static CardInstance OnBoard(GameState s, ICardDatabase db, int seat, CardDefinition def)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(s, card, def, seat, -1, db), Is.True);
            return card;
        }

        private static CommandResult Play(GameState s, ICardDatabase db, CardInstance card,
            params TargetRef[] targets)
        {
            var result = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, card.Id, 0, targets));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }

        [Test]
        public void Reanimated_Minion_Arrives_On_Its_Controllers_Board_At_Printed_Stats_And_Summoning_Sick()
        {
            var fallen = Minion(FixtureCard.Id("fallen"), 4, 2);
            var raiseDead = Spell(FixtureCard.Id("raise_dead"), TestRules.Rule("raise_dead", Trigger.ON_CAST,
                new ReanimateAction(new TargetSpec("FRIENDLY_GRAVEYARD_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(fallen).Register(raiseDead);
            var s = State();
            var corpse = InGraveyard(s, 0, fallen, damageTaken: 1); // died wounded -- damage must not carry over

            var result = Play(s, db, ToHand(s, 0, raiseDead.Id), new TargetRef(corpse.Id));

            // The graveyard is not empty afterward -- the just-cast SPELL joins it, same as any
            // other resolved spell. The corpse is what must have left.
            Assert.That(s.Players[0].Graveyard, Has.None.Matches<CardInstance>(c => c.Id == corpse.Id));
            var revived = s.Players[0].Board.Single();
            Assert.That(revived.Id, Is.EqualTo(corpse.Id), "the same instance returns -- its id was already public");
            Assert.That((revived.CurrentAttack, revived.MaxHealth, revived.CurrentHealth), Is.EqualTo((4, 2, 2)));
            Assert.That(revived.SummonedThisTurn, Is.True);
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.CardSummoned && e.Source == corpse.Id), Is.True);
        }

        [Test]
        public void A_Full_Board_Leaves_The_Corpse_In_The_Graveyard_Instead_Of_Rejecting_The_Cast()
        {
            var filler = Minion(FixtureCard.Id("filler"), 1, 1);
            var fallen = Minion(FixtureCard.Id("fallen"), 4, 2);
            var raiseDead = Spell(FixtureCard.Id("raise_dead"), TestRules.Rule("raise_dead", Trigger.ON_CAST,
                new ReanimateAction(new TargetSpec("FRIENDLY_GRAVEYARD_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(filler).Register(fallen).Register(raiseDead);
            var s = State();
            for (int i = 0; i < 7; i++) OnBoard(s, db, 0, filler); // BoardCap
            var corpse = InGraveyard(s, 0, fallen);

            Play(s, db, ToHand(s, 0, raiseDead.Id), new TargetRef(corpse.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(7));
            Assert.That(s.Players[0].Graveyard, Does.Contain(corpse), "the corpse stays put -- there was nowhere to put it");
            Assert.That(corpse.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [Test]
        public void Reanimating_From_The_Enemy_Graveyard_Revives_It_Under_The_Effect_Controller()
        {
            var fallen = Minion(FixtureCard.Id("fallen"), 3, 3);
            var raiseTheirDead = Spell(FixtureCard.Id("raise_their_dead"), TestRules.Rule("raise_their_dead", Trigger.ON_CAST,
                new ReanimateAction(new TargetSpec("ENEMY_GRAVEYARD_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(fallen).Register(raiseTheirDead);
            var s = State();
            var corpse = InGraveyard(s, 1, fallen); // seat 1 (P1) owns the corpse; P0 (active) casts

            Play(s, db, ToHand(s, 0, raiseTheirDead.Id), new TargetRef(corpse.Id));

            var revived = s.Players[0].Board.Single();
            Assert.That(revived.Id, Is.EqualTo(corpse.Id));
            Assert.That(revived.Controller, Is.EqualTo(s.Players[0].Id));
            Assert.That(s.Players[1].Graveyard, Is.Empty);
        }

        // ── Mass Rebirth shape: AUTOMATIC selection across BOTH graveyards ──────────────

        private static CardDefinition UndeadMinion(int id, int attack, int health)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 0, attack, health,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(), "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, Tribe.UNDEAD, "");

        private static CardDefinition MassRebirthShaped(int id)
            => Spell(id, TestRules.Rule("mass_rebirth", Trigger.ON_CAST,
                new ReanimateAction(new TargetSpec("ANY_GRAVEYARD_MINION", TargetSelection.AutomaticOf(7),
                    filter: new TargetFilterDef(tribe: Tribe.UNDEAD, negateTribe: true)))));

        [Test]
        public void Automatic_Selection_Needs_No_Player_Chosen_Target()
        {
            var fallen = Minion(FixtureCard.Id("fallen"), 2, 2);
            var massRebirth = MassRebirthShaped(FixtureCard.Id("mass_rebirth"));
            var db = new FakeCardDatabase().Register(fallen).Register(massRebirth);
            var s = State();
            InGraveyard(s, 0, fallen);

            // No TargetRef supplied -- ValidatePlayerChoices must accept zero targets for a spec
            // that is not player-chosen (IsPlayerChosen == false for an automatic selection).
            Play(s, db, ToHand(s, 0, massRebirth.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1));
        }

        [Test]
        public void Automatic_Selection_Reaches_Both_Graveyards_Newest_Death_First_And_Skips_Undead()
        {
            var fallen = Minion(FixtureCard.Id("fallen"), 2, 2);
            var undead = UndeadMinion(FixtureCard.Id("undead"), 2, 2);
            var massRebirth = MassRebirthShaped(FixtureCard.Id("mass_rebirth"));
            var db = new FakeCardDatabase().Register(fallen).Register(undead).Register(massRebirth);
            var s = State();

            // Death order across BOTH graveyards, oldest to newest -- no single graveyard's list
            // order could express this by itself (GameState.NextDeathSequence is the shared clock).
            var oldest = InGraveyard(s, 0, fallen);       // seat 0, 1st death
            var middle = InGraveyard(s, 1, fallen);       // seat 1, 2nd death
            var mostRecentUndead = InGraveyard(s, 0, undead); // seat 0, 3rd death -- filtered out
            var newest = InGraveyard(s, 1, fallen);       // seat 1, 4th death

            Play(s, db, ToHand(s, 0, massRebirth.Id));

            // Every selected corpse belongs to the caster after reanimation, regardless of which
            // graveyard supplied it. The one global death clock determines their board order.
            Assert.That(s.Players[0].Board.Select(c => c.Id),
                Is.EqualTo(new[] { newest.Id, middle.Id, oldest.Id }));
            Assert.That(s.Players[0].Board, Has.All.Matches<CardInstance>(
                c => c.Controller == s.Players[0].Id));
            Assert.That(s.Players[1].Board, Is.Empty);

            Assert.That(s.Players[0].Graveyard, Does.Contain(mostRecentUndead), "Undead stays dead");
            // The cast spell itself joins seat 0's graveyard alongside the un-reanimated Undead --
            // same as any other resolved spell (see the printed-stats test above).
            Assert.That(s.Players[0].Graveyard, Has.Count.EqualTo(2));
            Assert.That(s.Players[1].Graveyard, Is.Empty);
        }

        [Test]
        public void Automatic_Selection_Stops_At_Board_Capacity_Without_Skipping_To_Later_Candidates()
        {
            var filler = Minion(FixtureCard.Id("filler"), 1, 1);
            var fallen = Minion(FixtureCard.Id("fallen"), 2, 2);
            var massRebirth = MassRebirthShaped(FixtureCard.Id("mass_rebirth"));
            var db = new FakeCardDatabase().Register(filler).Register(fallen).Register(massRebirth);
            var s = State();
            for (int i = 0; i < 6; i++) OnBoard(s, db, 0, filler); // 1 free slot, BoardCap 7

            var oldest = InGraveyard(s, 0, fallen);
            var newest = InGraveyard(s, 0, fallen);

            Play(s, db, ToHand(s, 0, massRebirth.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(7));
            Assert.That(s.Players[0].Board, Has.Some.Matches<CardInstance>(c => c.Id == newest.Id),
                "the most recent death fills the one free slot");
            Assert.That(s.Players[0].Graveyard, Does.Contain(oldest), "the older corpse has nowhere to go");
        }
    }
}
