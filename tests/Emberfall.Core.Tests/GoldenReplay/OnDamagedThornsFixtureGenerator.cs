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
    /// One-shot fixture generator for <c>on_damaged_thorns</c> (EF-014). Marked [Explicit] — never runs in
    /// CI. Run it once locally to produce and rebaseline the fixture, then commit the resulting
    /// <c>*.replay.json</c> and <c>*.rebaseline.md</c> pair:
    /// <code>
    /// dotnet test tests/Emberfall.Core.Tests/Emberfall.Core.Tests.csproj -c Release \
    ///   --filter "FullyQualifiedName~OnDamagedThornsFixtureGenerator"
    /// </code>
    ///
    /// <para>Scenario: the first player lands <c>test_thornback</c> (1/5, "deal 1 to the enemy hero when
    /// this is damaged") and then <c>test_aegis_thornback</c> — the same card plus AEGIS. The
    /// second player then casts <c>frost_ripple</c>, a 1-damage sweep over <c>ENEMY_MINIONS</c>, catching
    /// both in a single volley.</para>
    ///
    /// <para>That one volley pins three properties of EF-013/EF-014's raise site that are invisible in any
    /// per-hit unit test:
    /// <list type="number">
    /// <item><b>The raise exists.</b> A real hit on a minion fires its own ON_DAMAGED — one
    ///   <c>onDamage</c> back at the casting hero.</item>
    /// <item><b>An Aegis-absorbed hit is not "damaged".</b> The shielded twin is hit by the same volley
    ///   and does <i>not</i> retaliate, because <c>DamagePipeline.ApplyDamage</c> returns at the shield
    ///   before the raise. The volley lands two hits and exactly ONE retaliation comes back.</item>
    /// <item><b>Enqueue-only: no drain between hits.</b> Both volley hits appear in the event stream
    ///   <i>before</i> the retaliation. A drain-between-hits implementation would interleave the
    ///   retaliation between them — the ordering decision recorded in <c>DamagePipeline.cs</c>.</item>
    /// </list></para>
    ///
    /// <para>Both minions are 5-health against a 1-damage volley so nothing dies: this fixture pins the
    /// damage trigger, and keeping death out of the log keeps it from doubling as a toll test
    /// (<c>toll_cascade</c> already owns that).</para>
    /// </summary>
    [TestFixture]
    public sealed class OnDamagedThornsFixtureGenerator
    {
        private const ulong Seed = 1_618_033_988_749_894UL;
        private const string Thornback = "test_thornback";
        private const string AegisThornback = "test_aegis_thornback";
        private const string Volley = "frost_ripple";

        /// <summary>
        /// Both seats run the same deck, because the coin flip decides which of P0/P1 leads and the
        /// fixture needs the leader to hold two thornbacks and the follower to hold the volley.
        /// </summary>
        private static readonly string[] ThornDeck =
        {
            Thornback, AegisThornback, Volley,
            Thornback, AegisThornback, Volley,
            Thornback, AegisThornback, Volley,
            Thornback,
        };

        [Test, Category("Generator"), Explicit("Writes on_damaged_thorns.replay.json — run locally to bake")]
        public void Write_OnDamagedThorns_Fixture()
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            // Both TIDECALLER: frost_ripple is a TIDECALLER spell and either seat may end up casting it.
            var setup = new MatchSetup(CardClass.TIDECALLER, ThornDeck, CardClass.TIDECALLER, ThornDeck);
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

            int casterHeroStart = state.Players[secondSeat].Hero.CurrentHealth;

            // ── Turn 1: the leader lands the bare thornback (1 mana) ─────────────
            var bare = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Thornback);
            Submit(new PlayCardIntent(firstId, bare.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            // ── Turn 2: the follower cannot afford the 2-cost volley yet ─────────
            Submit(new EndTurnIntent(secondId));

            // ── Turn 3: the leader lands the shielded twin beside it (2 mana) ────
            var shielded = state.Players[firstSeat].Hand.First(c => c.DefinitionId == AegisThornback);
            Submit(new PlayCardIntent(firstId, shielded.Id, 1, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            Assert.That(state.Players[firstSeat].Board.Select(m => m.DefinitionId),
                Is.EqualTo(new[] { Thornback, AegisThornback }),
                "Board order is load-bearing: the volley sweeps left→right, so the bare thornback must be hit first.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(casterHeroStart),
                "Nothing has been damaged yet — no ON_DAMAGED may have fired before the volley.");

            // ── Turn 4: the follower casts the volley — ONE hit, TWO targets ─────
            var volley = state.Players[secondSeat].Hand.First(c => c.DefinitionId == Volley);
            Submit(new PlayCardIntent(secondId, volley.Id, 0, Array.Empty<TargetRef>()));

            var board = state.Players[firstSeat].Board;
            Assert.That(board[0].CurrentHealth, Is.EqualTo(4), "The bare thornback took the volley's 1 damage.");
            Assert.That(board[1].CurrentHealth, Is.EqualTo(5), "The shielded twin lost no health — Aegis ate the hit.");
            Assert.That(board[1].HasAegisShield, Is.False, "…and the shield is spent.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(casterHeroStart - 1),
                "EXACTLY ONE retaliation: the damaged thornback raised ON_DAMAGED, the Aegis-absorbed twin did not.");
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "The scenario must not end the match — a decided outcome would truncate the replay.");

            // ── Write fixture JSON and bake ──────────────────────────────────────
            var fixturePath = Path.Combine(RepoPaths.FixturesDir, "on_damaged_thorns.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);

            var fx = ReplayFixture.Load(fixturePath);
            ReplayHarness.Rebaseline(fx);

            TestContext.Out.WriteLine($"✓ Wrote and baked: {fixturePath}");
            TestContext.Out.WriteLine("Fill in on_damaged_thorns.rebaseline.md, then commit both files.");
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
                ["name"] = "on_damaged_thorns",
                ["engineContractVersion"] = "interfaces.v3",   // Contracts schema counter (A2b §7 ruling item 6)
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
                        $"OnDamagedThornsFixtureGenerator.ToJson: unhandled intent type {intent.GetType().Name}.");
            }
        }
    }
}
