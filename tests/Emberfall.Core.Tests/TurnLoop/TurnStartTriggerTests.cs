using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.TurnLoop
{
    /// <summary>
    /// L1 — EF-011. <c>ON_TURN_START</c> was declared in the <see cref="Trigger"/> enum and raised
    /// nowhere, so a card carrying it validated, loaded and then did nothing forever. These lock the
    /// raise site's observable properties: explicit controller/opponent/any-turn qualification, deterministic
    /// minion observation, AFTER the thaw/mana reset, and BEFORE the draw. The last one is the sharp one:
    /// a lethal start-of-turn toll must end the match rather than let a corpse draw fatigue.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class TurnStartTriggerTests
    {
        private const string Toller = "ef011_toller";     // 1/3, "at the start of a turn, 1 damage to the enemy hero"
        private const string Vanilla = "ef011_vanilla";   // 1/1, no effects — the control

        private static EffectConditionDef OwnerCondition(TurnOwner owner)
            => new EffectConditionDef(ConditionType.TURN_OWNER, owner);

        private static FakeCardDatabase Db(TurnOwner owner = TurnOwner.ANY, int tollDamage = 1, int tollerHealth = 3)
        {
            var toll = new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                amount: tollDamage, amount2: null, tokenId: null, grantedKeyword: null,
                vfxOverride: null, selector: "ENEMY_HERO", condition: OwnerCondition(owner));

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
            var state = host.BeginMatch(new MatchId("EF011"), new PlayerId("P0"), new PlayerId("P1"), seed);
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

        private static CardInstance PutOnBoard(GameState s, ICardDatabase db, int seat, string cardId)
        {
            var ps = s.Players[seat];
            var def = db.Get(cardId);
            var card = new CardInstance(s.NextInstanceId(), cardId, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            ps.Board.Add(card);
            return card;
        }

        [Test]
        public void OnTurnStart_Fires_At_All()
        {
            var (host, s) = Match(Db(), Toller, seed: 21UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int before = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));   // -> second player's turn STARTS

            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(before - 1),
                "A minion with an ON_TURN_START effect must actually resolve it — the trigger was dead before EF-011.");
        }

        [Test]
        public void Missing_TurnOwner_Condition_Fails_Closed_At_Runtime()
        {
            var invalid = new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                amount: 1, amount2: null, tokenId: null, grantedKeyword: null,
                vfxOverride: null, selector: "ENEMY_HERO");
            var db = new FakeCardDatabase()
                .Register(Minion(Toller, health: 3, effects: new[] { invalid }));
            var (host, s) = Match(db, Toller, seed: 210UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int before = s.Players[secondSeat].Hero.CurrentHealth;
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(before),
                "Invalid card data must not recover by silently treating a missing condition as ANY.");
        }

        [Test]
        public void Any_Turn_Condition_Fires_On_Both_Players_Turn_Starts()
        {
            var (host, s) = Match(Db(TurnOwner.ANY), Toller, seed: 22UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int start = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));    // OPPONENT's turn starts
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 1));

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));   // owner's own turn starts
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 2),
                "ANY explicitly qualifies both the controller's and opponent's turn starts.");
        }

        [Test]
        public void Controller_Turn_Condition_Fires_Only_On_Its_Controllers_Turn()
        {
            var (host, s) = Match(Db(TurnOwner.CONTROLLER), Toller, seed: 220UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int start = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start),
                "CONTROLLER must not qualify the opponent's turn start.");

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 1),
                "CONTROLLER qualifies when the source controller is the incoming player.");
        }

        [Test]
        public void Opponent_Turn_Condition_Fires_Only_On_The_Opponents_Turn()
        {
            var (host, s) = Match(Db(TurnOwner.OPPONENT), Toller, seed: 221UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int start = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 1),
                "OPPONENT qualifies when the incoming player is not the source controller.");

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(start - 1),
                "OPPONENT must not qualify the source controller's turn start.");
        }

        [Test]
        public void Opponent_Turn_Temporary_Mana_Is_Unusable_And_Clears_At_The_Controllers_Reset()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.GAIN_MANA,
                        amount: 2, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, condition: OwnerCondition(TurnOwner.OPPONENT)),
                }));
            var (host, s) = Match(db, Toller, seed: 222UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));

            Assert.That(s.Players[firstSeat].Mana.Temporary, Is.EqualTo(2),
                "An explicitly opponent-qualified effect may grant off-turn temporary mana.");
            var offTurnCard = s.Players[firstSeat].Hand.First(c => c.DefinitionId == Toller);
            var rejected = host.Submit(new PlayCardIntent(s.Players[firstSeat].Id, offTurnCard.Id, 1,
                Array.Empty<TargetRef>()));
            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.Reason, Is.EqualTo(RejectReason.NotYourTurn),
                "There is no reaction window in which the off-turn mana can be spent.");
            Assert.That(s.Players[firstSeat].Mana.Temporary, Is.EqualTo(2),
                "A rejected off-turn play must not mutate the granted mana.");

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));

            Assert.That(s.Players[firstSeat].Mana.Temporary, Is.EqualTo(0),
                "The source controller's next start-of-turn reset clears off-turn temporary mana.");
        }

        [Test]
        public void Both_Sides_Tollers_Fire_On_A_Single_Turn_Start()
        {
            var (host, s) = Match(Db(), Toller, seed: 23UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);                                       // first player's toller
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));    // second player's turn starts
            Play(host, s, Toller);                                       // second player's toller
            int firstHero = s.Players[firstSeat].Hero.CurrentHealth;
            int secondHero = s.Players[secondSeat].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[secondSeat].Id));   // first player's turn starts

            Assert.That(s.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHero - 1),
                "Second player's toller hits the first player's hero.");
            Assert.That(s.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHero - 1),
                "First player's toller hits the second player's hero on the same turn start.");
        }

        [Test]
        public void Same_Side_Subscribers_Resolve_Left_To_Right()
        {
            const string leftId = "ef011_left";
            const string rightId = "ef011_right";
            var leftEffect = new EffectDef(Trigger.ON_TURN_START,
                Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 1, amount2: null, tokenId: null,
                grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO",
                condition: OwnerCondition(TurnOwner.CONTROLLER));
            var rightEffect = new EffectDef(Trigger.ON_TURN_START,
                Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 2, amount2: null, tokenId: null,
                grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO",
                condition: OwnerCondition(TurnOwner.CONTROLLER));
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(leftId, health: 3, effects: new[] { leftEffect }))
                .Register(Minion(rightId, health: 3, effects: new[] { rightEffect }));
            var (host, s) = Match(db, Vanilla, seed: 230UL);
            int outgoing = s.SeatOf(s.ActivePlayer);
            int incoming = 1 - outgoing;
            var left = PutOnBoard(s, db, incoming, leftId);
            var right = PutOnBoard(s, db, incoming, rightId);

            var result = Accept(host, new EndTurnIntent(s.Players[outgoing].Id));

            var damage = result.Events.Where(e => e.Kind == GameEventKind.DamageApplied).ToList();
            Assert.That(damage, Has.Count.EqualTo(2));
            Assert.That(damage[0].Source, Is.EqualTo(left.Id),
                "The incoming side's leftmost subscriber must enqueue first.");
            Assert.That(damage[0].Amount, Is.EqualTo(1));
            Assert.That(damage[1].Source, Is.EqualTo(right.Id));
            Assert.That(damage[1].Amount, Is.EqualTo(2));
        }

        [Test]
        public void Subscriber_Snapshot_Still_Resolves_A_Source_Killed_By_An_Earlier_Subscriber()
        {
            const string sweeperId = "ef011_sweeper";
            const string followerId = "ef011_follower";
            var sweep = new EffectDef(Trigger.ON_TURN_START,
                Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 5, amount2: null, tokenId: null,
                grantedKeyword: null, vfxOverride: null, selector: "ALL_MINIONS",
                condition: OwnerCondition(TurnOwner.CONTROLLER));
            var follow = new EffectDef(Trigger.ON_TURN_START,
                Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 1, amount2: null, tokenId: null,
                grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO",
                condition: OwnerCondition(TurnOwner.CONTROLLER));
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(sweeperId, health: 3, effects: new[] { sweep }))
                .Register(Minion(followerId, health: 3, effects: new[] { follow }));
            var (host, s) = Match(db, Vanilla, seed: 231UL);
            int outgoing = s.SeatOf(s.ActivePlayer);
            int incoming = 1 - outgoing;
            _ = PutOnBoard(s, db, incoming, sweeperId);
            var follower = PutOnBoard(s, db, incoming, followerId);
            int outgoingHealth = s.Players[outgoing].Hero.CurrentHealth;

            var result = Accept(host, new EndTurnIntent(s.Players[outgoing].Id));

            Assert.That(s.Players[incoming].Board, Is.Empty,
                "The leftmost subscriber's sweep kills both minions during the cascade.");
            Assert.That(s.Players[incoming].Graveyard.Any(c => c.Id == follower.Id), Is.True);
            Assert.That(s.Players[outgoing].Hero.CurrentHealth, Is.EqualTo(outgoingHealth - 1),
                "The follower was snapshotted before the drain, so its queued effect survives its death.");
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.DamageApplied && e.Source == follower.Id), Is.True);
        }

        [Test]
        public void Non_Qualifying_Ownership_Conditions_Do_Not_Consume_The_Resolution_Cap()
        {
            const string budgetId = "ef011_budget";
            var nonMatching = Enumerable.Range(0, MatchEngine.ActionResolutionCap)
                .Select(_ => new EffectDef(Trigger.ON_TURN_START,
                    Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 1, amount2: null, tokenId: null,
                    grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO",
                    condition: OwnerCondition(TurnOwner.CONTROLLER)));
            var matching = new EffectDef(Trigger.ON_TURN_START,
                Emberfall.Core.Contracts.Action.DEAL_DAMAGE, amount: 1, amount2: null, tokenId: null,
                grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO",
                condition: OwnerCondition(TurnOwner.OPPONENT));
            var effects = nonMatching.Concat(new[] { matching }).ToArray();
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(budgetId, health: 3, effects: effects));
            var (host, s) = Match(db, Vanilla, seed: 232UL);
            int outgoing = s.SeatOf(s.ActivePlayer);
            int incoming = 1 - outgoing;
            _ = PutOnBoard(s, db, outgoing, budgetId);
            int incomingHealth = s.Players[incoming].Hero.CurrentHealth;

            Accept(host, new EndTurnIntent(s.Players[outgoing].Id));

            Assert.That(s.Players[incoming].Hero.CurrentHealth, Is.EqualTo(incomingHealth - 1),
                "Only the final OPPONENT-qualified effect should consume budget and resolve.");
            Assert.That(s.ActionResolutionsThisTurn, Is.EqualTo(1),
                "Effects rejected by TURN_OWNER qualification are not action resolutions.");
        }

        [Test]
        public void Turn_Start_Trigger_Fires_After_The_Mana_Reset()
        {
            // ORDERING, first half. StartTurnSequence zeroes Mana.Temporary and refills Available before
            // it raises. Temporary crystals granted BY the trigger must therefore survive into Main — if
            // the raise were hoisted above the mana block, this reset would silently wipe them.
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.GAIN_MANA,
                        amount: 2, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, selector: null, condition: OwnerCondition(TurnOwner.CONTROLLER)),
                }));

            var (host, s) = Match(db, Toller, seed: 24UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);

            Play(host, s, Toller);
            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));
            Assert.That(s.Players[firstSeat].Mana.Temporary, Is.EqualTo(0),
                "CONTROLLER does not qualify the opponent's turn start.");
            Accept(host, new EndTurnIntent(s.Players[1 - firstSeat].Id)); // source controller is now the incoming player

            Assert.That(s.Players[firstSeat].Mana.Temporary, Is.EqualTo(2),
                "The incoming player's reset must run before its controller-qualified GAIN_MANA effect.");
        }

        [Test]
        public void Lethal_Turn_Start_Toll_Ends_The_Match_Before_The_Draw()
        {
            // ORDERING, second half — the one the EF-010 seam comment called out: settle the cascade
            // BEFORE DrawOne, "or a corpse would draw fatigue". CheckWin is called explicitly after the
            // drain because Drain only win-checks while its queue is non-empty.
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                        amount: 99, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, selector: "ENEMY_HERO", condition: OwnerCondition(TurnOwner.OPPONENT)),
                }));

            var (host, s) = Match(db, Toller, seed: 25UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;

            Play(host, s, Toller);
            int handBefore = s.Players[secondSeat].Hand.Count;

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));   // second player's turn starts, and they die on it

            Assert.That(s.Outcome, Is.EqualTo(firstSeat == 0 ? MatchOutcome.Player0Win : MatchOutcome.Player1Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
            Assert.That(s.Players[secondSeat].Hand.Count, Is.EqualTo(handBefore),
                "The match ended during the start-of-turn cascade, so the start-of-turn draw must never have happened.");
        }

        [Test]
        public void Turn_Start_Cascade_Settles_Before_The_Draw()
        {
            // A non-lethal cascade must also fully resolve first: the corpse is in the graveyard, and the
            // turn then proceeds normally into Main with its draw taken.
            var db = new FakeCardDatabase()
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Minion(Toller, health: 3, effects: new[]
                {
                    new EffectDef(Trigger.ON_TURN_START, Emberfall.Core.Contracts.Action.DEAL_DAMAGE,
                        amount: 5, amount2: null, tokenId: null, grantedKeyword: null,
                        vfxOverride: null, selector: "ALL_MINIONS", condition: OwnerCondition(TurnOwner.OPPONENT)),
                }));

            var (host, s) = Match(db, Toller, seed: 26UL);
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = 1 - firstSeat;
            int handBefore = s.Players[secondSeat].Hand.Count;

            Play(host, s, Toller);
            Assert.That(s.Players[firstSeat].Board, Has.Count.EqualTo(1));

            Accept(host, new EndTurnIntent(s.Players[firstSeat].Id));   // second player's turn starts

            Assert.That(s.Players[firstSeat].Board, Is.Empty,
                "The toller's 5-damage sweep kills itself; the corpse must be off the board before the draw.");
            Assert.That(s.Players[firstSeat].Graveyard.Any(c => c.DefinitionId == Toller), Is.True,
                "Death resolution must have run inside StartTurnSequence, not been deferred past the draw.");
            Assert.That(s.Players[secondSeat].Hand.Count, Is.EqualTo(handBefore + 1),
                "Nobody died, so the start-of-turn draw still happens after the cascade settles.");
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.Main));
        }

        [Test]
        public void A_Board_With_No_Turn_Start_Cards_Is_Untouched()
        {
            // The raise must be inert for every card that carries no ON_TURN_START effect — this is what
            // keeps the pre-existing golden fixtures green unrebaselined.
            var (host, s) = Match(Db(), Vanilla, seed: 27UL);
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
