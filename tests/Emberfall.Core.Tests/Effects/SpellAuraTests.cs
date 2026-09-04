using System;
using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Effects
{
    /// <summary>
    /// An aura cast by a SPELL, which has no board presence to bound it and must declare its own end.
    ///
    /// <para><b>The failure this fixture is built around is the one from
    /// <see cref="AuraDomainTests"/>: an aura that validates and reaches NOBODY.</b> It is silent
    /// from every angle — no exception, no warning, a card that simply does nothing — and a spell
    /// aura has two fresh ways to land in it that a minion aura does not. First, the fold has to
    /// find a source that is not on the board at all. Second, <c>SELF</c> and <c>ADJACENT</c> are
    /// positional, and an <see cref="OngoingEffectInstance"/> has no position, so <c>Targeting</c>
    /// answers them with the empty set and reports nothing wrong. So the load-bearing assertions
    /// here COUNT what was reached, and the positional selectors are asserted to be refused at the
    /// gate rather than merely to reach nobody at runtime.</para>
    ///
    /// <para><b>The second thing being guarded is the clock.</b> <c>uses</c> is decremented from the
    /// resolution path for every other ongoing rule, and an aura never resolves — so before the tick
    /// site in <c>CommandProcessor.ExpireControllerAuras</c> existed, a FINITE aura was a PERMANENT
    /// one wearing a number. That is why the duration tests below end turns and count, rather than
    /// asserting on <c>RemainingUses</c>: the field moving is not evidence that the aura stopped.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SpellAuraTests
    {
        private static readonly int Anthem = FixtureCard.Id("aura_spell_anthem");
        private static readonly int Grunt = FixtureCard.Id("aura_spell_grunt");

        // ── the capability relation ───────────────────────────────────────────────

        /// <summary>
        /// The whole of the widening: what bounds an aura is a function of WHAT CASTS IT. A minion's
        /// aura lives as long as the minion does and may say nothing else; a spell is gone the moment
        /// it resolves, so it must name a duration and may not borrow the minion's.
        /// </summary>
        [Test]
        public void An_auras_legal_lifetime_depends_on_its_source_type()
        {
            RuleDefinition Aura(RuleLifetime lifetime)
                => new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                    AlwaysCondition.Instance, lifetime,
                    new ModifyStatsAction(1, 1, Sweep("ANY_FRIENDLY_MINION")));

            Assert.Multiple(() =>
            {
                Assert.That(AuraCapabilities.Supports(CardType.MINION, Aura(SourceBoundLifetime.Instance)),
                    Is.True, "a minion aura is bounded by the minion");
                Assert.That(AuraCapabilities.Supports(CardType.MINION, Aura(new FiniteLifetime(3))),
                    Is.False, "a minion may not declare its own duration — it already has one");
                Assert.That(AuraCapabilities.Supports(CardType.MINION, Aura(PermanentLifetime.Instance)),
                    Is.False, "a permanent minion aura would outlive the minion");

                Assert.That(AuraCapabilities.Supports(CardType.SPELL, Aura(SourceBoundLifetime.Instance)),
                    Is.False, "a spell has no source to be bound to — this is the shape that would "
                            + "produce an aura with no end and no carrier");
                Assert.That(AuraCapabilities.Supports(CardType.SPELL, Aura(OneShotLifetime.Instance)),
                    Is.True, "this turn");
                Assert.That(AuraCapabilities.Supports(CardType.SPELL, Aura(new FiniteLifetime(3))),
                    Is.True, "for three turns");
                Assert.That(AuraCapabilities.Supports(CardType.SPELL, Aura(PermanentLifetime.Instance)),
                    Is.True, "the rest of the game");
                Assert.That(AuraCapabilities.Supports(CardType.SPELL, Aura(new FiniteLifetime(0))),
                    Is.False, "a zero-turn duration is an authoring error, not a no-op");
            });
        }

        /// <summary>
        /// A WEAPON and a HERO_POWER get no aura at all, and they are the reason
        /// <c>LifetimeFits</c> is a switch with a default rather than a "not a minion" test.
        /// Nothing folds them, so admitting either would be the silent-empty bug by another door.
        /// </summary>
        [Test]
        public void Only_a_minion_or_a_spell_may_declare_an_aura()
        {
            var admitted = new[] { CardType.MINION, CardType.SPELL, CardType.WEAPON, CardType.HERO_POWER }
                .Where(t => new RuleLifetime[]
                    {
                        SourceBoundLifetime.Instance, OneShotLifetime.Instance,
                        new FiniteLifetime(2), PermanentLifetime.Instance,
                    }
                    .Any(l => AuraCapabilities.Supports(t, new RuleDefinition(new RuleId("aura"),
                        AuraActivation.Instance, AlwaysCondition.Instance, l,
                        new ModifyStatsAction(1, 1, Sweep("ANY_FRIENDLY_MINION"))))))
                .ToArray();

            TestContext.Out.WriteLine("Card types that may declare an aura: "
                + string.Join(", ", admitted));
            Assert.That(admitted, Is.EqualTo(new[] { CardType.MINION, CardType.SPELL }));
        }

        /// <summary>
        /// <c>SELF</c> and <c>ADJACENT</c> ask where the source is standing. A spell's aura is
        /// carried by an <see cref="OngoingEffectInstance"/>, which has an id and a controller and no
        /// board slot, so <c>Targeting</c> resolves both to the empty set WITHOUT reporting a
        /// problem. Refusing them at the gate is what turns a card that silently does nothing into a
        /// card that does not validate.
        /// </summary>
        [TestCase("SELF")]
        [TestCase("ADJACENT")]
        public void A_positional_selector_is_minion_only(string selector)
        {
            var rule = new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                AlwaysCondition.Instance, new FiniteLifetime(2),
                new ModifyStatsAction(1, 1, Sweep(selector)));
            var minionRule = new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                new ModifyStatsAction(1, 1, Sweep(selector)));

            Assert.Multiple(() =>
            {
                Assert.That(AuraCapabilities.Supports(CardType.SPELL, rule), Is.False,
                    $"{selector} has no meaning without a board position, and resolves to the empty "
                    + "set rather than failing — so it must be refused here.");
                Assert.That(AuraCapabilities.Supports(CardType.MINION, minionRule), Is.True,
                    $"{selector} is still legal on a minion — this widening took nothing away.");
            });
        }

        // ── the fold ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The aura reaches the board from a source that is not on it. An EMPTY result here is the
        /// specific bug this fixture exists for.
        /// </summary>
        [Test]
        public void A_spell_aura_reaches_the_board_although_its_source_is_not_on_it()
        {
            var (host, s) = MatchWithAura(new FiniteLifetime(2), "ANY_FRIENDLY_MINION");
            int seat = s.SeatOf(s.ActivePlayer);
            PlayMinion(host, s);
            PlayMinion(host, s);
            CastAnthem(host, s);

            Assert.That(s.OngoingEffects, Has.Count.EqualTo(1),
                "the spell must leave exactly one carrierless observer behind");
            Assert.That(Buffed(s, seat), Is.EqualTo(2),
                "the aura reached nobody — the source is an OngoingEffectInstance, so the fold has "
                + "to walk OngoingEffects and not just the two boards.");
            Assert.That(s.Players[seat].Board.All(m => m.CurrentAttack == 2), Is.True,
                "reached, but the stat delta did not land");
        }

        /// <summary>
        /// A minion summoned AFTER the spell resolved is inside the aura too. This is what makes it
        /// a continuous fold rather than a one-time buff applied at cast, and it is the property a
        /// "just apply an enchantment on resolution" shortcut would fail.
        /// </summary>
        [Test]
        public void A_spell_aura_reaches_minions_that_arrive_after_it()
        {
            var (host, s) = MatchWithAura(new FiniteLifetime(2), "ANY_FRIENDLY_MINION");
            int seat = s.SeatOf(s.ActivePlayer);
            CastAnthem(host, s);
            Assert.That(Buffed(s, seat), Is.Zero, "nothing on the board to reach yet");

            PlayMinion(host, s);

            Assert.That(Buffed(s, seat), Is.EqualTo(1),
                "a minion played under a live aura must be caught by it");
        }

        // ── the clock ─────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>The seat filter, which is the whole reason the number on the card is honest.</b> Both
        /// seats are walked at every turn boundary, so a tick that did not filter by controller would
        /// spend a 2-turn aura in a single round trip — "for 2 turns" would last a turn and a half.
        ///
        /// <para>Asserted by counting the buff across a full round, not by reading
        /// <c>RemainingUses</c>: the field moving is not evidence that the fold stopped.</para>
        /// </summary>
        [Test]
        public void A_finite_spell_aura_spends_one_use_per_its_own_controllers_turn()
        {
            var (host, s) = MatchWithAura(new FiniteLifetime(2), "ANY_FRIENDLY_MINION");
            int caster = s.SeatOf(s.ActivePlayer);
            PlayMinion(host, s);
            CastAnthem(host, s);
            Assert.That(Buffed(s, caster), Is.EqualTo(1), "cast turn");

            EndTurn(host, s);                                  // caster's turn 1 ends — one use spent
            Assert.That(Buffed(s, caster), Is.EqualTo(1),
                "still live: two turns means two of the CASTER's turns");

            EndTurn(host, s);                                  // opponent's turn ends — NOT the caster's
            Assert.That(Buffed(s, caster), Is.EqualTo(1),
                "the opponent's turn end must not age the caster's aura — an unfiltered tick would "
                + "have expired it here, half a round early.");

            EndTurn(host, s);                                  // caster's turn 2 ends — last use spent
            Assert.That(Buffed(s, caster), Is.Zero, "two of the caster's turns are up");
            Assert.That(s.OngoingEffects, Is.Empty, "the expired aura must leave the list");
        }

        /// <summary>
        /// Expiry restores the board exactly, and does so with NO un-apply path anywhere.
        /// <c>DerivedStats.RemoveAuras</c> is provenance-blind — it strips every aura enchantment on
        /// every pass and the fold re-lays them from live sources — so an aura that is gone from
        /// <c>OngoingEffects</c> simply stops being re-applied. If someone later adds a "remove my
        /// enchantments" step to expiry, this test keeps passing and that step is dead code; if
        /// someone removes the <c>Recompute</c> call after the tick, this is what fails.
        /// </summary>
        [Test]
        public void An_expired_spell_aura_leaves_no_trace_on_the_board()
        {
            var (host, s) = MatchWithAura(OneShotLifetime.Instance, "ANY_FRIENDLY_MINION");
            int caster = s.SeatOf(s.ActivePlayer);
            PlayMinion(host, s);
            CardInstance minion = s.Players[caster].Board[0];
            int baseAttack = minion.CurrentAttack;
            int baseHealth = minion.MaxHealth;

            CastAnthem(host, s);
            Assert.That(minion.CurrentAttack, Is.EqualTo(baseAttack + 1), "the aura never applied");

            EndTurn(host, s);

            Assert.Multiple(() =>
            {
                Assert.That(minion.Enchantments.Any(e => e.IsAura), Is.False,
                    "an aura enchantment outlived its source");
                Assert.That(minion.CurrentAttack, Is.EqualTo(baseAttack), "attack not restored");
                Assert.That(minion.MaxHealth, Is.EqualTo(baseHealth), "health not restored");
            });
        }

        /// <summary>ONE_SHOT on an aura is "this turn" — one tick, taken at the caster's own turn end.</summary>
        [Test]
        public void A_one_shot_spell_aura_lasts_exactly_the_turn_it_was_cast_on()
        {
            var (host, s) = MatchWithAura(OneShotLifetime.Instance, "ANY_FRIENDLY_MINION");
            int caster = s.SeatOf(s.ActivePlayer);
            PlayMinion(host, s);
            CastAnthem(host, s);
            Assert.That(Buffed(s, caster), Is.EqualTo(1));

            EndTurn(host, s);

            Assert.That(Buffed(s, caster), Is.Zero, "ONE_SHOT must not survive its own turn");
        }

        /// <summary>
        /// PERMANENT is the case the tick must NOT touch. <c>ConsumeOngoingEffect</c> returns
        /// "not expired" for it and leaves the count null; a tick that treated a null count as zero
        /// would delete every permanent aura at the first turn end.
        /// </summary>
        [Test]
        public void A_permanent_spell_aura_survives_many_turns()
        {
            var (host, s) = MatchWithAura(PermanentLifetime.Instance, "ANY_FRIENDLY_MINION");
            int caster = s.SeatOf(s.ActivePlayer);
            PlayMinion(host, s);
            CastAnthem(host, s);

            for (int i = 0; i < 6; i++) EndTurn(host, s);

            Assert.Multiple(() =>
            {
                Assert.That(s.OngoingEffects, Has.Count.EqualTo(1), "a permanent aura was aged away");
                Assert.That(Buffed(s, caster), Is.EqualTo(1), "still on the board, still folding");
            });
        }

        // ── the text ──────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>uses</c> counts FIRINGS on a triggered rule and TURNS on an aura — one field, two
        /// clocks — so the generated text has to name the right one. "For its next 3 uses" on a
        /// continuous effect promises a player three firings of something that never fires.
        /// </summary>
        [TestCase(1, "This turn, ")]
        [TestCase(3, "For 3 turns, ")]
        public void A_finite_aura_is_rendered_in_turns_never_in_uses(int uses, string expected)
        {
            string text = Rendered(new FiniteLifetime(uses));

            Assert.That(text, Does.StartWith("Aura: " + expected));
            Assert.That(text, Does.Not.Contain("use"),
                "an aura never resolves, so it has no 'uses' to spend");
        }

        [Test]
        public void An_endless_aura_announces_no_duration()
        {
            Assert.That(Rendered(PermanentLifetime.Instance), Is.EqualTo(Rendered(SourceBoundLifetime.Instance,
                CardType.MINION)), "a permanent spell aura and a minion's aura read the same to a "
                + "player: on until something takes it away.");
        }

        private static string Rendered(RuleLifetime lifetime, CardType type = CardType.SPELL)
        {
            var def = type == CardType.SPELL
                ? SpellCard(Anthem, lifetime, "ANY_FRIENDLY_MINION")
                : MinionCard(Grunt, new[]
                {
                    new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                        AlwaysCondition.Instance, lifetime,
                        new ModifyStatsAction(1, 1, Sweep("ANY_FRIENDLY_MINION"))),
                });
            return string.Join(" ", CardTextGenerator.Generate(def, new FakeCardDatabase()));
        }

        // ── scaffolding ───────────────────────────────────────────────────────────

        /// <summary>Every candidate of a selector — an aura may hold no chooser and no dice.</summary>
        private static TargetSpec Sweep(string selector)
        {
            int? max = TargetQueries.Get(selector).MaxCandidates;
            return new TargetSpec(selector,
                max.HasValue ? TargetSelection.Of(max.Value) : TargetSelection.Of(1));
        }

        /// <summary>Minions the given seat has that are carrying an aura enchantment.</summary>
        private static int Buffed(GameState s, int seat)
            => s.Players[seat].Board.Count(m => m.Enchantments.Any(e => e.IsAura));

        private static CardDefinition SpellCard(int id, RuleLifetime lifetime, string selector)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                type: CardType.SPELL, cost: 0,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(),
                rules: new[]
                {
                    new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                        AlwaysCondition.Instance, lifetime,
                        new ModifyStatsAction(1, 1, Sweep(selector))),
                },
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static CardDefinition MinionCard(int id, IReadOnlyList<RuleDefinition> rules)
            => new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id),
                // Free, so a scenario can put several on the board on turn one — these fixtures are
                // about the aura's reach and duration, never about the mana curve.
                type: CardType.MINION, cost: 0,
                attack: 1, health: 2, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: "art/" + FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "");

        private static (LocalMatchHost host, GameState state) MatchWithAura(
            RuleLifetime lifetime, string selector)
        {
            var db = new FakeCardDatabase()
                .Register(SpellCard(Anthem, lifetime, selector))
                .Register(MinionCard(Grunt, Array.Empty<RuleDefinition>()));

            var deck = new List<int>();
            for (int i = 0; i < 10; i++) { deck.Add(Grunt); deck.Add(Anthem); }

            var setup = new MatchSetup(CardClass.EMBERWARD, deck, CardClass.TIDECALLER, deck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var state = host.BeginMatch(new MatchId("SPELLAURA"), new PlayerId("P0"), new PlayerId("P1"), 7UL);
            Accept(host, new MulliganIntent(state.Players[0].Id, Array.Empty<InstanceId>()));
            Accept(host, new MulliganIntent(state.Players[1].Id, Array.Empty<InstanceId>()));
            return (host, state);
        }

        private static void Accept(LocalMatchHost host, PlayerIntent intent)
        {
            var r = host.Submit(intent);
            Assert.That(r.Accepted, Is.True, $"{intent.GetType().Name} rejected: {r.Reason}");
        }

        private static void EndTurn(LocalMatchHost host, GameState s)
            => Accept(host, new EndTurnIntent(s.ActivePlayer));

        private static void CastAnthem(LocalMatchHost host, GameState s) => PlayFromHand(host, s, Anthem);

        private static void PlayMinion(LocalMatchHost host, GameState s) => PlayFromHand(host, s, Grunt);

        /// <summary>
        /// Play the active player's first copy of a card, moving it into hand from the deck if the
        /// shuffle did not deal one — these scenarios are about the aura, not the draw.
        /// </summary>
        private static void PlayFromHand(LocalMatchHost host, GameState s, int cardId)
        {
            var ps = s.Players[s.SeatOf(s.ActivePlayer)];
            var card = ps.Hand.FirstOrDefault(c => c.DefinitionId == cardId);
            if (card == null)
            {
                card = ps.Deck.First(c => c.DefinitionId == cardId);
                ps.Deck.Remove(card);
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
            }
            Accept(host, new PlayCardIntent(s.ActivePlayer, card.Id, ps.Board.Count,
                Array.Empty<TargetRef>()));
        }
    }
}
