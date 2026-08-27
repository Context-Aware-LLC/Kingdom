using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Editor;
using Emberfall.Presentation;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class CardSoundEffectTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Card_json_round_trips_sound_ids_and_triggers()
        {
            CardDefinition original = Minion(1001, new[]
            {
                new CardSoundEffect("big_lizard", Trigger.ON_PLAY),
                new CardSoundEffect("sword-unsheathe4", Trigger.ON_ATTACK),
            });

            string json = CardJsonRowIO.Serialize(original);
            CardDefinition parsed = CardJson.Deserialize(json);

            Assert.That(parsed.SoundEffects, Has.Count.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(parsed.SoundEffects[0].SoundId, Is.EqualTo("big_lizard"));
                Assert.That(parsed.SoundEffects[0].Trigger, Is.EqualTo(Trigger.ON_PLAY));
                Assert.That(parsed.SoundEffects[1].SoundId, Is.EqualTo("sword-unsheathe4"));
                Assert.That(parsed.SoundEffects[1].Trigger, Is.EqualTo(Trigger.ON_ATTACK));
            });
        }

        [Test]
        public void Sound_effect_rows_reject_unknown_fields()
        {
            const string json = """
                {"id":1001,"handle":"sound_card","name":"Sound Card","type":"MINION","cost":1,
                 "attack":1,"health":1,"class":"NEUTRAL","rarity":"COMMON","keywords":[],
                 "setId":"CORE","collectible":true,"artId":"sound_card","rules":[],
                 "soundEffects":[{"soundId":"big_lizard","trigger":"ON_PLAY","volume":0.5}]}
                """;

            FormatException error = Assert.Throws<FormatException>(() => CardJson.Deserialize(json));
            Assert.That(error.Message, Does.Contain("Unknown card JSON property 'volume'"));
        }

        [Test]
        public void Validator_rejects_aura_and_non_addressable_sound_ids()
        {
            CardDefinition card = Minion(1001, new[]
            {
                new CardSoundEffect("Not A Filename", Trigger.ON_PLAY),
                new CardSoundEffect("ambient_loop", Trigger.AURA),
            });
            var database = new TestDatabase(card);

            ValidationReport report = new CardValidator().Validate(card, database);

            Assert.That(report.Issues, Has.Some.Matches<ValidationIssue>(issue =>
                issue.Code == "C30-SOUNDID-FORMAT"));
            Assert.That(report.Issues, Has.Some.Matches<ValidationIssue>(issue =>
                issue.Code == "C30-SOUND-TRIGGER-AURA"));
        }

        [Test]
        public void Minion_play_routes_its_own_cue_then_friendly_summon_observers()
        {
            CardDefinition played = Minion(1001,
                new[] { new CardSoundEffect("play", Trigger.ON_PLAY) });
            CardDefinition observer = Minion(1002,
                new[] { new CardSoundEffect("friend_arrived", Trigger.ON_FRIENDLY_SUMMON) });
            var database = new TestDatabase(played, observer);
            GameState state = State();
            var observingCard = new CardInstance(new InstanceId(3), observer.Id, P0)
            {
                Zone = Zone.Board,
            };
            var playedCard = new CardInstance(new InstanceId(4), played.Id, P0)
            {
                Zone = Zone.Board,
            };
            state.Players[0].Board.Add(observingCard);
            state.Players[0].Board.Add(playedCard);

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.CardPlayed, 1, playedCard.Id, null, null, EventFlags.None));

            Assert.That(sounds, Is.EqualTo(new[] { "play", "friend_arrived" }));
        }

        private static GameState State()
        {
            var hero0 = new HeroInstance(new InstanceId(1), P0, CardClass.NEUTRAL, 30);
            var hero1 = new HeroInstance(new InstanceId(2), P1, CardClass.NEUTRAL, 30);
            var state = new GameState(new MatchId("SFX"), new DeterministicRandom(1),
                new PlayerState(P0, hero0), new PlayerState(P1, hero1));
            state.ActivePlayer = P0;
            return state;
        }

        private static CardDefinition Minion(int id, IReadOnlyList<CardSoundEffect> sounds) =>
            new CardDefinition(id, "sound_card_" + id, "Sound Card", CardType.MINION, 1,
                1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(), "", "sound_card",
                SetId.CORE, true, null, "", soundEffects: sounds);

        private sealed class TestDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _cards = new();

            public TestDatabase(params CardDefinition[] cards)
            {
                foreach (CardDefinition card in cards) _cards.Add(card.Id, card);
            }

            public bool TryGet(int cardId, out CardDefinition def) =>
                _cards.TryGetValue(cardId, out def!);

            public CardDefinition Get(int cardId) => _cards[cardId];

            public IReadOnlyCollection<CardDefinition> AllCollectible() => _cards.Values;
        }
    }
}
