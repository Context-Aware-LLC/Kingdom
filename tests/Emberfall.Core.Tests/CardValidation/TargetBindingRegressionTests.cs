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
        public void All_261_Live_Cards_Explicitly_Bind_Each_Multi_Rule_Player_Choice()
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

            Assert.That(cards, Has.Count.EqualTo(261));
            Assert.That(unbound, Is.Empty,
                "cards with multiple simultaneous player choices must name their bindings");
        }

        [Test]
        public void Shipped_Tidal_Emissary_Offers_And_Executes_Incompatible_Selectors_Independently()
        {
            CardDefinition emissary = LoadAllSets()
                .Single(card => card.Id == "rekind_tide_tidal_emissary");
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(emissary).Register(body);
            GameState state = State();
            CardInstance friendly = Board(state, 0, body);
            CardInstance enemy = Board(state, 1, body);

            IReadOnlyList<TargetChoiceRequirement> plan = Targeting.PlayerChoicePlan(
                state, db, state.Players[0].Id, emissary, Trigger.ON_PLAY);

            Assert.That(plan.Select(requirement => requirement.Binding),
                Is.EqualTo(new[] { "frost_target", "reinforce_target" }));
            Assert.That(plan[0].Candidates.Select(candidate => candidate.Instance),
                Does.Contain(enemy.Id));
            Assert.That(plan[0].Candidates.Select(candidate => candidate.Instance),
                Does.Not.Contain(friendly.Id));
            Assert.That(plan[1].Candidates.Select(candidate => candidate.Instance),
                Is.EqualTo(new[] { friendly.Id }));

            CardInstance card = ZoneCard(state, 0, emissary, Zone.Hand);
            CommandResult accepted = new CommandProcessor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, card.Id, 1,
                new[]
                {
                    new TargetRef(state.Players[1].Hero.Id, "frost_target"),
                    new TargetRef(friendly.Id, "reinforce_target"),
                }));

            Assert.That(accepted.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.IsFrosted, Is.True);
            Assert.That((friendly.CurrentAttack, friendly.CurrentHealth, friendly.MaxHealth),
                Is.EqualTo((2, 4, 4)));
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
