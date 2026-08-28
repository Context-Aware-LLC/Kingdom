using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Editor;
using Emberfall.Core.Tests.Support;
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

        [Test]
        public void Spell_with_no_authored_row_falls_back_to_its_class_cast()
        {
            CardDefinition spell = Spell(1001, CardClass.TEMPEST, Array.Empty<CardSoundEffect>());
            var database = new TestDatabase(spell);
            GameState state = State();
            var cast = new CardInstance(new InstanceId(4), spell.Id, P0) { Zone = Zone.Board };
            state.Players[0].Board.Add(cast);

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.CardPlayed, 1, cast.Id, null, null, EventFlags.None));

            // The stem is the enum member lowercased, with no name map anywhere -- so TEMPEST is
            // "tempest_spell_cast" and never "storm_". Same rule as vo_<class>_<moment>_<n>.
            Assert.That(sounds, Is.EqualTo(new[] { "tempest_spell_cast" }));
        }

        [Test]
        public void An_authored_row_wins_over_the_class_default()
        {
            CardDefinition spell = Spell(1001, CardClass.TEMPEST,
                new[] { new CardSoundEffect("bespoke_cast", Trigger.ON_CAST) });
            var database = new TestDatabase(spell);
            GameState state = State();
            var cast = new CardInstance(new InstanceId(4), spell.Id, P0) { Zone = Zone.Board };
            state.Players[0].Board.Add(cast);

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.CardPlayed, 1, cast.Id, null, null, EventFlags.None));

            // A row is how a card asks for something OTHER than its class sound -- never additive,
            // or every bespoke spell would play two clips at once.
            Assert.That(sounds, Is.EqualTo(new[] { "bespoke_cast" }));
        }

        [Test]
        public void Hero_power_falls_back_to_the_one_shared_activate_clip()
        {
            CardDefinition power = HeroPower(1001, CardClass.GRAVEKEEP);
            var database = new TestDatabase(power);
            GameState state = State();
            state.Players[0].Hero.HeroPowerId = power.Id;

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.HeroPowerUsed, 1, state.Players[0].Hero.Id, null, null,
                EventFlags.None));

            // Deliberately NOT class-keyed: the sheet authors one clip for all eight powers.
            Assert.That(sounds, Is.EqualTo(new[] { "hero_power_activate" }));
        }

        [Test]
        public void The_default_never_reaches_observers()
        {
            // The observer paths run once per carrier on the board. If a default leaked into them
            // a three-minion board would play three clips for one spell, which is the failure this
            // whole split exists to prevent -- so the count, not just the content, is the assertion.
            CardDefinition spell = Spell(1001, CardClass.TEMPEST, Array.Empty<CardSoundEffect>());
            CardDefinition silent = Minion(1002, Array.Empty<CardSoundEffect>());
            var database = new TestDatabase(spell, silent);
            GameState state = State();
            state.Players[0].Board.Add(
                new CardInstance(new InstanceId(3), silent.Id, P0) { Zone = Zone.Board });
            state.Players[1].Board.Add(
                new CardInstance(new InstanceId(5), silent.Id, P1) { Zone = Zone.Board });
            var cast = new CardInstance(new InstanceId(4), spell.Id, P0) { Zone = Zone.Board };
            state.Players[0].Board.Add(cast);

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.CardPlayed, 1, cast.Id, null, null, EventFlags.None));

            Assert.That(sounds, Is.EqualTo(new[] { "tempest_spell_cast" }));
        }

        [Test]
        public void Phase_two_triggers_stay_silent_until_their_clips_exist()
        {
            // A minion play has no default ON PURPOSE: the summon archetypes are phase 2 of
            // sound-library.md and none of those clips exist yet. A default written ahead of its
            // file is an Addressables warning on every summon, which reads as a broken router.
            CardDefinition minion = Minion(1001, Array.Empty<CardSoundEffect>());
            var database = new TestDatabase(minion);
            GameState state = State();
            var played = new CardInstance(new InstanceId(4), minion.Id, P0) { Zone = Zone.Board };
            state.Players[0].Board.Add(played);

            var router = new CardSoundCueRouter(database);
            router.Capture(state);
            IReadOnlyList<string> sounds = router.Route(new GameEvent(
                GameEventKind.CardPlayed, 1, played.Id, null, null, EventFlags.None));

            Assert.That(sounds, Is.Empty);
        }

        /// <summary>
        /// The check that catches the failure the map-free stem convention exists to prevent: a
        /// clip and the router agreeing by NAME and nothing else.
        /// </summary>
        /// <remarks>
        /// Two halves, deliberately of different severities, because they are different problems.
        /// <list type="bullet">
        /// <item><b>A misnamed clip FAILS.</b> A <c>*_spell_cast</c> file that matches no
        /// <see cref="CardClass"/> is a clip nothing will ever ask for — it takes up a bundle slot,
        /// looks present in every listing, and plays never. That is a defect the moment it lands,
        /// so it is an assertion.</item>
        /// <item><b>A class with no clip yet WARNS.</b> Missing audio is unfinished content, not a
        /// broken build: the phase-1 clips are auditioned four takes at a time and promoted by hand,
        /// so between generating and picking every class is legitimately absent. Failing here would
        /// paint the whole blocking suite red for a content step and train everyone to ignore it —
        /// <see cref="Assert.Warn"/> reports it on every run without doing that.</item>
        /// </list>
        /// </remarks>
        [Test]
        public void Class_cast_clips_on_disk_match_the_router_and_report_what_is_missing()
        {
            string sfx = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Sound", "SFX");
            // spell_cast_1 is a pre-convention placeholder that nothing routes to; it is listed
            // so the orphan check stays about NEW mistakes rather than re-reporting a known one.
            var wanted = new List<string> { "hero_power_activate", "spell_cast_1" };
            foreach (CardClass cardClass in Enum.GetValues(typeof(CardClass)))
                wanted.Add(cardClass.ToString().ToLowerInvariant() + "_spell_cast");

            var present = new HashSet<string>();
            if (Directory.Exists(sfx))
                foreach (string file in Directory.GetFiles(sfx))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (Path.GetExtension(file) is ".wav" or ".mp3") present.Add(stem);
                }

            // Only stems claiming to BE a shared-library clip are judged; a bespoke per-card clip
            // is free to be named anything the C30 filename rule allows.
            //
            // CONTAINS, not EndsWith. An EndsWith("_spell_cast") test passes every near-miss it
            // most needs to catch, because the ways a stem goes wrong are overwhelmingly SUFFIXES:
            // "tempest_spell_cast_" (a take-suffix stripper that left its separator behind, which
            // is exactly how the whole phase-1 set promoted unusable on 2026-08-28) or
            // "tempest_spell_cast__take2" (no stripping at all). Both END with something else and
            // so read as "not a class cast at all" rather than as the broken class cast they are.
            var orphans = new List<string>();
            foreach (string stem in present)
                if ((stem.Contains("spell_cast", StringComparison.Ordinal) ||
                     stem.Contains("hero_power", StringComparison.Ordinal)) &&
                    !wanted.Contains(stem))
                    orphans.Add(stem);

            var missing = new List<string>();
            foreach (string stem in wanted)
                if (stem != "spell_cast_1" && !present.Contains(stem)) missing.Add(stem);

            Assert.That(orphans, Is.Empty,
                "Assets/Sound/SFX holds these class-cast clips that CardSoundCueRouter.DefaultSoundId "
                + "will never ask for — the stem is the CardClass member lowercased, so 'storm_' and "
                + "'warden_' are wrong where 'tempest_' and 'stonewarden_' are right: "
                + string.Join(", ", orphans));

            if (missing.Count == 0) return;
            string report =
                "[card-sfx] " + missing.Count + " default clip(s) not yet promoted from "
                + "art-pipeline/sfx — run tooling/sfx/soundweb.py, pick a take, then "
                + "Emberfall/Addressables/Setup Groups: " + string.Join(", ", missing);
            // BOTH, on purpose. Assert.Warn alone lands as a bare "Skipped" line with the message
            // swallowed by the VSTest console reporter, which is a guard that reports nothing —
            // TestContext.Progress writes straight through to stdout so the list is actually read.
            TestContext.Progress.WriteLine(report);
            Assert.Warn(report);
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

        private static CardDefinition Spell(int id, CardClass cardClass,
            IReadOnlyList<CardSoundEffect> sounds) =>
            new CardDefinition(id, "sound_spell_" + id, "Sound Spell", CardType.SPELL, 1,
                null, null, null, cardClass, Rarity.COMMON,
                Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(), "", "sound_spell",
                SetId.CORE, true, null, "", soundEffects: sounds);

        private static CardDefinition HeroPower(int id, CardClass cardClass) =>
            new CardDefinition(id, "sound_power_" + id, "Sound Power", CardType.HERO_POWER, 2,
                null, null, null, cardClass, Rarity.COMMON,
                Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(), "", "sound_power",
                SetId.CORE, false, null, "", soundEffects: Array.Empty<CardSoundEffect>());

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
