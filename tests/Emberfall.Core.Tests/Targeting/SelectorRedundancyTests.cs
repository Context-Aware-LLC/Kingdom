using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

// The fixture's own namespace ends in Targeting, so name the engine seam explicitly.
using Engine = Emberfall.Core.Targeting;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// Measures which of the 26 selector names are the SAME selector — by resolving all of them
    /// against one board and grouping by the candidate set each actually produces, rather than by
    /// reading the names or their prose.
    ///
    /// <para><b>Why measure instead of assert a list.</b> The claim that drives the
    /// <c>TargetCriteria</c> work is that the names redundantly re-derive a criteria tuple. A claim
    /// of that shape is only worth as much as the thing that checks it, and reading 26 switch arms
    /// for equality is exactly the review that already missed them.</para>
    ///
    /// <para><b>The board separates the three source-relative selectors on purpose.</b> With FOUR
    /// friendly minions and the source at index 1, <c>ANY_FRIENDLY_MINION</c>,
    /// <c>OTHER_FRIENDLY_MINIONS</c> and <c>ADJACENT</c> produce three different sets. At three
    /// minions the latter two coincide and the measurement would report a redundancy that is an
    /// artifact of the fixture — the failure mode this test exists to avoid, met while writing it.</para>
    /// </summary>
    [TestFixture]
    public sealed class SelectorRedundancyTests
    {
        /// <summary>
        /// <b>No two selector names may resolve to the same candidate set.</b>
        ///
        /// <para>This fixture used to hold the opposite: a list of three known-redundant pairs, with
        /// a retirement trigger saying the commit that collapsed them would delete the fixture too.
        /// Half of that was right. The pairs collapsed on 2026-08-20 and the list is gone — but the
        /// CHECK is worth more now than it was, because it is the thing that stops a fourth
        /// duplicate name being minted. A name that duplicates another's query is a second spelling
        /// of one idea, and the corpus paid 12 rows of rewording to be rid of the last three.</para>
        ///
        /// <para>Note what this cannot see: two names are "the same" here only if they resolve
        /// identically ON THE SEEDED BOARD. A pair that differs solely in a zone this fixture leaves
        /// empty would look redundant and is not. That is why the board seeds every zone on both
        /// sides rather than only the ones a selector is expected to reach.</para>
        /// </summary>
        [Test]
        public void No_two_selectors_resolve_to_the_same_candidate_set()
        {
            IReadOnlyList<string[]> groups = RedundantGroups(PlayerChoiceSeam: false);

            TestContext.Out.WriteLine("Selectors grouped by the candidate set they resolve to:");
            foreach (string[] group in groups) TestContext.Out.WriteLine("  " + string.Join(" == ", group));
            if (groups.Count == 0) TestContext.Out.WriteLine("  (no two names share a set)");

            Assert.That(Rendered(groups), Is.Empty,
                "two selector names resolve to one candidate set — the name is carrying something " +
                "the criteria do not, or it is a duplicate that should never have been minted.");
        }

        /// <summary>
        /// The presentation seam collapses MORE names than the resolver does, and for an unrelated
        /// reason: hidden zones fail closed, so a selector reaching into one offers the player an
        /// empty set and every such selector looks alike from outside. This is an artifact of what a
        /// client may be shown, NOT evidence that two selectors mean the same thing — recorded here
        /// so the wider grouping is not mistaken for a wider redundancy when the two measurements
        /// are compared.
        /// </summary>
        [Test]
        public void The_player_seam_collapses_further_because_hidden_zones_fail_closed()
        {
            IReadOnlyList<string[]> groups = RedundantGroups(PlayerChoiceSeam: true);

            TestContext.Out.WriteLine("Selectors indistinguishable to a CLIENT (hidden zones fail closed):");
            foreach (string[] group in groups) TestContext.Out.WriteLine("  " + string.Join(" == ", group));

            int atResolver = RedundantGroups(PlayerChoiceSeam: false).Count;
            Assert.That(groups.Count, Is.GreaterThan(atResolver),
                "The client seam should hide at least as much as the resolver does. Measured against " +
                "the resolver seam rather than a literal, because the resolver's count reached zero " +
                "when the redundant names collapsed and a hard-coded 3 would have gone quietly stale.");
        }

        /// <summary>
        /// The load-bearing question for <c>TargetCriteria</c>: are the criteria axes enough to NAME
        /// all 26 selectors? Partition the names by the axes a criteria tuple carries — the set of
        /// sides and the set of kinds its candidates occupy — and any group whose members resolve to
        /// DIFFERENT candidate sets is a pair the tuple cannot tell apart.
        ///
        /// <para><b>Both axes are sets, and the count of occupied signatures is not a grid.</b> Two
        /// sides and six kinds; a selector admits a subset of each, so there is no cell to be empty.
        /// "No name means a hero on both sides" is a gap in the NAMES, and it stops existing the
        /// moment the axes are authored directly — which is the argument FOR the criteria object,
        /// not a hole in it to be ruled on.</para>
        ///
        /// <para><b>They are not, for five names — and the reason is one thing, not five.</b> Every name in the
        /// under-determined groups is defined by a RELATION TO THE SOURCE rather than by a property
        /// of its candidates: <c>SELF</c> is the source, <c>ADJACENT</c> is beside it,
        /// <c>OTHER_FRIENDLY_MINIONS</c> is everything-but it, <c>TRIGGER_SOURCE</c> is whatever
        /// raised the trigger. A criteria object of Team × Zone × Type + filters has nowhere to put
        /// that, so the grammar needs a source-relation axis — or those names survive the collapse
        /// as references rather than descriptions.</para>
        ///
        /// <para><b>SELF and TRIGGER_SOURCE do not even have fixed axes.</b> Their side, zone and
        /// type here are those of whatever the source happens to be — a minion, in this fixture.
        /// Change the source to a weapon and the same name lands in a different group. A name whose
        /// criteria are decided by its referent is not a criteria expression at all.</para>
        /// </summary>
        [Test]
        public void Criteria_axes_alone_cannot_name_the_source_relative_selectors()
        {
            (IReadOnlyList<string> partition, IReadOnlyList<string> underDetermined) = AxisPartition();

            TestContext.Out.WriteLine(
                "Selector names partitioned by criteria axes (sets of sides, zones and types):");
            foreach (string line in partition) TestContext.Out.WriteLine("  " + line);
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("Groups the axes CANNOT separate (same axes, different candidate sets):");
            foreach (string line in underDetermined) TestContext.Out.WriteLine("  " + line);

            Assert.That(underDetermined, Is.EqualTo(new[]
            {
                "sides {enemy}, zones {Board}, types {Minion}: ANY_ENEMY_MINION, TRIGGER_SOURCE",
                "sides {friendly}, zones {Board}, types {Minion}: ADJACENT, ANY_FRIENDLY_MINION, " +
                    "OTHER_FRIENDLY_MINIONS, SELF",
            }));
        }

        private static (IReadOnlyList<string> Partition, IReadOnlyList<string> UnderDetermined) AxisPartition()
        {
            (GameState state, FakeCardDatabase db) = BoardOfFour();
            PlayerId controller = state.Players[0].Id;
            InstanceId source = state.Players[0].Board[1].Id;
            InstanceId trigger = state.Players[1].Board[0].Id;

            var byAxes = new Dictionary<string, List<(string Name, string Set)>>();
            foreach (string selector in TargetSpec.Selectors)
            {
                IReadOnlyList<TargetCandidate> candidates = Engine.ResolutionCandidates(
                    state, db, controller, new TargetSpec(selector, TargetSelection.Of(1)), null, source, trigger);

                // EVERY axis is a SET, and none gets a name per combination. Naming the observed
                // combinations — an "Either" side, a "Hero+Minion" kind — rebuilds the vocabulary
                // this work exists to delete, one axis down. (Sam, 2026-08-18, on the first draft of
                // this fixture, which did exactly that.)
                //
                // There are THREE axes here where there used to be two, because the kind axis was
                // (zone x type) fused: `Weapon` was a zone, `Minion` a Board-zone type, and one type
                // wore three zone names in hand, deck and graveyard. Reporting the halves separately
                // is what lets a group be under-determined on one axis and separated on the other.
                string sides = Set(candidates.Select(c => c.Controller == controller ? "friendly" : "enemy"));
                string zones = Set(candidates.Select(c => c.Zone.ToString()));
                string types = Set(candidates.Select(c => TypeAxis(c, db)));
                string axes = "sides " + sides + ", zones " + zones + ", types " + types;

                if (!byAxes.TryGetValue(axes, out List<(string, string)>? members))
                    byAxes[axes] = members = new List<(string, string)>();
                members.Add((selector, string.Join(",",
                    candidates.Select(c => c.Instance.ToString()).OrderBy(s => s))));
            }

            var partition = new List<string>();
            var underDetermined = new List<string>();
            foreach (KeyValuePair<string, List<(string Name, string Set)>> group in
                     byAxes.OrderBy(g => g.Key, System.StringComparer.Ordinal))
            {
                string names = string.Join(", ", group.Value
                    .Select(m => m.Name).OrderBy(n => n, System.StringComparer.Ordinal));
                partition.Add(group.Key + ": " + names);
                if (group.Value.Select(m => m.Set).Distinct().Count() > 1)
                    underDetermined.Add(group.Key + ": " + names);
            }
            return (partition, underDetermined);
        }

        private static IReadOnlyList<string[]> RedundantGroups(bool PlayerChoiceSeam)
        {
            (GameState state, FakeCardDatabase db) = BoardOfFour();
            PlayerId controller = state.Players[0].Id;
            InstanceId source = state.Players[0].Board[1].Id;
            InstanceId trigger = state.Players[1].Board[0].Id;

            var bySet = new Dictionary<string, List<string>>();
            foreach (string selector in TargetSpec.Selectors)
            {
                // Membership, not quantity: both seams enumerate before selecting, so the term
                // is unread here and AllOf would throw on the unbounded selectors.
                var spec = new TargetSpec(selector, TargetSelection.Of(1));
                IReadOnlyList<TargetCandidate> candidates = PlayerChoiceSeam
                    ? Engine.Candidates(state, db, controller, spec, null, source, trigger)
                    : Engine.ResolutionCandidates(state, db, controller, spec, null, source, trigger);

                // Identity is the SET, not the order — order is pinned separately by
                // CandidateOrderTests, and folding the two would make each failure ambiguous.
                string key = string.Join(",", candidates.Select(c => c.Instance.ToString()).OrderBy(s => s));
                if (!bySet.TryGetValue(key, out List<string>? names)) bySet[key] = names = new List<string>();
                names.Add(selector);
            }

            return bySet.Values.Where(g => g.Count > 1)
                .Select(g => g.OrderBy(n => n, System.StringComparer.Ordinal).ToArray())
                .OrderBy(g => g[0], System.StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// The TYPE axis of one candidate, in <c>TargetTypes</c>' vocabulary. The hero is the one
        /// candidate with no definition, and the one non-card (Sam, 2026-08-20).
        ///
        /// <para><b>This has to go through the database, and the fake one lies</b> — see
        /// <c>FakeCardDatabase.DeclareWeapons</c>, which every fixture here calls for that reason.
        /// A type axis served by a database that invents types is a printed lie, and this fixture's
        /// output is read by a human.</para>
        /// </summary>
        private static string TypeAxis(TargetCandidate candidate, ICardDatabase db)
            => candidate.DefinitionId == 0
                ? TargetTypes.Hero.ToString()
                : TargetAxes.OfCard(db.Get(candidate.DefinitionId).Type).ToString();

        /// <summary>Renders an axis value as the SET it is, so no combination acquires a name.</summary>
        private static string Set(IEnumerable<string> values)
            => "{" + string.Join(",", values.Distinct().OrderBy(v => v, System.StringComparer.Ordinal)) + "}";

        private static string Rendered(IReadOnlyList<string[]> groups)
            => string.Join("\n", groups
                .Select(g => string.Join(" == ", g.OrderBy(n => n, System.StringComparer.Ordinal)))
                .OrderBy(line => line, System.StringComparer.Ordinal));

        /// <summary>
        /// Seeds every zone on both sides, PLUS one non-minion in each graveyard. Without that
        /// second type, <c>FakeCardDatabase.Get</c> fabricates a MINION row for every id here (see
        /// its own doc comment), so a graveyard holding only minions makes the new AnyCard/Minion
        /// selector pairs (<c>*_GRAVEYARD</c> vs <c>*_GRAVEYARD_MINION</c>) look redundant for a
        /// reason that has nothing to do with the selectors — exactly the fixture-artifact failure
        /// mode <see cref="No_two_selectors_resolve_to_the_same_candidate_set"/>'s own doc warns
        /// about, and the one the weapon slot already needed <c>DeclareWeapons</c> for.
        /// </summary>
        private static (GameState State, FakeCardDatabase Db) BoardOfFour()
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            int nextId = 0;

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(nextId++), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(nextId++), p1Id, CardClass.TIDECALLER, 30));

            var graveyardSpellIds = new List<int>();
            foreach (PlayerState player in new[] { p0, p1 })
            {
                for (int i = 0; i < 4; i++)
                {
                    CardInstance minion = Card(ref nextId, player.Id, Zone.Board);
                    minion.Position = i;
                    player.Board.Add(minion);
                }
                player.Weapon = Card(ref nextId, player.Id, Zone.Weapon);
                player.Weapon.CurrentDurability = 2;
                for (int i = 0; i < 2; i++)
                {
                    player.Hand.Add(Card(ref nextId, player.Id, Zone.Hand));
                    player.Deck.Add(Card(ref nextId, player.Id, Zone.Deck));
                }
                CardInstance graveyardMinion = Card(ref nextId, player.Id, Zone.Graveyard);
                player.Graveyard.Add(graveyardMinion);
                CardInstance graveyardSpell = Card(ref nextId, player.Id, Zone.Graveyard);
                player.Graveyard.Add(graveyardSpell);
                graveyardSpellIds.Add(graveyardSpell.DefinitionId);
            }

            var state = new GameState(new MatchId("m-selector-redundancy"), new DeterministicRandom(0xC0FFEEUL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = nextId,
            };

            FakeCardDatabase db = new FakeCardDatabase().DeclareWeapons(state);
            foreach (int spellDefId in graveyardSpellIds)
                db.Register(new CardDefinition(
                    id: spellDefId, handle: $"s{spellDefId}", name: $"s{spellDefId}", type: CardType.SPELL,
                    cost: 1, attack: null, health: null, durability: null,
                    @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                    keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
                    flavorText: "", artId: $"s{spellDefId}", setId: SetId.CORE,
                    collectible: true, tribe: null, factionId: ""));

            return (state, db);
        }

        private static CardInstance Card(ref int nextId, PlayerId controller, Zone zone)
        {
            int id = nextId++;
            return new CardInstance(new InstanceId(id), FixtureCard.Id($"c_{id:00}"), controller)
            {
                Zone = zone,
                CurrentAttack = 1,
                CurrentHealth = 1,
                MaxHealth = 1,
            };
        }
    }
}
