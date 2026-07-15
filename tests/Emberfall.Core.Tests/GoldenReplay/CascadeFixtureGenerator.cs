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
    /// One-shot fixture generator for <c>deathrattle_cascade</c> (golden-replay.md §5 / Step 5 of
    /// M1 Phase B). Marked [Explicit] — never runs in CI. Run it once locally to produce and rebaseline
    /// the fixture, then commit the resulting <c>*.replay.json</c> and <c>*.rebaseline.md</c> pair.
    ///
    /// Scenario: both players build identical Lantern-Bearer boards, then trade on turn 3.
    /// Both 2/2 DEATHRATTLE minions die simultaneously; the cascade loop fires both ON_DEATH
    /// effects in active-player-first board order, summoning a Wisp for each side.
    /// </summary>
    [TestFixture]
    public sealed class CascadeFixtureGenerator
    {
        private const ulong Seed = 3_141_592_653_589_793UL;

        private static readonly string[] LanternDeck =
            Enumerable.Repeat("core_lantern_bearer", 10).ToArray();

        [Test, Category("Generator"), Explicit("Writes deathrattle_cascade.replay.json — run locally to bake")]
        public void Write_DeathrattleCascade_Fixture()
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            var setup = new MatchSetup(CardClass.EMBERWARD, LanternDeck, CardClass.TIDECALLER, LanternDeck);
            var processor = new CommandProcessor(db);
            var host = new LocalMatchHost(processor, db, setup);

            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            host.BeginMatch(new MatchId("GOLD-0003"), p0, p1, Seed);
            var state = host.State;

            // Record intents as JSON objects so we can write them to the fixture verbatim.
            var recorded = new List<JsonObject>();
            void Submit(PlayerIntent intent)
            {
                var r = host.Submit(intent);
                Assert.That(r.Accepted, Is.True,
                    $"{intent.GetType().Name} rejected: {r.Reason}");
                recorded.Add(ToJson(intent));
            }

            int firstSeat = state.SeatOf(state.ActivePlayer);
            int secondSeat = 1 - firstSeat;
            var firstId = state.Players[firstSeat].Id;
            var secondId = state.Players[secondSeat].Id;

            // ── Mulligan: keep all ───────────────────────────────────────────────
            Submit(new MulliganIntent(p0, Array.Empty<InstanceId>()));
            Submit(new MulliganIntent(p1, Array.Empty<InstanceId>()));

            // ── Turn 1: pass (1 mana — can't afford Lantern-Bearer at cost 2) ──
            Submit(new EndTurnIntent(firstId));
            Submit(new EndTurnIntent(secondId));

            // ── Turn 2: each player plays a Lantern-Bearer ───────────────────────
            var fpCard = state.Players[firstSeat].Hand
                .First(c => c.DefinitionId == "core_lantern_bearer");
            Submit(new PlayCardIntent(firstId, fpCard.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            var spCard = state.Players[secondSeat].Hand
                .First(c => c.DefinitionId == "core_lantern_bearer");
            Submit(new PlayCardIntent(secondId, spCard.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(secondId));

            // ── Turn 3: ATTACK — 2/2 trades into 2/2, cascade fires ─────────────
            Assert.That(state.Players[firstSeat].Board, Has.Count.EqualTo(1));
            Assert.That(state.Players[secondSeat].Board, Has.Count.EqualTo(1));
            var attacker = state.Players[firstSeat].Board[0];
            var defender = state.Players[secondSeat].Board[0];
            Submit(new AttackIntent(firstId, attacker.Id, new TargetRef(defender.Id)));

            // Verify the cascade: both LBs dead, one Wisp per side.
            Assert.That(state.Players[firstSeat].Graveyard
                .Any(c => c.DefinitionId == "core_lantern_bearer"), Is.True,
                "First player's LB should be in graveyard.");
            Assert.That(state.Players[secondSeat].Graveyard
                .Any(c => c.DefinitionId == "core_lantern_bearer"), Is.True,
                "Second player's LB should be in graveyard.");
            Assert.That(state.Players[firstSeat].Board
                .Any(c => c.DefinitionId == "token_wisp_1_1"), Is.True,
                "First player should have a Wisp from deathrattle.");
            Assert.That(state.Players[secondSeat].Board
                .Any(c => c.DefinitionId == "token_wisp_1_1"), Is.True,
                "Second player should have a Wisp from deathrattle.");

            // ── Write fixture JSON and rebaseline ────────────────────────────────
            var fixturePath = Path.Combine(RepoPaths.FixturesDir, "deathrattle_cascade.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);

            var fx = ReplayFixture.Load(fixturePath);
            ReplayHarness.Rebaseline(fx);

            TestContext.Out.WriteLine($"✓ Wrote and rebaselined: {fixturePath}");
            TestContext.Out.WriteLine("Fill in deathrattle_cascade.rebaseline.md, then commit both files.");
        }

        // ── Serialization helpers ────────────────────────────────────────────────

        private static void WriteFixtureJson(string path, MatchSetup setup, List<JsonObject> intents)
        {
            var intentsArr = new JsonArray();
            foreach (var o in intents)
                intentsArr.Add(o.DeepClone());

            var p0Deck = new JsonArray();
            foreach (var id in setup.P0Deck) p0Deck.Add((JsonNode)id);
            var p1Deck = new JsonArray();
            foreach (var id in setup.P1Deck) p1Deck.Add((JsonNode)id);

            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["name"] = "deathrattle_cascade",
                ["engineContractVersion"] = "interfaces.v2",
                ["cardSetSnapshot"] = "M1.cards.snapshot.json",
                ["matchId"] = "GOLD-0003",
                ["p0"] = "P0",
                ["p1"] = "P1",
                ["p0Class"] = setup.P0Class.ToString(),
                ["p1Class"] = setup.P1Class.ToString(),
                ["seed"] = Seed,
                ["p0Deck"] = p0Deck,
                ["p1Deck"] = p1Deck,
                ["intents"] = intentsArr,
                ["expected"] = new JsonObject
                {
                    ["stateHashAlgo"] = "sha256",
                    ["stateHash"] = "",
                    ["eventStream"] = new JsonArray(),
                },
            };

            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(opts) + "\n");
        }

        private static JsonObject ToJson(PlayerIntent intent)
        {
            switch (intent)
            {
                case MulliganIntent m:
                    {
                        var arr = new JsonArray();
                        foreach (var id in m.ToReplace) arr.Add((JsonNode)id.ToString());
                        return new JsonObject
                        {
                            ["$type"] = "mulligan",
                            ["player"] = m.Player.ToString(),
                            ["toReplace"] = arr,
                        };
                    }
                case EndTurnIntent e:
                    return new JsonObject
                    {
                        ["$type"] = "end_turn",
                        ["player"] = e.Player.ToString(),
                    };
                case PlayCardIntent p:
                    {
                        var tgts = new JsonArray();
                        foreach (var t in p.Targets) tgts.Add((JsonNode)t.Instance.ToString());
                        return new JsonObject
                        {
                            ["$type"] = "play_card",
                            ["player"] = p.Player.ToString(),
                            ["card"] = p.Card.ToString(),
                            ["slot"] = p.BoardSlot,
                            ["targets"] = tgts,
                        };
                    }
                case AttackIntent a:
                    return new JsonObject
                    {
                        ["$type"] = "attack",
                        ["player"] = a.Player.ToString(),
                        ["attacker"] = a.Attacker.ToString(),
                        ["defender"] = a.Defender.Instance.ToString(),
                    };
                default:
                    throw new NotSupportedException(
                        $"CascadeFixtureGenerator.ToJson: unhandled intent type {intent.GetType().Name}.");
            }
        }
    }
}
