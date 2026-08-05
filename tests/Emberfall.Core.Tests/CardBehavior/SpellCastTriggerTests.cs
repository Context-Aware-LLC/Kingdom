using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// L1 — EF-016. <c>ON_SPELL_CAST</c> was declared in the <see cref="Trigger"/> enum and raised nowhere,
    /// so a card carrying it validated, loaded and then did nothing forever. These lock the raise site's
    /// four observable properties: that it fires at all; that a HERO POWER does <b>not</b> fire it (the trap
    /// — hero powers reach the interpreter as an ephemeral HERO_POWER card raising the same
    /// <see cref="Trigger.ON_CAST"/>, so a generic hook would have caught them); that it fires on BOTH
    /// players' casts (the scope decision, matching EF-012's <c>ON_TURN_END</c>); and that it resolves
    /// AFTER the spell's own <c>ON_CAST</c>, so a reaction sees the board the spell left behind.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SpellCastTriggerTests
    {
        private const string Echo = "ef016_echo";           // 1/3, "whenever a spell is cast, 1 damage to the enemy hero"
        private const string Bolt = "ef016_bolt";           // 0-cost spell, 2 damage to the enemy hero
        private const string Vanilla = "ef016_vanilla";     // 1/1, no effects — the control
        private const string HeroPowerId = "ef016_hero_power";

        // ── card scaffolding ──────────────────────────────────────────────────────

        private static EffectDef Damage(Trigger trigger, int amount, string selector)
            => new EffectDef(trigger, Verb.DEAL_DAMAGE, amount: amount, amount2: null,
                tokenId: null, grantedKeyword: null, selector: selector);

        private static CardDefinition Minion(string id, int health, EffectDef[] effects)
            => new CardDefinition(
                id: id, name: id, type: CardType.MINION, cost: 1,
                attack: 1, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: effects,
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        private static CardDefinition Spell(string id, int cost, EffectDef[] effects)
            => new CardDefinition(
                id: id, name: id, type: CardType.SPELL, cost: cost,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: effects,
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        private static CardDefinition HeroPower(string id, EffectDef[] effects)
            => new CardDefinition(
                id: id, name: id, type: CardType.HERO_POWER, cost: 2,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: effects,
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: false, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        /// <param name="echoDamage">damage the echo deals to the enemy hero on every spell cast.</param>
        private static FakeCardDatabase Db(int echoDamage = 1, int echoHealth = 3)
            => new FakeCardDatabase()
                .Register(Minion(Echo, echoHealth, new[] { Damage(Trigger.ON_SPELL_CAST, echoDamage, "ENEMY_HERO") }))
                .Register(Minion(Vanilla, health: 1, effects: Array.Empty<EffectDef>()))
                .Register(Spell(Bolt, cost: 0, effects: new[] { Damage(Trigger.ON_CAST, 2, "ENEMY_HERO") }))
                // The hero power deals damage too — the SAME verb the echo uses — so "the echo did not
                // fire" cannot be confused with "the hero power did nothing".
                .Register(HeroPower(HeroPowerId, new[] { Damage(Trigger.ON_CAST, 1, "ENEMY_HERO") }));

        // ── match scaffolding ─────────────────────────────────────────────────────

        /// <summary>A match on a deck of <paramref name="deck"/> ids, already past mulligan.</summary>
        private static (LocalMatchHost host, GameState state) Match(ICardDatabase db, IEnumerable<string> deck, ulong seed)
        {
            var list = deck.ToList();
            var setup = new MatchSetup(CardClass.EMBERWARD, list, CardClass.TIDECALLER, list);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var state = host.BeginMatch(new MatchId("EF016"), new PlayerId("P0"), new PlayerId("P1"), seed);
            Accept(host, new MulliganIntent(state.Players[0].Id, Array.Empty<InstanceId>()));
            Accept(host, new MulliganIntent(state.Players[1].Id, Array.Empty<InstanceId>()));
            // Both heroes take the fixture's hero power so the trap test is seat-independent.
            state.Players[0].Hero.HeroPowerId = HeroPowerId;
            state.Players[1].Hero.HeroPowerId = HeroPowerId;
            return (host, state);
        }

        /// <summary>Alternating echo/bolt deck — deep enough that both are always somewhere in hand.</summary>
        private static List<string> MixedDeck()
        {
            var d = new List<string>();
            for (int i = 0; i < 10; i++) { d.Add(Echo); d.Add(Bolt); }
            return d;
        }

        private static CommandResult Accept(LocalMatchHost host, PlayerIntent intent)
        {
            var r = host.Submit(intent);
            Assert.That(r.Accepted, Is.True, $"{intent.GetType().Name} rejected: {r.Reason}");
            return r;
        }

        /// <summary>
        /// Play the active player's first copy of <paramref name="cardId"/>, moving it into hand from the
        /// deck first if the shuffle did not deal one — the scenarios are about the trigger, not the draw.
        /// </summary>
        private static (CardInstance card, CommandResult result) Play(
            LocalMatchHost host, GameState s, string cardId, int slot = 0)
        {
            int seat = s.SeatOf(s.ActivePlayer);
            var ps = s.Players[seat];
            var card = ps.Hand.FirstOrDefault(c => c.DefinitionId == cardId);
            if (card == null)
            {
                card = ps.Deck.First(c => c.DefinitionId == cardId);
                ps.Deck.Remove(card);
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
            }
            var result = Accept(host, new PlayCardIntent(s.ActivePlayer, card.Id, slot, Array.Empty<TargetRef>()));
            return (card, result);
        }

        // ── the four properties ───────────────────────────────────────────────────

        [Test]
        public void OnSpellCast_Fires_At_All()
        {
            var (host, s) = Match(Db(), MixedDeck(), seed: 21UL);
            int seat = s.SeatOf(s.ActivePlayer);
            int foe = 1 - seat;

            _ = Play(host, s, Echo);
            int before = s.Players[foe].Hero.CurrentHealth;

            _ = Play(host, s, Bolt);

            Assert.That(s.Players[foe].Hero.CurrentHealth, Is.EqualTo(before - 3),
                "2 from the spell's own ON_CAST + 1 from the echo's ON_SPELL_CAST. The trigger was dead before EF-016.");
        }

        [Test]
        public void A_Hero_Power_Does_Not_Fire_OnSpellCast()
        {
            // THE TRAP. ApplyHeroPower raises Trigger.ON_CAST through an ephemeral HERO_POWER CardInstance,
            // so hooking generic ON_CAST would have fired spell reactions on every hero power. The raise is
            // gated to ApplyPlayCard's CardType.SPELL arm; this is the test that proves the gate holds.
            var (host, s) = Match(Db(), MixedDeck(), seed: 22UL);
            int seat = s.SeatOf(s.ActivePlayer);
            int foe = 1 - seat;

            _ = Play(host, s, Echo);

            // Turn 1 leaves no mana for a 2-cost power; take it on this player's next turn.
            Accept(host, new EndTurnIntent(s.Players[seat].Id));
            Accept(host, new EndTurnIntent(s.Players[foe].Id));
            int before = s.Players[foe].Hero.CurrentHealth;

            Accept(host, new HeroPowerIntent(s.Players[seat].Id, Array.Empty<TargetRef>()));

            Assert.That(s.Players[foe].Hero.CurrentHealth, Is.EqualTo(before - 1),
                "The hero power's own 1 damage and NOTHING else — a hero power is not a spell cast, so the "
                + "echo standing on the board must not have fired. If this reads -2, ON_SPELL_CAST has been "
                + "hooked to generic ON_CAST.");
        }

        [Test]
        public void OnSpellCast_Fires_On_Both_Players_Casts()
        {
            // The scope decision: RaiseObservers sweeps BOTH boards, so the trigger is "whenever ANY spell
            // is cast", not "whenever YOU cast a spell" — the name carries no FRIENDLY_ qualifier the way
            // ON_FRIENDLY_SUMMON does. If this ever becomes controller-scoped, this test changes deliberately.
            var (host, s) = Match(Db(), MixedDeck(), seed: 23UL);
            int first = s.SeatOf(s.ActivePlayer);
            int second = 1 - first;

            _ = Play(host, s, Echo);                                        // first player's echo
            int secondHero = s.Players[second].Hero.CurrentHealth;
            Accept(host, new EndTurnIntent(s.Players[first].Id));

            int firstHero = s.Players[first].Hero.CurrentHealth;
            _ = Play(host, s, Bolt);                                        // the OPPONENT casts

            Assert.That(s.Players[first].Hero.CurrentHealth, Is.EqualTo(firstHero - 2),
                "The opponent's bolt hits the first player's hero for its own 2.");
            Assert.That(s.Players[second].Hero.CurrentHealth, Is.EqualTo(secondHero - 1),
                "The echo belongs to the first player but must also fire on the SECOND player's cast.");
        }

        [Test]
        public void Both_Sides_Echoes_Fire_On_A_Single_Cast()
        {
            var (host, s) = Match(Db(), MixedDeck(), seed: 24UL);
            int first = s.SeatOf(s.ActivePlayer);
            int second = 1 - first;

            _ = Play(host, s, Echo);
            Accept(host, new EndTurnIntent(s.Players[first].Id));
            _ = Play(host, s, Echo);                                        // second player's echo
            int firstHero = s.Players[first].Hero.CurrentHealth;
            int secondHero = s.Players[second].Hero.CurrentHealth;

            _ = Play(host, s, Bolt);                                        // second player casts

            Assert.That(s.Players[first].Hero.CurrentHealth, Is.EqualTo(firstHero - 3),
                "2 from the bolt + 1 from the second player's own echo.");
            Assert.That(s.Players[second].Hero.CurrentHealth, Is.EqualTo(secondHero - 1),
                "The first player's echo fires on the same cast.");
        }

        [Test]
        public void The_Spells_Own_Effect_Resolves_Before_The_Observers()
        {
            // Queue order, not just totals: RaiseSelf(ON_CAST) is called before RaiseObservers and the
            // interpreter's queue is FIFO, so the spell lands first and reactions follow. Totals cannot
            // show this — both effects damage the same hero — so assert on the ordered event stream, where
            // each DamageApplied fact carries the instance id of the source that dealt it.
            var (host, s) = Match(Db(), MixedDeck(), seed: 25UL);
            int seat = s.SeatOf(s.ActivePlayer);

            var (echo, _) = Play(host, s, Echo);
            var (bolt, cast) = Play(host, s, Bolt);

            var damage = cast.Events.Where(e => e.Kind == GameEventKind.DamageApplied).ToList();
            Assert.That(damage, Has.Count.EqualTo(2), "One hit from the spell, one from the echo.");
            Assert.That(damage[0].Source, Is.EqualTo(bolt.Id),
                "The spell's own ON_CAST must resolve first — it was enqueued first.");
            Assert.That(damage[0].Amount, Is.EqualTo(2));
            Assert.That(damage[1].Source, Is.EqualTo(echo.Id),
                "The ON_SPELL_CAST reaction resolves after it.");
            Assert.That(damage[1].Amount, Is.EqualTo(1));
        }

        [Test]
        public void An_Observer_Killed_By_The_Spell_Still_Reacts()
        {
            // Subscribers are SNAPSHOTTED AT CAST TIME, not re-checked at resolution: RaiseObservers walks
            // the boards and enqueues before Drain runs anything, so an echo the spell itself kills has
            // already been enqueued and still fires from the graveyard. This is not specific to EF-016 —
            // it is the shape RaiseObservers has always had, shared with ON_FRIENDLY_SUMMON and
            // ON_TURN_END. Pinned here because it is invisible on inspection and it is the first thing a
            // card designer will assume works the other way. Making it "only living subscribers react"
            // is a design ruling on RaiseObservers, not a fix to this raise site.
            var db = new FakeCardDatabase()
                .Register(Minion(Echo, health: 3, effects: new[] { Damage(Trigger.ON_SPELL_CAST, 1, "ENEMY_HERO") }))
                .Register(Spell(Bolt, cost: 0, effects: new[] { Damage(Trigger.ON_CAST, 5, "ALL_MINIONS") }))
                .Register(HeroPower(HeroPowerId, new[] { Damage(Trigger.ON_CAST, 1, "ENEMY_HERO") }));

            var (host, s) = Match(db, MixedDeck(), seed: 28UL);
            int seat = s.SeatOf(s.ActivePlayer);
            int foe = 1 - seat;

            _ = Play(host, s, Echo);
            int foeHero = s.Players[foe].Hero.CurrentHealth;

            _ = Play(host, s, Bolt);

            Assert.That(s.Players[seat].Board, Is.Empty, "The bolt's 5-damage sweep kills the echo.");
            Assert.That(s.Players[foe].Hero.CurrentHealth, Is.EqualTo(foeHero - 1),
                "The echo was enqueued while it was alive, so its reaction still resolves after it dies.");
        }

        [Test]
        public void A_Board_With_No_Spell_Cast_Cards_Is_Untouched()
        {
            // The raise must be inert for every card that carries no ON_SPELL_CAST effect — this is what
            // keeps the pre-existing golden fixtures green unrebaselined.
            var (host, s) = Match(Db(), new[] { Vanilla, Bolt }.SelectMany(id => Enumerable.Repeat(id, 10)), seed: 26UL);
            int seat = s.SeatOf(s.ActivePlayer);
            int foe = 1 - seat;

            _ = Play(host, s, Vanilla);
            int mine = s.Players[seat].Hero.CurrentHealth;
            int theirs = s.Players[foe].Hero.CurrentHealth;

            _ = Play(host, s, Bolt);

            Assert.That(s.Players[foe].Hero.CurrentHealth, Is.EqualTo(theirs - 2),
                "Only the spell's own effect lands.");
            Assert.That(s.Players[seat].Hero.CurrentHealth, Is.EqualTo(mine));
            Assert.That(s.Players[seat].Board, Has.Count.EqualTo(1));
        }

        [Test]
        public void Every_Subscriber_On_Both_Boards_Fires_Exactly_Once()
        {
            // RaiseObservers enqueues one QueuedEffect per subscribed permanent per sweep. With two echoes
            // on one board and one on the other, a single cast must produce exactly three reactions —
            // not one per board, and not one per effect re-entered.
            var (host, s) = Match(Db(), MixedDeck(), seed: 27UL);
            int first = s.SeatOf(s.ActivePlayer);
            int second = 1 - first;

            _ = Play(host, s, Echo, slot: 0);
            Accept(host, new EndTurnIntent(s.Players[first].Id));
            _ = Play(host, s, Echo, slot: 0);
            Accept(host, new EndTurnIntent(s.Players[second].Id));
            _ = Play(host, s, Echo, slot: 1);                               // first player now has two

            int firstHero = s.Players[first].Hero.CurrentHealth;
            int secondHero = s.Players[second].Hero.CurrentHealth;
            _ = Play(host, s, Bolt);

            Assert.That(s.Players[second].Hero.CurrentHealth, Is.EqualTo(secondHero - 4),
                "2 from the bolt + 1 from each of the caster's two echoes.");
            Assert.That(s.Players[first].Hero.CurrentHealth, Is.EqualTo(firstHero - 1),
                "The single opposing echo fires once.");
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.InProgress));
        }
    }
}
