using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

// The fixture's own namespace ends in Targeting, so name the engine seam explicitly.
using Engine = Emberfall.Core.Targeting;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// Pins the canonical candidate order that <c>Targeting</c>'s class comment claims and nothing
    /// enforced: <b>controller side before opponent side, then kind, then position within the zone.</b>
    ///
    /// <para><b>Why this is a rule and not a table.</b> Order is what maps an RNG index onto an
    /// entity (<c>Targeting.ResolveTargets</c> draws <c>state.Rng.NextInt(candidates.Count)</c>), so a
    /// silent reorder diverges every RANDOM resolution in a live match with no hash movement and a
    /// green suite. The obvious guard — an expected candidate list per selector — would write a
    /// FOURTH enumeration of the 26 selector names, which is the exact duplication the effect-model
    /// work exists to delete. So the order is stated ONCE as a key over the axes and every selector
    /// is checked against it. That survives the <c>TargetCriteria</c> refactor collapsing the names,
    /// because the axes are what the names were spelling out.</para>
    ///
    /// <para><b>The zone axis is not invented here.</b> It is <see cref="Zone"/>'s own declaration
    /// order, which is why "board before card zones" needs no second list to be true. The one thing
    /// zone cannot say is that a hero precedes the minions it shares the Board with — the hero is
    /// the one non-card, so that is a TYPE fact — and it is the only part of the middle key any
    /// authored selector exercises, because every selector today produces candidates from exactly
    /// one zone. The cross-zone ranking is pinned here anyway, so a later query that spans zones
    /// cannot quietly choose a different one.</para>
    ///
    /// <para><b>This key was re-anchored on 2026-08-20</b>, when kind×zone was unfused and
    /// <c>TargetCandidateKind</c> — the enum whose declaration order WAS the middle key — ceased to
    /// exist. The hero-before-minions half is preserved exactly; the cross-zone half now follows
    /// <c>Zone</c> instead of the deleted enum, which reorders nothing observable and is written
    /// down here rather than absorbed silently.</para>
    ///
    /// <para><b>Side is controller-relative, not seat-absolute</b> — every case runs from both seats
    /// for exactly that reason. An implementation that ordered by seat index would pass from seat 0
    /// and be wrong for the player sitting in seat 1, which is the half a single-seat fixture misses.</para>
    /// </summary>
    [TestFixture]
    public sealed class CandidateOrderTests
    {
        [TestCaseSource(nameof(SelectorsFromBothSeats))]
        public void Resolution_candidates_are_sorted_by_side_then_kind_then_position(
            string selector, int controllerSeat)
        {
            GameState state = SymmetricBoard();
            PlayerId controller = state.Players[controllerSeat].Id;
            (InstanceId source, InstanceId trigger) = Anchors(state, controllerSeat);

            IReadOnlyList<TargetCandidate> candidates = Engine.ResolutionCandidates(
                state, new FakeCardDatabase().DeclareWeapons(state), controller,
                // The selection term is not read on this path — candidates are enumerated
                // before anything selects from them — so the smallest legal term will do, and
                // AllOf would throw on the unbounded deck and graveyard selectors.
                new TargetSpec(selector, TargetSelection.Of(1)),
                action: null, source: source, triggerSource: trigger);

            // A selector that enumerates nothing would satisfy any ordering claim. The board is
            // seeded so all 26 produce candidates on the resolution path; an empty result here means
            // the fixture stopped covering the selector, not that the selector is ordered.
            Assert.That(candidates, Is.Not.Empty, $"{selector} enumerated nothing — fixture gap, not a pass.");
            AssertCanonicallyOrdered(state, controller, selector, candidates);
        }

        /// <summary>
        /// The public presentation seam applies the fail-closed hidden-zone rule and the shroud and
        /// action-capability filters, all of which remove candidates. Removal must not reorder, so
        /// the same key holds — and an empty result is legitimate here (an enemy deck offers the
        /// player nothing), which is why this case does not assert coverage.
        /// </summary>
        [TestCaseSource(nameof(SelectorsFromBothSeats))]
        public void Player_choice_candidates_are_sorted_by_the_same_key(string selector, int controllerSeat)
        {
            GameState state = SymmetricBoard();
            PlayerId controller = state.Players[controllerSeat].Id;
            (InstanceId source, InstanceId trigger) = Anchors(state, controllerSeat);

            IReadOnlyList<TargetCandidate> candidates = Engine.Candidates(
                state, new FakeCardDatabase().DeclareWeapons(state), controller,
                new TargetSpec(selector, TargetSelection.Of(1)),
                action: null, source: source, triggerSource: trigger);

            AssertCanonicallyOrdered(state, controller, selector, candidates);
        }

        private static void AssertCanonicallyOrdered(GameState state, PlayerId controller,
            string selector, IReadOnlyList<TargetCandidate> candidates)
        {
            IReadOnlyDictionary<InstanceId, int> positions = PositionsWithinZone(state);
            (int, int, int, int) previous = (-1, -1, -1, -1);
            string previousLabel = "start";

            foreach (TargetCandidate candidate in candidates)
            {
                Assert.That(positions.ContainsKey(candidate.Instance), Is.True,
                    $"{selector}: candidate {candidate} is not an entity of the seeded board.");

                // The rule, stated once: side (controller first) → zone → the hero before the
                // board minions → position within its zone.
                (int, int, int, int) key = (
                    candidate.Controller == controller ? 0 : 1,
                    (int)candidate.Zone,
                    candidate.IsHero ? 0 : 1,
                    positions[candidate.Instance]);

                Assert.That(Compare(key, previous), Is.GreaterThan(0),
                    $"{selector}: {candidate} at key {key} does not follow {previousLabel} at key {previous}. " +
                    "Candidate order is the RNG index mapping — a reorder here diverges every RANDOM " +
                    "resolution without moving the state hash.");
                previous = key;
                previousLabel = candidate.ToString();
            }
        }

        private static int Compare(
            (int Side, int Zone, int Hero, int Position) a,
            (int Side, int Zone, int Hero, int Position) b)
        {
            if (a.Side != b.Side) return a.Side.CompareTo(b.Side);
            if (a.Zone != b.Zone) return a.Zone.CompareTo(b.Zone);
            if (a.Hero != b.Hero) return a.Hero.CompareTo(b.Hero);
            return a.Position.CompareTo(b.Position);
        }

        /// <summary>Index of every entity within the list that owns it; a hero and a weapon are alone in theirs.</summary>
        private static IReadOnlyDictionary<InstanceId, int> PositionsWithinZone(GameState state)
        {
            var positions = new Dictionary<InstanceId, int>();
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState player = state.Players[seat];
                positions[player.Hero.Id] = 0;
                if (player.Weapon != null) positions[player.Weapon.Id] = 0;
                Index(player.Board, positions);
                Index(player.Hand, positions);
                Index(player.Deck, positions);
                Index(player.Graveyard, positions);
            }
            return positions;
        }

        private static void Index(IReadOnlyList<CardInstance> cards, Dictionary<InstanceId, int> into)
        {
            for (int i = 0; i < cards.Count; i++) into[cards[i].Id] = i;
        }

        /// <summary>
        /// SELF, ADJACENT and OTHER_FRIENDLY_MINIONS resolve against the effect's source, and
        /// TRIGGER_SOURCE against the raising entity. The source is the MIDDLE friendly minion so
        /// ADJACENT yields two candidates rather than one — the only selector whose ordering claim
        /// has a left and a right to get backwards.
        /// </summary>
        private static (InstanceId Source, InstanceId Trigger) Anchors(GameState state, int controllerSeat)
            => (state.Players[controllerSeat].Board[1].Id,
                state.Players[controllerSeat == 0 ? 1 : 0].Board[0].Id);

        private static IEnumerable<TestCaseData> SelectorsFromBothSeats()
        {
            foreach (string selector in TargetSpec.Selectors)
                for (int seat = 0; seat < 2; seat++)
                    yield return new TestCaseData(selector, seat).SetArgDisplayNames(selector, $"seat{seat}");
        }

        /// <summary>
        /// Both seats hold three board minions, a weapon and two cards in each of hand, deck and
        /// graveyard — so every one of the 26 selectors enumerates at least two candidates from at
        /// least one side, and no selector's ordering claim is vacuous.
        /// </summary>
        private static GameState SymmetricBoard()
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            int nextId = 0;

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(nextId++), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(nextId++), p1Id, CardClass.TIDECALLER, 30));

            foreach (PlayerState player in new[] { p0, p1 })
            {
                for (int i = 0; i < 3; i++)
                {
                    CardInstance minion = Card(ref nextId, player.Id, Zone.Board, "minion");
                    minion.Position = i;
                    player.Board.Add(minion);
                }
                player.Weapon = Card(ref nextId, player.Id, Zone.Weapon, "weapon");
                player.Weapon.CurrentDurability = 2;
                for (int i = 0; i < 2; i++)
                {
                    player.Hand.Add(Card(ref nextId, player.Id, Zone.Hand, "hand"));
                    player.Deck.Add(Card(ref nextId, player.Id, Zone.Deck, "deck"));
                    player.Graveyard.Add(Card(ref nextId, player.Id, Zone.Graveyard, "grave"));
                }
            }

            return new GameState(new MatchId("m-candidate-order"), new DeterministicRandom(0xC0FFEEUL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = nextId,
            };
        }

        private static CardInstance Card(ref int nextId, PlayerId controller, Zone zone, string prefix)
        {
            int id = nextId++;
            return new CardInstance(new InstanceId(id), $"{prefix}_{id:00}", controller)
            {
                Zone = zone,
                CurrentAttack = 1,
                CurrentHealth = 1,
                MaxHealth = 1,
            };
        }
    }
}
