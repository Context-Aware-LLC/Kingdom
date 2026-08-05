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
    /// One-shot generator for the NOR-31 ongoing-effect replay. Two resolved spells become
    /// carrierless turn-start observers: the permanent source survives in final canonical state,
    /// while the one-shot source emits its single trigger and expiration in the golden event stream.
    /// </summary>
    [TestFixture]
    public sealed class OngoingEffectFixtureGenerator
    {
        private const ulong Seed = 2_718_281_828_459_045UL;
        private const string Permanent = "test_ongoing_permanent";
        private const string OneShot = "test_ongoing_one_shot";

        private static readonly string[] Deck = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0 ? Permanent : OneShot).ToArray();

        [Test, Category("Generator"), Explicit("Writes ongoing_effect_lifecycle.replay.json")]
        public void Write_OngoingEffectLifecycle_Fixture()
        {
            string snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            var setup = new MatchSetup(CardClass.EMBERWARD, Deck, CardClass.EMBERWARD, Deck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            GameState state = host.BeginMatch(new MatchId("GOLD-0010"), p0, p1, Seed);
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

            int activeSeat = state.SeatOf(state.ActivePlayer);
            PlayerState active = state.Players[activeSeat];
            CardInstance permanent = active.Hand.FirstOrDefault(c => c.DefinitionId == Permanent)!;
            CardInstance oneShot = active.Hand.FirstOrDefault(c => c.DefinitionId == OneShot)!;
            Assert.That(permanent, Is.Not.Null,
                "Seed must deal the permanent fixture spell; change Seed and re-bake if it does not.");
            Assert.That(oneShot, Is.Not.Null,
                "Seed must deal the one-shot fixture spell; change Seed and re-bake if it does not.");

            Submit(new PlayCardIntent(active.Id, permanent.Id, 0, Array.Empty<TargetRef>()));
            Submit(new PlayCardIntent(active.Id, oneShot.Id, 0, Array.Empty<TargetRef>()));
            Assert.That(state.OngoingEffects, Has.Count.EqualTo(2));
            InstanceId permanentId = state.OngoingEffects.Single(e =>
                e.SourceDefinitionId == Permanent).Id;
            InstanceId oneShotId = state.OngoingEffects.Single(e =>
                e.SourceDefinitionId == OneShot).Id;

            CommandResult turn = host.Submit(new EndTurnIntent(active.Id));
            Assert.That(turn.Accepted, Is.True);
            recorded.Add(ToJson(new EndTurnIntent(active.Id)));

            Assert.Multiple(() =>
            {
                Assert.That(state.OngoingEffects, Has.Count.EqualTo(1));
                Assert.That(state.OngoingEffects[0].Id, Is.EqualTo(permanentId));
                Assert.That(state.OngoingEffects[0].Lifetime,
                    Is.EqualTo(OngoingEffectLifetime.PERMANENT));
                Assert.That(turn.Events.Count(e => e.Kind == GameEventKind.OngoingEffectTriggered),
                    Is.EqualTo(2));
                Assert.That(turn.Events.Single(e => e.Kind == GameEventKind.OngoingEffectExpired).Source,
                    Is.EqualTo(oneShotId));
            });

            string fixturePath = Path.Combine(RepoPaths.FixturesDir,
                "ongoing_effect_lifecycle.replay.json");
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
                ["name"] = "ongoing_effect_lifecycle",
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
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(options) + "\n");
        }

        private static JsonObject ToJson(PlayerIntent intent)
        {
            switch (intent)
            {
                case MulliganIntent mulligan:
                    var replaced = new JsonArray();
                    foreach (InstanceId id in mulligan.ToReplace) replaced.Add((JsonNode)id.ToString());
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
                case EndTurnIntent endTurn:
                    return new JsonObject
                    {
                        ["$type"] = "end_turn",
                        ["player"] = endTurn.Player.ToString(),
                    };
                default:
                    throw new NotSupportedException(
                        $"Unhandled fixture intent {intent.GetType().Name}.");
            }
        }
    }
}
