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
    /// One-shot fixture generator for <c>turn_start_toll</c> (EF-011). Marked [Explicit] — never runs in
    /// CI. Run it once locally to produce and rebaseline the fixture, then commit the resulting
    /// <c>*.replay.json</c> and <c>*.rebaseline.md</c> pair:
    /// <code>
    /// dotnet test tests/Emberfall.Core.Tests/Emberfall.Core.Tests.csproj -c Release \
    ///   --filter "FullyQualifiedName~TurnStartTollFixtureGenerator"
    /// </code>
    ///
    /// <para>Scenario: both players run <c>test_turn_start_toller</c> (1/3, explicit
    /// <c>TURN_OWNER:ANY</c>, "deal 1 to the enemy hero at the start of a turn"). The first player lands one on turn 1 and ends; the second lands one on
    /// turn 2 and ends; the first ends again on turn 3. That pins three properties of the new raise site
    /// in one replay — that ON_TURN_START resolves at all, that the ANY condition qualifies the owner's
    /// turn start as well as the opponent's, and that when both boards are subscribed the
    /// sweep runs active-player-first.</para>
    ///
    /// <para>It also pins the BEFORE-THE-DRAW half of the ordering directly, which is the part worth
    /// looking at: every toll's <c>onDamage</c> sits between that turn's <c>onTurnStart</c> and its
    /// <c>onDraw</c> (seq 4-6, 9-12, 14-17). Hoisting the raise below <c>DrawOne</c> reorders those rows
    /// and fails the fixture. The AFTER-THE-MANA-RESET half is not expressible in a hero-damage stream
    /// — no mana event is emitted — so it is pinned in <c>TurnStartTriggerTests</c> instead
    /// (<c>Turn_Start_Trigger_Fires_After_The_Mana_Reset</c>), as is the lethal case
    /// (<c>Lethal_Turn_Start_Toll_Ends_The_Match_Before_The_Draw</c>).</para>
    /// </summary>
    [TestFixture]
    public sealed class TurnStartTollFixtureGenerator
    {
        private const ulong Seed = 3_141_592_653_589_793UL;
        private const string Toller = "test_turn_start_toller";

        private static readonly string[] TollerDeck = Enumerable.Repeat(Toller, 10).ToArray();

        [Test, Category("Generator"), Explicit("Writes turn_start_toll.replay.json — run locally to bake")]
        public void Write_TurnStartToll_Fixture()
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            var setup = new MatchSetup(CardClass.EMBERWARD, TollerDeck, CardClass.TIDECALLER, TollerDeck);
            var processor = new CommandProcessor(db);
            var host = new LocalMatchHost(processor, db, setup);

            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            host.BeginMatch(new MatchId("GOLD-0005"), p0, p1, Seed);
            var state = host.State;

            var recorded = new List<JsonObject>();
            void Submit(PlayerIntent intent)
            {
                var r = host.Submit(intent);
                Assert.That(r.Accepted, Is.True, $"{intent.GetType().Name} rejected: {r.Reason}");
                recorded.Add(ToJson(intent));
            }

            int firstSeat = state.SeatOf(state.ActivePlayer);
            int secondSeat = 1 - firstSeat;
            var firstId = state.Players[firstSeat].Id;
            var secondId = state.Players[secondSeat].Id;

            // ── Mulligan: keep all ───────────────────────────────────────────────
            Submit(new MulliganIntent(p0, Array.Empty<InstanceId>()));
            Submit(new MulliganIntent(p1, Array.Empty<InstanceId>()));

            int firstHeroStart = state.Players[firstSeat].Hero.CurrentHealth;
            int secondHeroStart = state.Players[secondSeat].Hero.CurrentHealth;

            // ── Turn 1: first player lands a toller and ends ─────────────────────
            // Ending turn 1 starts turn 2, and THAT is where the first toll lands. Note the contrast
            // with turn_end_toll: there the toll resolves inside ApplyEndTurn, here inside the
            // StartTurnSequence that follows it.
            var fpToller = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Toller);
            Submit(new PlayCardIntent(firstId, fpToller.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHeroStart - 1),
                "ON_TURN_START must have resolved on the second player's turn start.");
            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHeroStart),
                "Only one toller is on the board; the first player's hero is untouched so far.");

            // ── Turn 2: second player lands one and ends — BOTH now fire ─────────
            var spToller = state.Players[secondSeat].Hand.First(c => c.DefinitionId == Toller);
            Submit(new PlayCardIntent(secondId, spToller.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(secondId));

            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHeroStart - 1),
                "The second player's toller hits the first player's hero.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHeroStart - 2),
                "The first player's explicit ANY toller fires on its owner's turn start too.");

            // ── Turn 3: first player ends again — both fire once more ────────────
            Submit(new EndTurnIntent(firstId));

            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHeroStart - 2));
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHeroStart - 3));
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "The scenario must not end the match — a decided outcome would truncate the replay.");

            // ── Write fixture JSON and bake ──────────────────────────────────────
            var fixturePath = Path.Combine(RepoPaths.FixturesDir, "turn_start_toll.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);

            var fx = ReplayFixture.Load(fixturePath);
            ReplayHarness.Rebaseline(fx);

            TestContext.Out.WriteLine($"✓ Wrote and baked: {fixturePath}");
            TestContext.Out.WriteLine("Fill in turn_start_toll.rebaseline.md, then commit both files.");
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
                ["schemaVersion"] = ReplayFixture.CurrentSchemaVersion,
                ["name"] = "turn_start_toll",
                ["engineContractVersion"] = "interfaces.v4",   // Contracts schema counter (A2b §7 ruling item 6)
                ["cardSetSnapshot"] = "M1.cards.snapshot.json",
                ["matchId"] = "GOLD-0005",
                ["p0"] = "P0",
                ["p1"] = "P1",
                ["p0Class"] = setup.P0Class.ToString(),
                ["p1Class"] = setup.P1Class.ToString(),
                // Schema v2 (C-G1-7): record every hash-load-bearing setup input, never inherit a C# default.
                ["heroMaxHealth"] = setup.HeroMaxHealth,
                ["sparkTokenId"] = setup.SparkTokenId,
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
                default:
                    throw new NotSupportedException(
                        $"TurnStartTollFixtureGenerator.ToJson: unhandled intent type {intent.GetType().Name}.");
            }
        }
    }
}
