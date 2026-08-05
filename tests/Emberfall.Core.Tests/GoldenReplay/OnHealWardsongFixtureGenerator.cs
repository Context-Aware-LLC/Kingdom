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
    /// One-shot fixture generator for <c>on_heal_wardsong</c> (EF-015). Marked [Explicit] — never runs in
    /// CI. Run it once locally to produce and rebaseline the fixture, then commit the resulting
    /// <c>*.replay.json</c> and <c>*.rebaseline.md</c> pair:
    /// <code>
    /// dotnet test tests/Emberfall.Core.Tests/Emberfall.Core.Tests.csproj -c Release \
    ///   --filter "FullyQualifiedName~OnHealWardsongFixtureGenerator"
    /// </code>
    ///
    /// <para>Scenario: the leader boards two <c>test_heal_wardsong</c> (1/3, "deal 1 to the enemy hero when
    /// this is healed"). The follower casts <c>frost_ripple</c>, a 1-damage sweep over <c>ENEMY_MINIONS</c>,
    /// wounding both to 2/3. The leader then boards a THIRD wardsong — untouched, at full health — and casts
    /// <c>test_heal_salve</c>, a 2-point heal over <c>FRIENDLY_MINIONS</c>, catching all three in one sweep.</para>
    ///
    /// <para>That one sweep pins three properties of EF-015's raise site that no per-heal unit test reaches:
    /// <list type="number">
    /// <item><b>The raise exists.</b> A minion whose health was really restored fires its own ON_HEAL — one
    ///   <c>onDamage</c> back at the healing hero.</item>
    /// <item><b>An ineffective heal is not a heal.</b> The fresh third wardsong is swept by the same salve
    ///   and does <i>not</i> ping, because <c>DamagePipeline.ApplyHeal</c> returns at its <c>healed &lt;= 0</c>
    ///   guard before the raise. Three targets healed, exactly TWO pings back. This is the heal-side twin of
    ///   <c>on_damaged_thorns</c>' Aegis control, and the only fixture that enforces it.</item>
    /// <item><b>Enqueue-only: no drain between targets.</b> Both <c>onHeal</c> events appear in the stream
    ///   <i>before</i> either ping. A drain-between-targets implementation would interleave them — the
    ///   ordering decision recorded in <c>DamagePipeline.cs</c> and inherited from EF-013.</item>
    /// </list></para>
    ///
    /// <para>The heal is 2 against 1 missing health, so it also pins the clamp: each <c>onHeal</c> carries the
    /// <i>effective</i> 1, not the requested 2. Nothing dies anywhere in the log — 3-health minions against a
    /// 1-damage volley — keeping this fixture about the heal trigger and not a second toll test.</para>
    /// </summary>
    [TestFixture]
    public sealed class OnHealWardsongFixtureGenerator
    {
        private const ulong Seed = 2_718_281_828_459_045UL;
        private const string Wardsong = "test_heal_wardsong";
        private const string Salve = "test_heal_salve";
        private const string Volley = "frost_ripple";

        /// <summary>
        /// Both seats run the same deck, because the coin flip decides which of P0/P1 leads and the fixture
        /// needs the leader holding three wardsongs plus a salve and the follower holding the volley.
        /// </summary>
        private static readonly string[] WardsongDeck =
        {
            Wardsong, Salve, Volley,
            Wardsong, Salve, Volley,
            Wardsong, Salve, Volley,
            Wardsong,
        };

        [Test, Category("Generator"), Explicit("Writes on_heal_wardsong.replay.json — run locally to bake")]
        public void Write_OnHealWardsong_Fixture()
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            // Both TIDECALLER: frost_ripple is a TIDECALLER spell and either seat may end up casting it.
            var setup = new MatchSetup(CardClass.TIDECALLER, WardsongDeck, CardClass.TIDECALLER, WardsongDeck);
            var processor = new CommandProcessor(db);
            var host = new LocalMatchHost(processor, db, setup);

            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            host.BeginMatch(new MatchId("GOLD-0007"), p0, p1, Seed);
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

            int healerFoeHeroStart = state.Players[secondSeat].Hero.CurrentHealth;

            // ── Turn 1: the leader lands the first wardsong (1 mana) ─────────────
            var first = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Wardsong);
            Submit(new PlayCardIntent(firstId, first.Id, 0, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            // ── Turn 2: the follower cannot afford the 2-cost volley yet ─────────
            Submit(new EndTurnIntent(secondId));

            // ── Turn 3: the leader lands the second wardsong beside it (2 mana) ──
            var second = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Wardsong);
            Submit(new PlayCardIntent(firstId, second.Id, 1, Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(firstId));

            // ── Turn 4: the follower's volley wounds both, 3 → 2 ─────────────────
            var volley = state.Players[secondSeat].Hand.First(c => c.DefinitionId == Volley);
            Submit(new PlayCardIntent(secondId, volley.Id, 0, Array.Empty<TargetRef>()));

            var board = state.Players[firstSeat].Board;
            Assert.That(board.Select(m => m.CurrentHealth), Is.EqualTo(new[] { 2, 2 }),
                "Both wardsongs took the volley's 1 damage and survived — the salve must have something to restore.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(healerFoeHeroStart),
                "Nothing has been healed yet — no ON_HEAL may have fired before the salve.");
            Submit(new EndTurnIntent(secondId));

            // ── Turn 5: a FRESH wardsong joins at full health, then the salve sweeps all three ──
            var third = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Wardsong);
            Submit(new PlayCardIntent(firstId, third.Id, 2, Array.Empty<TargetRef>()));
            Assert.That(board.Select(m => m.CurrentHealth), Is.EqualTo(new[] { 2, 2, 3 }),
                "Board order is load-bearing: the salve sweeps left→right, and the untouched control must be last.");

            var salve = state.Players[firstSeat].Hand.First(c => c.DefinitionId == Salve);
            Submit(new PlayCardIntent(firstId, salve.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(board.Select(m => m.CurrentHealth), Is.EqualTo(new[] { 3, 3, 3 }),
                "The salve restored 1 to each wounded wardsong — clamped from the requested 2 — and 0 to the fresh one.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(healerFoeHeroStart - 2),
                "EXACTLY TWO pings: the two wardsongs that actually gained health raised ON_HEAL, "
                + "the full-health third did not — a heal that restores nothing is not a heal.");
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "The scenario must not end the match — a decided outcome would truncate the replay.");

            // ── Write fixture JSON and bake ──────────────────────────────────────
            var fixturePath = Path.Combine(RepoPaths.FixturesDir, "on_heal_wardsong.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);

            var fx = ReplayFixture.Load(fixturePath);
            ReplayHarness.Rebaseline(fx);

            TestContext.Out.WriteLine($"✓ Wrote and baked: {fixturePath}");
            TestContext.Out.WriteLine("Fill in on_heal_wardsong.rebaseline.md, then commit both files.");
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
                ["name"] = "on_heal_wardsong",
                ["engineContractVersion"] = "interfaces.v3",   // Contracts schema counter (A2b §7 ruling item 6)
                ["cardSetSnapshot"] = "M1.cards.snapshot.json",
                ["matchId"] = "GOLD-0007",
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
                        $"OnHealWardsongFixtureGenerator.ToJson: unhandled intent type {intent.GetType().Name}.");
            }
        }
    }
}
