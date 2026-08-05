using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// One-shot generator for the Unearth choice replay. The first offer is resolved by the exact
    /// server-issued option plus its correlation sequence; the second exercises timer AutoConfirm.
    /// </summary>
    [TestFixture]
    public sealed class UnearthChoiceFixtureGenerator
    {
        private const ulong Seed = 2_718_281_828_459_045UL;
        private const string Source = "test_unearth_source";

        private static readonly string[] SourceDeck =
        {
            Source, Source, Source, Source, Source, Source,
            Source, Source, Source, Source, Source, Source,
        };

        [Test, Category("Generator"), Explicit("Writes unearth_choice.replay.json — run locally to bake")]
        public void Write_UnearthChoice_Fixture()
        {
            var db = JsonCardDatabase.FromSnapshotFile(
                Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json"));
            var setup = new MatchSetup(CardClass.GRAVEKEEP, SourceDeck,
                CardClass.EMBERWARD, SourceDeck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            host.BeginMatch(new MatchId("GOLD-0010"), p0, p1, Seed);
            GameState state = host.State;
            var recorded = new List<JsonObject>();

            void Submit(PlayerIntent intent)
            {
                CommandResult result = host.Submit(intent);
                Assert.That(result.Accepted, Is.True,
                    $"{intent.GetType().Name} rejected: {result.Reason}");
                recorded.Add(ToJson(intent));
            }

            Submit(new MulliganIntent(p0, Array.Empty<InstanceId>()));
            Submit(new MulliganIntent(p1, Array.Empty<InstanceId>()));

            PlayerId active = state.ActivePlayer;
            PlayerState player = state.PlayerOf(active);
            CardInstance FirstSource() => player.Hand.First(c => c.DefinitionId == Source);

            Submit(new PlayCardIntent(active, FirstSource().Id, 0, Array.Empty<TargetRef>()));
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice!.Options, Has.Count.EqualTo(3));
            string explicitSelection = state.PendingChoice.Options[1];
            ulong firstSequence = state.PendingChoice.ChoiceSequence;
            Submit(new UnearthChoiceIntent(active, explicitSelection, firstSequence));
            Assert.That(player.Hand.Any(c => c.DefinitionId == explicitSelection), Is.True);

            Submit(new PlayCardIntent(active, FirstSource().Id, 0, Array.Empty<TargetRef>()));
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice!.ChoiceSequence, Is.GreaterThan(firstSequence));
            string autoSelection = state.PendingChoice.Options[0];
            Submit(new AutoConfirmIntent(active));

            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(player.Hand.Any(c => c.DefinitionId == autoSelection), Is.True);

            string fixturePath = Path.Combine(RepoPaths.FixturesDir, "unearth_choice.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);
            ReplayHarness.Rebaseline(ReplayFixture.Load(fixturePath));
        }

        private static void WriteFixtureJson(string path, MatchSetup setup,
            List<JsonObject> intents)
        {
            var intentArray = new JsonArray();
            foreach (JsonObject intent in intents) intentArray.Add(intent.DeepClone());
            var p0Deck = new JsonArray();
            foreach (string id in setup.P0Deck) p0Deck.Add((JsonNode)id);
            var p1Deck = new JsonArray();
            foreach (string id in setup.P1Deck) p1Deck.Add((JsonNode)id);

            var root = new JsonObject
            {
                ["schemaVersion"] = ReplayFixture.CurrentSchemaVersion,
                ["name"] = "unearth_choice",
                ["engineContractVersion"] = "interfaces.v4",
                ["cardSetSnapshot"] = "M1.cards.snapshot.json",
                ["matchId"] = "GOLD-0010",
                ["p0"] = "P0",
                ["p1"] = "P1",
                ["p0Class"] = setup.P0Class.ToString(),
                ["p1Class"] = setup.P1Class.ToString(),
                ["heroMaxHealth"] = setup.HeroMaxHealth,
                ["sparkTokenId"] = setup.SparkTokenId,
                ["seed"] = Seed,
                ["p0Deck"] = p0Deck,
                ["p1Deck"] = p1Deck,
                ["intents"] = intentArray,
                ["expected"] = new JsonObject
                {
                    ["stateHashAlgo"] = "sha256",
                    ["stateHash"] = "",
                    ["eventStream"] = new JsonArray(),
                },
            };

            File.WriteAllText(path, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        private static JsonObject ToJson(PlayerIntent intent)
        {
            switch (intent)
            {
                case MulliganIntent mulligan:
                    var replaced = new JsonArray();
                    foreach (InstanceId id in mulligan.ToReplace)
                        replaced.Add((JsonNode)id.ToString());
                    return new JsonObject
                    {
                        ["$type"] = "mulligan",
                        ["player"] = mulligan.Player.ToString(),
                        ["toReplace"] = replaced,
                    };
                case PlayCardIntent play:
                    return new JsonObject
                    {
                        ["$type"] = "play_card",
                        ["player"] = play.Player.ToString(),
                        ["card"] = play.Card.ToString(),
                        ["slot"] = play.BoardSlot,
                        ["targets"] = new JsonArray(),
                    };
                case UnearthChoiceIntent choice:
                    return new JsonObject
                    {
                        ["$type"] = "unearth_choice",
                        ["player"] = choice.Player.ToString(),
                        ["chosenCardId"] = choice.ChosenCardId,
                        ["choiceSequence"] = choice.ChoiceSequence,
                    };
                case AutoConfirmIntent auto:
                    return new JsonObject
                    {
                        ["$type"] = "auto_confirm",
                        ["player"] = auto.Player.ToString(),
                    };
                default:
                    throw new NotSupportedException(
                        $"Unsupported fixture intent: {intent.GetType().Name}");
            }
        }
    }
}
