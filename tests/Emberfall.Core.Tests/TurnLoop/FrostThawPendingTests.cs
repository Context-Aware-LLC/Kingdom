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
    /// NOR-367 — a freeze always costs a FULL turn. NOR-355 made frost non-inert by thawing at the END
    /// of the frozen player's turn, which is correct for the ordinary enemy-freeze case but under-charged
    /// a unit frozen DURING its own turn (the warden's ON_DEATH trap): it thawed at the end of the very
    /// turn it was frozen on and then attacked freely next turn. The two-step FrostThawPending flag fixes
    /// that: frost is armed on apply, disarmed at the START of the frozen unit's own turn, and the
    /// end-of-turn thaw only clears NON-pending frost — so a mid-own-turn freeze survives into the unit's
    /// next full turn. The enemy-freeze path is unchanged and must not gain a double penalty.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FrostThawPendingTests
    {
        private const string Attacker = "nor367_attacker";      // 2/3 vanilla — survives a counter, can attack
        private const string Warden   = "nor367_warden";        // 1/1, ON_DEATH FROST ENEMY_MINIONS (the warden)
        private const string Caster   = "nor367_frost_caster";  // 1/1, ON_PLAY FROST ENEMY_MINIONS (the frost verb)

        private static FakeCardDatabase Db()
        {
            var wardenFrost = TestRules.Rule("warden_frost", Trigger.ON_DEATH,
                new FrostAction(new TargetSpec("ENEMY_MINIONS")));
            var casterFrost = TestRules.Rule("caster_frost", Trigger.ON_PLAY,
                new FrostAction(new TargetSpec("ENEMY_MINIONS")));

            return new FakeCardDatabase()
                .Register(Minion(Attacker, attack: 2, health: 3, rules: Array.Empty<RuleDefinition>()))
                .Register(Minion(Warden, attack: 1, health: 1, rules: new[] { wardenFrost }))
                .Register(Minion(Caster, attack: 1, health: 1, rules: new[] { casterFrost }));
        }

        private static CardDefinition Minion(string id, int attack, int health, RuleDefinition[] rules)
            => new CardDefinition(
                id: id, name: id, type: CardType.MINION, cost: 1,
                attack: attack, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static (LocalMatchHost host, GameState state) Match(ICardDatabase db, ulong seed)
        {
            var deck = Enumerable.Repeat(Attacker, 20).ToList();
            var setup = new MatchSetup(CardClass.EMBERWARD, deck, CardClass.TIDECALLER, deck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var state = host.BeginMatch(new MatchId("NOR367"), new PlayerId("P0"), new PlayerId("P1"), seed);
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

        // ── The fix: a unit frozen DURING its own turn owes its next full turn ───────────────
        [Test]
        public void Frost_During_Own_Turn_Costs_The_Next_Full_Turn()
        {
            var db = Db();
            var (host, s) = Match(db, seed: 367UL);
            int me = s.SeatOf(s.ActivePlayer);
            int opp = 1 - me;

            var attacker = PutOnBoard(s, db, me, Attacker);   // my minion, will be frozen mid-combat
            var warden = PutOnBoard(s, db, opp, Warden);      // enemy warden, dies when I attack it

            // I attack the warden on MY turn. It dies and its ON_DEATH frosts my board — a freeze
            // applied during the frozen unit's OWN turn.
            Accept(host, new AttackIntent(s.Players[me].Id, attacker.Id, new TargetRef(warden.Id)));
            Assert.Multiple(() =>
            {
                Assert.That(s.Players[opp].Board, Is.Empty, "warden died");
                Assert.That(attacker.IsFrosted, Is.True, "attacker frosted by the warden's ON_DEATH");
                Assert.That(attacker.FrostThawPending, Is.True,
                    "frozen during its own turn — thaw is armed pending, so it must NOT clear at this turn's end");
            });

            // End MY turn. Old (NOR-355) behavior thawed here; the fix holds the frost because it is
            // still pending — the unit has not yet served a full frozen turn.
            Accept(host, new EndTurnIntent(s.Players[me].Id));   // -> opponent's turn
            Assert.Multiple(() =>
            {
                Assert.That(attacker.IsFrosted, Is.True,
                    "frost does NOT thaw at the end of the turn it was applied on (this is the NOR-367 fix)");
                Assert.That(attacker.FrostThawPending, Is.True,
                    "still pending: the opponent's turn-start clears the opponent's units, not mine");
            });

            // End the opponent's turn -> my next turn STARTS, which disarms my pending flag.
            Accept(host, new EndTurnIntent(s.Players[opp].Id));  // -> my turn again (the full turn frost costs)
            Assert.That(s.SeatOf(s.ActivePlayer), Is.EqualTo(me), "it is my turn again");
            Assert.Multiple(() =>
            {
                Assert.That(attacker.IsFrosted, Is.True,
                    "still frozen on my next turn — the freeze costs a FULL turn, not just the remainder of one");
                Assert.That(attacker.FrostThawPending, Is.False, "disarmed at my turn start; it will thaw when I end this turn");
            });

            var blocked = host.Submit(new AttackIntent(s.Players[me].Id, attacker.Id,
                new TargetRef(s.Players[opp].Hero.Id)));
            Assert.That(blocked.Accepted, Is.False, "the frozen unit cannot attack on the full turn it is made to sit out");
            Assert.That(blocked.Reason, Is.EqualTo(RejectReason.AttackerFrosted));

            // End my full frozen turn -> now it thaws (no longer pending).
            Accept(host, new EndTurnIntent(s.Players[me].Id));
            Assert.That(attacker.IsFrosted, Is.False, "thaws at the end of the full turn it served frozen");
        }

        // ── Unchanged: an enemy freeze costs exactly one turn, never two ─────────────────────
        [Test]
        public void Enemy_Freeze_Costs_Exactly_One_Turn_No_Double_Penalty()
        {
            var db = Db();
            var (host, s) = Match(db, seed: 368UL);
            int me = s.SeatOf(s.ActivePlayer);
            int opp = 1 - me;

            var victim = PutOnBoard(s, db, opp, Attacker);   // enemy minion I will freeze

            // Play a frost caster from hand on MY turn: the real FROST verb applied to an ENEMY minion.
            var caster = new CardInstance(s.NextInstanceId(), Caster, s.Players[me].Id) { Zone = Zone.Hand };
            s.Players[me].Hand.Add(caster);
            Accept(host, new PlayCardIntent(s.Players[me].Id, caster.Id, 0, Array.Empty<TargetRef>()));
            Assert.Multiple(() =>
            {
                Assert.That(victim.IsFrosted, Is.True, "enemy minion frosted by the frost verb");
                Assert.That(victim.FrostThawPending, Is.True, "armed on apply");
            });

            // End my turn -> the enemy's turn starts, which disarms the flag on their units.
            Accept(host, new EndTurnIntent(s.Players[me].Id));   // -> enemy's turn
            Assert.Multiple(() =>
            {
                Assert.That(victim.IsFrosted, Is.True, "still frozen through the enemy's whole turn");
                Assert.That(victim.FrostThawPending, Is.False,
                    "disarmed at the enemy's turn start — the enemy was frozen BEFORE this turn, so it thaws at its end");
            });

            var blocked = host.Submit(new AttackIntent(s.Players[opp].Id, victim.Id,
                new TargetRef(s.Players[me].Hero.Id)));
            Assert.That(blocked.Accepted, Is.False, "frozen enemy cannot attack on its one lost turn");
            Assert.That(blocked.Reason, Is.EqualTo(RejectReason.AttackerFrosted));

            // End the enemy's turn -> it thaws (was not pending). One turn lost, no more.
            Accept(host, new EndTurnIntent(s.Players[opp].Id));  // -> my turn
            Assert.That(victim.IsFrosted, Is.False, "thaws at the end of the single turn it lost");

            // Back to the enemy's next turn: it can attack — no double penalty.
            Accept(host, new EndTurnIntent(s.Players[me].Id));   // -> enemy's turn again
            var allowed = host.Submit(new AttackIntent(s.Players[opp].Id, victim.Id,
                new TargetRef(s.Players[me].Hero.Id)));
            Assert.That(allowed.Accepted, Is.True, "the freeze cost exactly one turn — the unit attacks freely the turn after");
        }
    }
}
