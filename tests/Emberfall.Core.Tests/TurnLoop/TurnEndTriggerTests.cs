using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.TurnLoop
{
    /// <summary>
    /// L1 — EF-012. <c>ON_TURN_END</c> was declared in the <see cref="Trigger"/> enum and raised nowhere,
    /// so a card carrying it validated, loaded and then did nothing forever. These lock the raise site's
    /// three observable properties: that it fires at all, that it fires on BOTH players' turn ends (the
    /// scope decision, see <c>CommandProcessor.ApplyEndTurn</c>), and that its cascade fully settles
    /// before the seat flip — a minion the trigger kills must not survive into the next turn.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class TurnEndTriggerTests
    {
        private const string Toller = "ef012_toller";     // 1/3, "at the end of a turn, 1 damage to the enemy hero"
        private const string Vanilla = "ef012_vanilla";   // 1/1, no effects — the control

        /// <param name="tollDamage">damage the toller deals to the enemy hero on every turn end.</param>
        private static FakeCardDatabase Db(int tollDamage = 1, int tollerHealth = 3)
        {
            var toll = new EffectDef(Trigger.ON_TURN_END, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                amount: tollDamage, amount2: null, tokenId: null, grantedKeyword: null,
                vfxOverride: null, selector: "ENEMY_HERO");

            return new FakeCardDatabase()
                .Register(Minion(Toller, health: tollerHealth, effects: new[] { toll }))
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()));
        }

        private static CardDefinition Minion(string id, int health, EffectDef[] effects)
            => new CardDefinition(
                id: id, name: id, type: CardType.MINION, cost: 1,
                attack: 1, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: effects,
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        /// <summary>A match where both decks are nothing but <paramref name="cardId"/>, already past mulligan.</summary>
        private static (LocalMatchHost host, GameState state) Match(ICardDatabase db, string cardId, ulong seed)
        {
            var deck = Enumerable.Repeat(cardId, 20).ToList();
            var setup = new MatchSetup(CardClass.EMBERWARD, deck, CardClass.TIDECALLER, deck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var state = host.BeginMatch(new MatchId("EF012"), new PlayerId("P0"), new PlayerId("P1"), seed);
            Accept(host, new MulliganIntent(state.Players[0].Id, Array.Empty<InstanceId>()));
            Accept(host, new MulliganIntent(state.Players[1].Id, Array.Empty<InstanceId>()));
            return (host, state);
        }

        private static CommandResult Accept(LocalMatchHost host, PlayerIntent intent)
        {
            var r = host.Submit(intent);
            Assert.That(r.Accepted, Is.True, $"{intent.GetType().Name} rejected: {r.Reason}");
            return r;
        }

        /// <summary>Play the active player's first copy of <paramref name="cardId"/> from hand.</summary>
        private static void Play(LocalMatchHost host, GameState s, string cardId, int slot = 0)
        {
            int seat = s.SeatOf(s.ActivePlayer);
            var card = s.Players[seat].Hand.First(c => c.DefinitionId == cardId);
            Accept(host, new PlayCardIntent(s.ActivePlayer, card.Id, slot, Array.Empty<TargetRef>()));
        }

        [Test]
        public void OnTurnEnd_Fires_At_All()
        {
            var (host, s) = Match(Db(), Toller, seed: 11UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int before = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(before - 1),
                "A minion with an ON_TURN_END effect must actually resolve it — the trigger was dead before EF-012.");
        }

        [Test]
        public void OnTurnEnd_Fires_On_Both_Players_Turn_Ends()
        {
            // The scope decision: RaiseObservers sweeps BOTH boards, so a turn-end effect is "at the end
            // of ANY turn", not "at the end of YOUR turn". If this ever becomes controller-scoped, this
            // test is the one that must change, deliberately.
            var (host, s) = Match(Db(), Toller, seed: 12UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int start = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));      // owner's turn ends
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 1));

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));     // OPPONENT's turn ends
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 2),
                "The toller belongs to the first player but must also fire when the second player ends their turn.");
        }

        [Test]
        public void Both_Sides_Tollers_Fire_On_A_Single_Turn_End()
        {
            var (host, s) = Match(Db(), Toller, seed: 13UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);                                          // first player's toller
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));
            Play(host, s, Toller);                                          // second player's toller
            int firstHero = s.Players[firstSeat].Hero.CurrentHealth;
            int secondHero = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));

            Assert.That(s.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHero - 1),
                "Second player's toller hits the first player's hero.");
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHero - 1),
                "First player's toller hits the second player's hero on the same turn end.");
        }

        [Test]
        public void Turn_End_Cascade_Settles_Before_The_Seat_Flip()
        {
            // The EF-010 ordering contract: deaths caused by ON_TURN_END must resolve BEFORE the incoming
            // StartTurnSequence, whose thaw loop resets SummonedThisTurn / AttacksUsedThisTurn. A corpse
            // that survives the flip would be thawed and playable on a board it should have left.
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_END, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                        amount: 5, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, selector: "ALL_MINIONS"),
                }));

            var (host, s) = Match(db, Toller, seed: 14UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);

            Play(host, s, Toller);
            Assert.That(s.Players[firstSeat].Board, Has.Count.EqualTo(1));

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Players[firstSeat].Board, Is.Empty,
                "The toller's 5-damage sweep kills itself; the corpse must be off the board before the seat flip.");
            Assert.That(s.Players[firstSeat].Graveyard.Any(c => c.DefinitionId == Toller), Is.True,
                "Death resolution must have run inside ApplyEndTurn, not been deferred past the flip.");
        }

        [Test]
        public void Turn_End_Damage_Can_End_The_Match()
        {
            // CheckWin is called explicitly after the drain because Drain only win-checks while its queue
            // is non-empty. A lethal toll must therefore still end the match rather than flip the seat.
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_END, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                        amount: 99, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, selector: "ENEMY_HERO"),
                }));

            var (host, s) = Match(db, Toller, seed: 15UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);

            Play(host, s, Toller);
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Outcome, Is.EqualTo(firstSeat == 0 ? MatchOutcome.Player0Win : MatchOutcome.Player1Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }

        [Test]
        public void A_Board_With_No_Turn_End_Cards_Is_Untouched()
        {
            // The raise must be inert for the 258 cards that carry no ON_TURN_END effect — this is what
            // keeps the three pre-existing golden fixtures green unrebaselined.
            var (host, s) = Match(Db(), Vanilla, seed: 16UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Vanilla);
            int firstHero = s.Players[firstSeat].Hero.CurrentHealth;
            int secondHero = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHero));
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHero));
            Assert.That(s.Players[firstSeat].Board, Has.Count.EqualTo(1));
        }
    }
}
