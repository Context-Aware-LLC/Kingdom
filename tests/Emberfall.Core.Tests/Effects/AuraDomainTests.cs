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
        /// <para>The rejections are the half worth reading. <c>OTHER_FRIENDLY_MINIONS</c> is a
        /// coherent aura the engine could now serve and is deliberately still out — widening a
        /// fail-closed capability is a ruling, not a refactor. Character-domain selectors are out
        /// because a stat aura on a hero means nothing, and <c>TRIGGER_SOURCE</c> because a
        /// continuous fold has no trigger to read.</para>
        /// </summary>
        [Test]
        public void Exactly_five_selectors_may_declare_an_aura()
        {
            var admitted = TargetSpec.Selectors
                .Where(s => AuraCapabilities.Supports(CardType.MINION, AuraRule(s)))
                .OrderBy(s => s, System.StringComparer.Ordinal)
                .ToArray();

            TestContext.Out.WriteLine("Selectors that may declare an aura: " + string.Join(", ", admitted));

            Assert.That(admitted, Is.EqualTo(new[]
            {
                "ADJACENT", "ANY_ENEMY_MINION", "ANY_FRIENDLY_MINION", "ANY_MINION", "SELF",
            }));
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
