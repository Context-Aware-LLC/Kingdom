using System;
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
    public sealed class CardTextGeneratorTests
    {
        [Test]
        public void Derives_Toll_Marker_From_Death_Trigger_Without_A_Keyword()
        {
            var rule = new RuleDefinition(new RuleId("break_draw"),
                new TriggeredActivation(Trigger.ON_DEATH), AlwaysCondition.Instance,
                SourceBoundLifetime.Instance, new DrawAction(1));

            string text = CardTextGenerator.Generate(new[] { rule });

            Assert.That(text, Is.EqualTo("[Toll]: Draw a card."));
        }

        [Test]
        public void Mainspring_Halberd_Rules_Render_A_Derived_Toll_Marker()
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "Emberfall.cards.json");
            CardDefinition mainspring = CardJson.DeserializeArray(File.ReadAllText(path))
                .Single(card => card.Id == "clock_mainspring_halberd");

            Assert.That(CardTextGenerator.Generate(mainspring), Is.EqualTo("[Toll]: Draw a card."));
            Assert.That(mainspring.Keywords, Is.Empty);
        }

        [Test]
        public void Card_Rendering_Includes_Keyword_Only_Carrier_Behaviour()
        {
            var card = new CardDefinition("keyword_only", "Keyword Only", CardType.MINION, 1,
                1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK, Keyword.LIFEDRAIN }, Array.Empty<RuleDefinition>(),
                "", "art/keyword_only", SetId.CORE, true, null, "");

            Assert.That(CardTextGenerator.Generate(card), Is.EqualTo("[Bulwark] [Lifedrain]"));
        }

        [Test]
        public void Card_Rendering_Derives_Trigger_Markers_Without_Duplicating_Them()
        {
            var card = new CardDefinition("herald", "Herald", CardType.MINION, 1,
                1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK }, new[] { DrawRule(new TriggeredActivation(Trigger.ON_PLAY)) },
                "", "art/herald", SetId.CORE, true, null, "");

            Assert.That(CardTextGenerator.Generate(card), Is.EqualTo("[Bulwark] [Herald]: Draw a card."));
        }

        [Test]
        public void Action_Markers_Are_Derived_Exactly_Once_Even_For_Legacy_Carrier_Data()
        {
            var rows = new (Keyword marker, RuleAction action, string expected)[]
            {
                (Keyword.BACKDRAFT, new BackdraftAction(2), "[Backdraft] (2)."),
                // REINFORCE renders no marker at all any more — it prints its stats like the
                // MODIFY_STATS +1/+1 it is underneath. The legacy carrier keyword is still
                // suppressed, which is what this row exists to prove: the expectation below holds
                // no trace of the word, from either the keyword slot or the action.
                (Keyword.REINFORCE,
                    new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION")),
                    "Give a friendly minion +1/+1."),
                (Keyword.SILENCE,
                    new SilenceAction(new TargetSpec("ANY_MINION")),
                    "[Silence] a minion."),
            };

            foreach (var row in rows)
            {
                var rule = new RuleDefinition(new RuleId("marker_action"),
                    new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                    OneShotLifetime.Instance, row.action);
                var card = new CardDefinition("legacy_marker", "Legacy Marker", CardType.SPELL, 1,
                    null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                    new[] { row.marker }, new[] { rule }, "", "art/legacy_marker", SetId.CORE,
                    true, null, "");

                Assert.That(CardTextGenerator.Generate(card), Is.EqualTo(row.expected), row.marker.ToString());
            }
        }

        [Test]
        public void All_261_Cards_Use_Actions_Not_Keywords_For_Action_Markers()
        {
            var cards = LoadAllCards();
            var markers = new HashSet<Keyword>
            {
                Keyword.BACKDRAFT,
                Keyword.REINFORCE,
                Keyword.SILENCE,
            };

            var authored = cards
                .Where(card => card.Keywords.Any(markers.Contains))
                .Select(card => card.Id)
                .ToList();

            Assert.That(cards, Has.Count.EqualTo(261));
            Assert.That(authored, Is.Empty,
                "action-derived presentation markers must not also be authored as carrier keywords");

            foreach (CardDefinition card in cards)
            {
                string text = CardTextGenerator.Generate(card);
                AssertMarkerCount(card, text, "Backdraft", rule => rule.Action is BackdraftAction);
                AssertMarkerCount(card, text, "Silence", rule => rule.Action is SilenceAction);

                // REINFORCE is the one legacy slot with NO marker: it prints "+1/+1" instead, so
                // the word must not reach a card face from either direction — not from the keyword
                // slot (asserted above) and not from the action. Asserted as an absence rather than
                // via AssertMarkerCount, whose contract is "one marker per rule that produces one".
                Assert.That(text, Does.Not.Contain("Reinforce"), card.Id + " must not print Reinforce");
                if (card.Rules.Any(rule => rule.Action is ReinforceAction))
                    Assert.That(text, Does.Contain("+1/+1"),
                        card.Id + " has a REINFORCE rule and must print its stats");
            }
        }

        [Test]
        public void Strict_Json_Rejects_The_Removed_CardText_Property()
        {
            var ex = Assert.Throws<FormatException>(() => CardJson.Deserialize(
                "{\"id\":\"removed_text\",\"type\":\"SPELL\",\"cost\":0,\"cardText\":\"legacy\"}"));

            Assert.That(ex!.Message, Does.Contain("cardText"));
        }

        [TestCase(Trigger.ON_PLAY, "[Herald]: Draw a card.")]
        [TestCase(Trigger.ON_DEATH, "[Toll]: Draw a card.")]
        [TestCase(Trigger.ON_ATTACK, "[Attack]: Draw a card.")]
        [TestCase(Trigger.ON_DAMAGED, "[Damaged]: Draw a card.")]
        [TestCase(Trigger.ON_HEAL, "[Healed]: Draw a card.")]
        [TestCase(Trigger.ON_TURN_START, "[Turn Start]: Draw a card.")]
        [TestCase(Trigger.ON_TURN_END, "[Turn End]: Draw a card.")]
        [TestCase(Trigger.ON_FRIENDLY_SUMMON, "[Friendly Summon]: Draw a card.")]
        [TestCase(Trigger.ON_SPELL_CAST, "[Spell Cast]: Draw a card.")]
        [TestCase(Trigger.ON_CAST, "Draw a card.")]
        public void Renders_Every_Supported_Triggered_Activation(Trigger trigger, string expected)
        {
            Assert.That(CardTextGenerator.Generate(new[] { DrawRule(new TriggeredActivation(trigger)) }),
                Is.EqualTo(expected));
        }

        [Test]
        public void Renders_Aura_Activation()
        {
            Assert.That(CardTextGenerator.Generate(new[] { DrawRule(AuraActivation.Instance) }),
                Is.EqualTo("[Aura]: Draw a card."));
        }

        [TestCase(TurnOwner.CONTROLLER, "While it is your turn, Draw a card.")]
        [TestCase(TurnOwner.OPPONENT, "While it is your opponent's turn, Draw a card.")]
        [TestCase(TurnOwner.ANY, "On either player's turn, Draw a card.")]
        public void Renders_Every_Turn_Owner_Condition(TurnOwner owner, string expected)
        {
            var rule = new RuleDefinition(new RuleId("turn_owner"),
                new TriggeredActivation(Trigger.ON_TURN_START), new TurnOwnerCondition(owner),
                SourceBoundLifetime.Instance, new DrawAction(1));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo("[Turn Start]: " + expected));
        }

        [Test]
        public void Renders_Target_Policy_And_Filter_As_Readable_Text()
        {
            var rule = new RuleDefinition(new RuleId("random_damaged"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(2, new TargetSpec("ANY_MINION", TargetChosenBy.RANDOM,
                    count: 1, filter: new TargetFilterDef(damaged: true))));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("Deal 2 damage to a random damaged minion."));
        }

        [Test]
        public void Renders_Other_Before_A_Tribe_Filter()
        {
            var rule = new RuleDefinition(new RuleId("construct_buff"),
                new TriggeredActivation(Trigger.ON_PLAY), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new ModifyStatsAction(1, 1, new TargetSpec("OTHER_FRIENDLY_MINIONS",
                    filter: new TargetFilterDef(tribe: Tribe.CONSTRUCT))));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("[Herald]: Give all other Construct friendly minions +1/+1."));
        }

        [TestCase("SELF", "Deal 1 damage to this.")]
        [TestCase("TRIGGER_SOURCE", "Deal 1 damage to the trigger source.")]
        [TestCase("ADJACENT", "Deal 1 damage to all adjacent minions.")]
        [TestCase("OTHER_FRIENDLY_MINIONS", "Deal 1 damage to all other friendly minions.")]
        [TestCase("FRIENDLY_WEAPON", "Deal 1 damage to your weapon.")]
        [TestCase("ENEMY_WEAPON", "Deal 1 damage to the enemy weapon.")]
        [TestCase("FRIENDLY_HAND", "Deal 1 damage to all cards in your hand.")]
        [TestCase("ENEMY_HAND", "Deal 1 damage to all cards in the enemy hand.")]
        [TestCase("ANY_HAND_CARD", "Deal 1 damage to a hand card.")]
        [TestCase("FRIENDLY_DECK", "Deal 1 damage to all cards in your deck.")]
        [TestCase("ENEMY_DECK", "Deal 1 damage to all cards in the enemy deck.")]
        [TestCase("ANY_DECK_CARD", "Deal 1 damage to a deck card.")]
        [TestCase("FRIENDLY_GRAVEYARD", "Deal 1 damage to all cards in your graveyard.")]
        [TestCase("ENEMY_GRAVEYARD", "Deal 1 damage to all cards in the enemy graveyard.")]
        [TestCase("ANY_GRAVEYARD_CARD", "Deal 1 damage to a graveyard card.")]
        public void Renders_Canonical_Selector_Families(string selector, string expected)
        {
            var rule = new RuleDefinition(new RuleId("selector_" + selector),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(1, new TargetSpec(selector)));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // Targeting.RequiredAutomaticCount gives an unspecified count on a RANDOM target exactly
        // ONE candidate, even on a selector that otherwise means every candidate. The generator has
        // to say the same thing: Death Mark and Stomp are this shape and printed "all friendly
        // minions" over a runtime that only ever buffed one of them.
        [TestCase("FRIENDLY_MINIONS", "Give a random friendly minion +1/+1.")]
        [TestCase("ENEMY_MINIONS", "Give a random enemy minion +1/+1.")]
        [TestCase("ANY_FRIENDLY_MINION", "Give a random friendly minion +1/+1.")]
        public void A_Random_Target_With_No_Count_Reads_As_One_Like_The_Resolver_Resolves_It(
            string selector, string expected)
        {
            var rule = new RuleDefinition(new RuleId("random_no_count"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new ModifyStatsAction(1, 1, new TargetSpec(selector, TargetChosenBy.RANDOM)));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // The one case that still reads as "all": an explicit -1 takes every candidate, in random
        // order. Saying "random" there would claim a narrowing that never happened.
        [Test]
        public void An_Explicit_Every_Candidate_Count_Stays_All_Even_When_Randomly_Ordered()
        {
            var rule = new RuleDefinition(new RuleId("random_all"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new ModifyStatsAction(1, 1,
                    new TargetSpec("FRIENDLY_MINIONS", TargetChosenBy.RANDOM, count: -1)));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("Give all friendly minions +1/+1."));
        }

        // The article agrees with the SOUND the phrase opens on, not its first letter — and a
        // summoned token opens on its stat prefix, so the number has to be read aloud too. No
        // shipping card trips any of these: every live summon is a consonant-initial name after
        // a 1/1 or 5/5. They are the cases the next token name walks into.
        [TestCase("token_wisp_1_1", "Summon a 1/1 Wisp.")]
        [TestCase("token_golem_8_8", "Summon an 8/8 Golem.")]
        [TestCase("token_ancient_11_11", "Summon an 11/11 Ancient.")]
        [TestCase("token_ember_wisp", "Summon an Ember Wisp.")]
        [TestCase("token_unicorn", "Summon a Unicorn.")]
        [TestCase("token_hourglass", "Summon an Hourglass.")]
        public void An_Indefinite_Article_Agrees_With_The_Sound_The_Name_Opens_On(
            string cardId, string expected)
        {
            var rule = new RuleDefinition(new RuleId("summon_one"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new SummonAction(cardId, 1));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // Pluralisation used to be a trailing "s", which is right for every name the card data
        // ships and wrong for the first one that ends in a sibilant, a consonant + y, or an f.
        [TestCase("token_wisp_1_1", "Summon 2 1/1 Wisps.")]
        [TestCase("token_gear_wisp", "Summon 2 Gear Wisps.")]
        [TestCase("token_ash", "Summon 2 Ashes.")]
        [TestCase("token_torch", "Summon 2 Torches.")]
        [TestCase("token_fox", "Summon 2 Foxes.")]
        [TestCase("token_harpy", "Summon 2 Harpies.")]
        [TestCase("token_ley", "Summon 2 Leys.")]
        [TestCase("token_wolf", "Summon 2 Wolves.")]
        [TestCase("token_wraithknife", "Summon 2 Wraithknives.")]
        public void A_Plural_Card_Name_Follows_English_Spelling_Not_A_Trailing_S(
            string cardId, string expected)
        {
            var rule = new RuleDefinition(new RuleId("summon_two"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new SummonAction(cardId, 2));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // A filter puts its own word first, so the article is re-picked against the modifier —
        // "an enemy minion" that is undamaged stays "an", one that is damaged becomes "a".
        [TestCase("ANY_MINION", true, null, "Deal 1 damage to a damaged minion.")]
        [TestCase("ANY_MINION", false, null, "Deal 1 damage to an undamaged minion.")]
        [TestCase("ANY_ENEMY", false, null, "Deal 1 damage to an undamaged enemy character.")]
        [TestCase("ANY_ENEMY", true, null, "Deal 1 damage to a damaged enemy character.")]
        [TestCase("ANY_MINION", null, Tribe.ELEMENTAL, "Deal 1 damage to an Elemental minion.")]
        [TestCase("ANY_MINION", null, Tribe.BEAST, "Deal 1 damage to a Beast minion.")]
        public void A_Filter_Re_Articles_The_Phrase_It_Modifies(
            string selector, bool? damaged, Tribe? tribe, string expected)
        {
            var rule = new RuleDefinition(new RuleId("filtered"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(1, new TargetSpec(selector,
                    filter: new TargetFilterDef(damaged: damaged, tribe: tribe))));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // distinctFrom on a possessive kept the determiner AND added one: "another your weapon".
        // "your other weapon" is the parallel of the "all " -> "all other " case beside it. That
        // it then claims two weapons is a fact about the authored data, not the phrasing.
        [TestCase("FRIENDLY_WEAPON", "Deal 1 damage to your other weapon.")]
        [TestCase("FRIENDLY_HERO", "Deal 1 damage to your other hero.")]
        [TestCase("ANY_MINION", "Deal 1 damage to another minion.")]
        [TestCase("ENEMY_HERO", "Deal 1 damage to another enemy hero.")]
        public void Another_Keeps_A_Possessive_Instead_Of_Doubling_The_Determiner(
            string selector, string expected)
        {
            var rule = new RuleDefinition(new RuleId("distinct"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(1, new TargetSpec(selector, distinctFrom: "first")));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        // The determiner table is shared by the filter and the random splice for exactly this
        // reason: "all other" and "your other" are two words, and an adjective goes after both of
        // them, not inside. When each splice site listed determiners for itself, one of them knew
        // about "all other " and the other did not.
        [TestCase("FRIENDLY_WEAPON", null, "Deal 1 damage to your other random damaged weapon.")]
        [TestCase("FRIENDLY_MINIONS", -1, "Deal 1 damage to all other damaged friendly minions.")]
        public void An_Adjective_Splices_After_A_Two_Word_Determiner_Not_Into_It(
            string selector, int? count, string expected)
        {
            var rule = new RuleDefinition(new RuleId("distinct_filtered_random"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(1, new TargetSpec(selector, TargetChosenBy.RANDOM, count: count,
                    filter: new TargetFilterDef(damaged: true), distinctFrom: "first")));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        [Test]
        public void Renders_Target_Count_And_Distinctness()
        {
            var rule = new RuleDefinition(new RuleId("multiple_other_minions"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(1, new TargetSpec("ANY_MINION", count: 2, distinctFrom: "first")));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("Deal 1 damage to 2 other minions."));
        }

        [Test]
        public void Fails_Explicitly_For_An_Unsupported_Target_Node()
        {
            var rule = new RuleDefinition(new RuleId("unknown_target"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(1, new TargetSpec("NOT_A_SELECTOR")));

            var ex = Assert.Throws<NotSupportedException>(() => CardTextGenerator.Generate(new[] { rule }));

            Assert.That(ex!.Message, Does.Contain("NOT_A_SELECTOR"));
        }

        [Test]
        public void Fails_Explicitly_For_An_Unsupported_Trigger_Node()
        {
            var rule = new RuleDefinition(new RuleId("unknown_trigger"),
                new TriggeredActivation((Trigger)999), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DrawAction(1));

            var ex = Assert.Throws<NotSupportedException>(() => CardTextGenerator.Generate(new[] { rule }));

            Assert.That(ex!.Message, Does.Contain("999"));
        }

        [Test]
        public void Every_Live_Card_Renders_Readably_And_Deterministically()
        {
            List<CardDefinition> cards = LoadAllCards();

            int rendered = 0;
            foreach (CardDefinition card in cards)
            {
                string first = CardTextGenerator.Generate(card);
                string second = CardTextGenerator.Generate(card);

                Assert.That(first, Is.EqualTo(second), $"{card.Id} did not render deterministically.");
                if (card.Rules.Count == 0 && card.Keywords.Count == 0)
                {
                    Assert.That(first, Is.Empty, $"{card.Id} should have no generated text.");
                    rendered++;
                    continue;
                }

                Assert.That(first, Is.Not.Empty, $"{card.Id} rendered no card text.");
                Assert.That(first, Does.Match("[A-Za-z]"), $"{card.Id} did not render readable words.");
                Assert.That(first, Does.Not.Contain("_"), $"{card.Id} leaked an identifier spelling.");
                if (card.Rules.Count > 0)
                    Assert.That(first, Does.EndWith("."), $"{card.Id} did not render a complete sentence.");
                rendered++;
            }

            Assert.That(cards.Count, Is.EqualTo(261));
            Assert.That(rendered, Is.EqualTo(261));
        }

        private static List<CardDefinition> LoadAllCards()
        {
            var cards = new List<CardDefinition>();
            foreach (string file in new[] { "Emberfall.cards.json", "Rekindling.cards.json" })
            {
                string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", file);
                cards.AddRange(CardJson.DeserializeArray(File.ReadAllText(path)));
            }
            return cards;
        }

        private static void AssertMarkerCount(CardDefinition card, string text, string marker,
            Func<RuleDefinition, bool> isMarkerAction)
        {
            int expected = card.Rules.Count(isMarkerAction);
            int actual = 0;
            string token = "[" + marker + "]";
            for (int offset = 0; (offset = text.IndexOf(token, offset, StringComparison.Ordinal)) >= 0;
                offset += token.Length)
                actual++;
            Assert.That(actual, Is.EqualTo(expected), card.Id + " marker count for " + marker);
        }

        private static RuleDefinition DrawRule(RuleActivation activation)
            => new RuleDefinition(new RuleId("draw"), activation, AlwaysCondition.Instance,
                SourceBoundLifetime.Instance, new DrawAction(1));
    }
}
