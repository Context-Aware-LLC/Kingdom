using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Effects
{
    /// <summary>
    /// Which minions an aura reaches, and which selectors may declare one.
    ///
    /// <para><b>Why this fixture exists.</b> On 2026-08-20 <c>AuraEngine.Targets</c> and
    /// <c>AuraCapabilities</c> were deduplicated — both held their own copy of the same five
    /// selector names, the duplication <c>docs/effect-model-fork.md</c> names as
    /// "<c>AuraEngine.Targets</c> beside <c>Targeting</c>". Measured before touching them: the AURA
    /// activation parses, and <b>zero shipped rows and zero tests used it</b>. Both files were
    /// entirely uncovered, so the suite was green before the rewrite, green after, and silent about
    /// whether it was correct. A green suite over uncovered code is not evidence, and rewriting
    /// uncovered code without first covering it is how a silent regression ships.</para>
    ///
    /// <para><b>The failure mode being guarded.</b> The two lists were never checked against each
    /// other. A selector the gate admitted but the runtime switch lacked fell through to an empty
    /// sequence — an aura that validated and then affected NOBODY, with no exception and no
    /// warning. So the load-bearing assertions here are the ones that count how many minions were
    /// reached: an aura that reaches nothing is the exact bug, and it looks like success from
    /// every other angle.</para>
    /// </summary>
    [TestFixture]
    public sealed class AuraDomainTests
    {
        // Seat 0 holds three minions, seat 1 holds two. The source is seat 0's MIDDLE minion, so
        // "adjacent" is a strict subset of "friendly" and neither coincides with "all" — a board
        // where two domains happened to agree would report a pass that means nothing.
        private const int SourceIndex = 1;

        [TestCase("SELF", "a0:1")]
        [TestCase("ANY_FRIENDLY_MINION", "a0:0,a0:1,a0:2")]
        [TestCase("ANY_ENEMY_MINION", "a1:0,a1:1")]
        [TestCase("ANY_MINION", "a0:0,a0:1,a0:2,a1:0,a1:1")]
        [TestCase("ADJACENT", "a0:0,a0:2")]
        [TestCase("OTHER_FRIENDLY_MINIONS", "a0:0,a0:2")]
        public void An_aura_reaches_exactly_the_minions_its_selector_names(string selector, string expected)
        {
            (GameState state, FakeCardDatabase db) = BoardWithAura(selector);

            AuraEngine.Recompute(state, db);

            Assert.That(Buffed(state), Is.EqualTo(expected),
                $"{selector}: wrong aura domain. An EMPTY result is the specific failure this " +
                "fixture exists for — it is what a gate and a runtime that disagree produce.");
        }

        /// <summary>
        /// The aura is continuous, so it must survive a recompute unchanged rather than stacking.
        /// <c>Recompute</c> removes aura provenance and reapplies from scratch; a domain that
        /// widened on the second pass, or an enchantment that failed to be removed, shows up here
        /// and nowhere else.
        /// </summary>
        [TestCase("ANY_FRIENDLY_MINION")]
        [TestCase("ADJACENT")]
        [TestCase("OTHER_FRIENDLY_MINIONS")]
        [TestCase("ANY_MINION")]
        public void Recomputing_an_aura_is_idempotent(string selector)
        {
            (GameState state, FakeCardDatabase db) = BoardWithAura(selector);

            AuraEngine.Recompute(state, db);
            string once = Buffed(state);
            IReadOnlyList<int> attackAfterOne = Attacks(state);

            AuraEngine.Recompute(state, db);

            Assert.That(Buffed(state), Is.EqualTo(once), $"{selector}: domain moved on recompute.");
            Assert.That(Attacks(state), Is.EqualTo(attackAfterOne), $"{selector}: buff stacked on recompute.");
        }

        /// <summary>
        /// The admitted set, pinned. The dedup replaced two hand-kept lists of five names with one
        /// test on the query's SHAPE, and this is the claim that the swap admitted no more and no
        /// less than before.
        ///
        /// <para>The rejections are the half worth reading. <c>OTHER_FRIENDLY_MINIONS</c> was held out
        /// until Sam ruled it in (2026-09-30). Character-domain selectors are out
        /// because a stat aura on a hero means nothing, and <c>TRIGGER_SOURCE</c> because a
        /// continuous fold has no trigger to read.</para>
        /// </summary>
        [Test]
        public void Exactly_six_selectors_may_declare_an_aura()
        {
            var admitted = TargetSpec.Selectors
                .Where(s => AuraCapabilities.Supports(CardType.MINION, AuraRule(s)))
                .OrderBy(s => s, System.StringComparer.Ordinal)
                .ToArray();

            TestContext.Out.WriteLine("Selectors that may declare an aura: " + string.Join(", ", admitted));

            Assert.That(admitted, Is.EqualTo(new[]
            {
                "ADJACENT", "ANY_ENEMY_MINION", "ANY_FRIENDLY_MINION", "ANY_MINION",
                "OTHER_FRIENDLY_MINIONS", "SELF",
            }));
        }

        /// <summary>"Other than the source" needs a source on the board; a spell's aura has none.</summary>
        [Test]
        public void Other_friendly_minions_is_minion_only()
        {
            var rule = new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                AlwaysCondition.Instance, PermanentLifetime.Instance,
                new ModifyStatsAction(1, 1, new TargetSpec("OTHER_FRIENDLY_MINIONS",
                    EveryCandidateOf("OTHER_FRIENDLY_MINIONS"))));

            Assert.That(AuraCapabilities.Supports(CardType.SPELL, rule), Is.False);
        }

        /// <summary>An aura is a continuous fold, so it can hold no chooser and no dice.</summary>
        [Test]
        public void A_chosen_or_random_domain_is_not_an_aura(
            [Values("player", "random")] string mode)
        {
            // ANY_FRIENDLY_MINION tops out at seven, so a count of one is short of every
            // candidate and therefore a player pick — nothing needs to say "player" out loud.
            TargetSelection selection = mode == "player"
                ? TargetSelection.Of(1)
                : TargetSelection.RandomOf(1);
            var rule = new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                new ModifyStatsAction(1, 1, new TargetSpec("ANY_FRIENDLY_MINION", selection)));

            Assert.That(AuraCapabilities.Supports(CardType.MINION, rule), Is.False);
        }

        /// <summary>
        /// A tribe filter narrows an aura's domain and does not empty it (Sam, 2026-08-25).
        ///
        /// <para>The assertion that matters is the COUNT, for the reason in this fixture's header:
        /// before the widening, <c>AuraCapabilities</c> vetoed every filter while
        /// <c>Targeting.ResolutionCandidates</c> had always applied them, so the gate was strictly
        /// stricter than the engine. Getting that backwards in the other direction — admitting a
        /// filter the runtime drops — would buff the WHOLE board and read as a working card, and
        /// admitting one the runtime over-applies reaches nobody. Both are named below so a failure
        /// says which happened.</para>
        /// </summary>
        [Test]
        public void A_tribe_filtered_aura_reaches_only_that_tribe()
        {
            (GameState state, FakeCardDatabase db) = TribalBoard(Tribe.CONSTRUCT);

            AuraEngine.Recompute(state, db);

            // Seat 0 is [Construct, SOURCE (tribeless), Construct]; seat 1 is [Construct, Beast].
            Assert.That(Buffed(state), Is.EqualTo("a0:0,a0:2"),
                "a tribe-filtered aura must reach its own side's Constructs and nothing else. "
                + "EMPTY means the runtime over-applied the filter; the full board means it dropped it; "
                + "including a1:0 means the selector's side was lost.");
        }

        /// <summary>
        /// The source itself is excluded by being TRIBELESS, not by a rule — which is the whole of
        /// why <c>clock_nameless_bell</c> can carry a Construct lord without buffing itself. If a
        /// future change makes the source's own tribe implicit, this is where it surfaces.
        /// </summary>
        [Test]
        public void A_tribeless_source_is_not_caught_by_its_own_tribal_aura()
        {
            (GameState state, FakeCardDatabase db) = TribalBoard(Tribe.CONSTRUCT);

            AuraEngine.Recompute(state, db);

            Assert.That(state.Players[0].Board[SourceIndex].Enchantments.Any(e => e.IsAura), Is.False,
                "the source has no tribe, so its own Construct aura must not reach it.");
        }

        /// <summary>
        /// Which filters may fold. Tribe is static, so the fold settles in one pass; DAMAGED reads
        /// current health and a stat aura WRITES current health, so selecting on it is a feedback
        /// loop with no examined fixed point. A whitelist of one field, not a veto of one — see
        /// <c>AuraCapabilities.IsFoldableFilter</c>.
        /// </summary>
        [Test]
        public void A_damaged_filter_is_not_an_aura_but_a_tribe_filter_is()
        {
            RuleDefinition Filtered(TargetFilterDef filter)
                => new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                    AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                    new ModifyStatsAction(1, 1,
                        new TargetSpec("ANY_FRIENDLY_MINION", EveryCandidateOf("ANY_FRIENDLY_MINION"),
                            filter: filter)));

            Assert.Multiple(() =>
            {
                Assert.That(AuraCapabilities.Supports(CardType.MINION,
                    Filtered(new TargetFilterDef(tribe: Tribe.CONSTRUCT))), Is.True, "tribe");
                Assert.That(AuraCapabilities.Supports(CardType.MINION,
                    Filtered(new TargetFilterDef(damaged: true))), Is.False, "damaged");
                Assert.That(AuraCapabilities.Supports(CardType.MINION,
                    Filtered(new TargetFilterDef(damaged: true, tribe: Tribe.CONSTRUCT))), Is.False,
                    "damaged rides in alongside a tribe");
                // NegateTribe is a new facet of the one foldable axis and nobody has ruled it safe
                // to recompute continuously yet -- the whitelist fails it closed (Sam, 2026-09-05).
                Assert.That(AuraCapabilities.Supports(CardType.MINION,
                    Filtered(new TargetFilterDef(tribe: Tribe.CONSTRUCT, negateTribe: true))), Is.False,
                    "negated tribe");
            });
        }

        /// <summary>
        /// Seat 0 is [Construct, tribeless SOURCE, Construct]; seat 1 is [Construct, Beast]. Every
        /// domain the assertions distinguish is a different set: the enemy Construct separates
        /// side-loss from filter-loss, and the Beast separates filter-loss from no-filter.
        /// </summary>
        private static (GameState, FakeCardDatabase) TribalBoard(Tribe tribe)
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            int nextId = 0;

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(nextId++), p0Id, CardClass.CLOCKWRIGHT, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(nextId++), p1Id, CardClass.TIDECALLER, 30));

            var db = new FakeCardDatabase();
            int constructId = FixtureCard.Id("aura_tribal_construct");
            int beastId = FixtureCard.Id("aura_tribal_beast");
            int sourceId = FixtureCard.Id("aura_tribal_source");

            db.Register(Tribal(constructId, Tribe.CONSTRUCT, System.Array.Empty<RuleDefinition>()));
            db.Register(Tribal(beastId, Tribe.BEAST, System.Array.Empty<RuleDefinition>()));
            db.Register(Tribal(sourceId, null, new[]
            {
                new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                    AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                    new ModifyStatsAction(1, 1,
                        new TargetSpec("ANY_FRIENDLY_MINION", EveryCandidateOf("ANY_FRIENDLY_MINION"),
                            filter: new TargetFilterDef(tribe: tribe)))),
            }));

            var layout = new[]
            {
                (p0, new[] { constructId, sourceId, constructId }),
                (p1, new[] { constructId, beastId }),
            };

            foreach ((PlayerState player, int[] ids) in layout)
                for (int i = 0; i < ids.Length; i++)
                    player.Board.Add(new CardInstance(new InstanceId(nextId++), ids[i], player.Id)
                    {
                        Zone = Zone.Board,
                        CurrentAttack = 1,
                        CurrentHealth = 1,
                        MaxHealth = 1,
                        Position = i,
                    });

            var state = new GameState(new MatchId("m-aura-tribe"), new DeterministicRandom(0xA1CEUL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = nextId,
            };
            return (state, db);
        }

        private static CardDefinition Tribal(int id, Tribe? tribe, IReadOnlyList<RuleDefinition> rules)
            => TestCards.Authored(new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id), type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: tribe, factionId: ""));

        private static RuleDefinition AuraRule(string selector)
            => new RuleDefinition(new RuleId("aura"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                new ModifyStatsAction(1, 0, new TargetSpec(selector, EveryCandidateOf(selector))));

        /// <summary>
        /// Every candidate of a selector, or a single pick where nothing bounds it. The deck and
        /// graveyard selectors have no maximum, so "every candidate" has no count there — and a
        /// selection that is not every candidate is exactly what an aura must refuse, which is the
        /// answer this fixture wants for them anyway.
        /// </summary>
        private static TargetSelection EveryCandidateOf(string selector)
        {
            int? max = TargetQueries.Get(selector).MaxCandidates;
            return max.HasValue ? TargetSelection.Of(max.Value) : TargetSelection.Of(1);
        }

        /// <summary>Every minion the aura reached, as "seat:index" — the board read back positionally
        /// so an assertion names places rather than instance ids nobody can check by eye.</summary>
        private static string Buffed(GameState state)
        {
            var hit = new List<string>();
            for (int seat = 0; seat < 2; seat++)
                for (int i = 0; i < state.Players[seat].Board.Count; i++)
                    if (state.Players[seat].Board[i].Enchantments.Any(e => e.IsAura))
                        hit.Add($"a{seat}:{i}");
            return string.Join(",", hit);
        }

        private static IReadOnlyList<int> Attacks(GameState state)
            => Enumerable.Range(0, 2)
                .SelectMany(seat => state.Players[seat].Board.Select(m => m.CurrentAttack))
                .ToArray();

        private static (GameState, FakeCardDatabase) BoardWithAura(string selector)
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            int nextId = 0;

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(nextId++), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(nextId++), p1Id, CardClass.TIDECALLER, 30));

            var db = new FakeCardDatabase();
            int plainId = FixtureCard.Id("aura_plain_minion");
            int sourceId = FixtureCard.Id("aura_source_minion");

            db.Register(Minion(plainId, System.Array.Empty<RuleDefinition>()));
            db.Register(Minion(sourceId, new[] { AuraRule(selector) }));

            foreach ((PlayerState player, int count) in new[] { (p0, 3), (p1, 2) })
                for (int i = 0; i < count; i++)
                {
                    bool isSource = ReferenceEquals(player, p0) && i == SourceIndex;
                    var minion = new CardInstance(new InstanceId(nextId++),
                        isSource ? sourceId : plainId, player.Id)
                    {
                        Zone = Zone.Board,
                        CurrentAttack = 1,
                        CurrentHealth = 1,
                        MaxHealth = 1,
                        Position = i,
                    };
                    player.Board.Add(minion);
                }

            var state = new GameState(new MatchId("m-aura-domain"), new DeterministicRandom(0xA1CEUL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = nextId,
            };
            return (state, db);
        }

        private static CardDefinition Minion(int id, IReadOnlyList<RuleDefinition> rules)
            => TestCards.Authored(new CardDefinition(
                id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id), type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: FixtureCard.Handle(id), setId: SetId.CORE,
                collectible: true, tribe: null, factionId: ""));
    }
}
