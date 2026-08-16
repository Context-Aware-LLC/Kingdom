using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class TargetBindingRegressionTests
    {
        [Test]
        public void Multiple_Player_Chosen_Rules_Require_Explicit_Bindings()
        {
            RuleDefinition PlayerRule(string id, RuleAction action) => new RuleDefinition(
                new RuleId(id), new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, action);
            CardDefinition def = Spell("unbound",
                PlayerRule("damage", new DamageAction(1, new TargetSpec("ANY_ENEMY"))),
                PlayerRule("buff", new ModifyStatsAction(1, 1,
                    new TargetSpec("ANY_FRIENDLY_MINION"))));
            var db = new FakeCardDatabase().Register(def);

            ValidationReport report = new CardValidator().Validate(def, db);

            Assert.That(report.Issues.Where(issue =>
                    issue.Code == "C14G-TARGET-BINDING-REQUIRED").Select(issue => issue.Field),
                Is.EqualTo(new[]
                {
                    "rules[0].action.target.binding",
                    "rules[1].action.target.binding",
                }));
        }

        [Test]
        public void All_Live_Cards_Explicitly_Bind_Each_Multi_Rule_Player_Choice()
        {
            IReadOnlyList<CardDefinition> cards = LoadAllSets();
            var unbound = cards.SelectMany(card => card.Rules
                    .GroupBy(RuleSemantics.TriggerOf)
                    .Select(group => new
                    {
                        Card = card.Id,
                        Trigger = group.Key,
                        Targets = group.Select(rule => RuleSemantics.TargetOf(rule.Action))
                            .Where(target => Targeting.IsPlayerChosen(target))
                            .Cast<TargetSpec>()
                            .ToList(),
                    }))
                .Where(group => group.Targets.Count > 1)
                .Where(group => group.Targets.Any(target => target.Binding.Length == 0))
                .Select(group => $"{group.Card}:{group.Trigger}")
                .ToList();

            Assert.That(cards, Has.Count.GreaterThan(200), "Card corpus failed to load.");
            Assert.That(unbound, Is.Empty,
                "cards with multiple simultaneous player choices must name their bindings");
        }

        /// <summary>
        /// The runtime half of the binding contract, against a REAL shipped card: two player
        /// choices on one trigger must be offered as two independently-bound requirements with
        /// their own candidate sets, and must resolve to whichever instance the player named for
        /// that binding — not positionally.
        ///
        /// <para>This used to be pinned to <c>tide_tidal_emissary</c>, which carried a FROST
        /// choice and a REINFORCE choice. Sam deliberately dropped its REINFORCE half on
        /// 2026-08-15, leaving it a single-choice card that cannot exercise this at all. Repointed
        /// at <c>ember_rekindled_flame</c>, which has the same shape and a sharper version
        /// of it: the two selectors OVERLAP (ANY_MINION and ANY_FRIENDLY_MINION both contain the
        /// friendly minion), so a positional or set-based resolver would still look correct on a
        /// card whose candidate sets were disjoint. Here it cannot.</para>
        ///
        /// <para>Seven other shipped cards have this shape today, so if this one loses its second
        /// choice as well, repoint rather than delete — the list is any card with two
        /// player-chosen rules on one trigger.</para>
        /// </summary>
        [Test]
        public void Shipped_Rekindled_Flame_Offers_And_Executes_Overlapping_Selectors_Independently()
        {
            CardDefinition flame = LoadAllSets()
                .Single(card => card.Id == "ember_rekindled_flame");
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(flame).Register(body);
            GameState state = State();
            CardInstance friendly = Board(state, 0, body);
            CardInstance enemy = Board(state, 1, body);

            IReadOnlyList<TargetChoiceRequirement> plan = Targeting.PlayerChoicePlan(
                state, db, state.Players[0].Id, flame, Trigger.ON_CAST);

            Assert.That(plan.Select(requirement => requirement.Binding),
                Is.EqualTo(new[] { "damage_target", "reinforce_target" }));

            // ANY_MINION sees both boards; ANY_FRIENDLY_MINION sees one. The overlap is the point.
            Assert.That(plan[0].Candidates.Select(candidate => candidate.Instance),
                Does.Contain(enemy.Id).And.Contains(friendly.Id));
            Assert.That(plan[1].Candidates.Select(candidate => candidate.Instance),
                Is.EqualTo(new[] { friendly.Id }));

            int enemyHealthBefore = enemy.CurrentHealth;
            var friendlyBefore = (friendly.CurrentAttack, friendly.CurrentHealth, friendly.MaxHealth);

            CardInstance card = ZoneCard(state, 0, flame, Zone.Hand);
            CommandResult accepted = new CommandProcessor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, card.Id, 3,
                new[]
                {
                    new TargetRef(enemy.Id, "damage_target"),
                    new TargetRef(friendly.Id, "reinforce_target"),
                }));

            Assert.That(accepted.Accepted, Is.True);
            // The damage went to the named instance, and NOT to the friendly minion that was also
            // a legal candidate for that same binding.
            Assert.That(enemy.CurrentHealth, Is.EqualTo(enemyHealthBefore - 4),
                "damage_target resolved to the instance the player named.");
            Assert.That((friendly.CurrentAttack, friendly.CurrentHealth, friendly.MaxHealth),
                Is.Not.EqualTo(friendlyBefore),
                "reinforce_target resolved to the friendly minion and changed it.");
        }

        private static IReadOnlyList<CardDefinition> LoadAllSets()
        {
            var cards = new List<CardDefinition>();
            foreach (string file in new[] { "Emberfall.cards.json", "Rekindling.cards.json" })
            {
                string path = Path.Combine(RepoPaths.RepoRoot,
                    "unity", "Emberfall", "Assets", "CardData", file);
                cards.AddRange(CardJson.DeserializeArray(File.ReadAllText(path)));
            }
            return cards;
        }

        private static CardDefinition Spell(string id, params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), rules,
                "", "art/" + id, SetId.CORE, true, null, "");

        private static CardDefinition Minion(string id)
            => new CardDefinition(id, id, CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE,
                true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("target-bindings"), new DeterministicRandom(42),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Max = state.Players[0].Mana.Available = 10;
            state.Players[1].Mana.Max = state.Players[1].Mana.Available = 10;
            return state;
        }

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CardInstance ZoneCard(GameState state, int seat, CardDefinition def, Zone zone)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = zone,
            };
            state.Players[seat].Hand.Add(card);
            return card;
        }
    }
}
