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
    /// One-shot fixture generator for <c>spell_cast_echo</c> (EF-016). Marked [Explicit] — never runs in
    /// CI. Run it once locally to produce and rebaseline the fixture, then commit the resulting
    /// <c>*.replay.json</c> and <c>*.rebaseline.md</c> pair:
    /// <code>
    /// dotnet test tests/Emberfall.Core.Tests/Emberfall.Core.Tests.csproj -c Release \
    ///   --filter "FullyQualifiedName~SpellCastEchoFixtureGenerator"
    /// </code>
    ///
    /// <para>Scenario: both players run <c>test_spell_cast_echo</c> (1/3, "whenever a spell is cast, deal 1
    /// to the enemy hero") and <c>test_spell_cast_probe</c> (a 0-cost spell that deals 2 to the enemy hero).
    /// The first player lands an echo; the second casts a probe and lands their own; the first takes a HERO
    /// POWER; the second casts again. That pins four properties of the new raise site in one replay — that
    /// ON_SPELL_CAST resolves at all, that an echo fires on the OPPONENT's cast (the both-sides scope), that
    /// a hero power does NOT fire it (the trap: hero powers raise the same ON_CAST through an ephemeral
    /// HERO_POWER card), and that with both boards subscribed the sweep runs active-player-first.</para>
    ///
    /// <para>Both seats are EMBERWARD on purpose: the hero-power leg must be <c>searing_bolt</c> whichever
    /// seat wins the coin flip, or the trap proof would depend on the flip.</para>
    /// </summary>
    [TestFixture]
    public sealed class SpellCastEchoFixtureGenerator
    {
        private const ulong Seed = 1_618_033_988_749_894UL;
        private const string Echo = "test_spell_cast_echo";
        private const string Probe = "test_spell_cast_probe";

        /// <summary>Alternating so a 3-or-4 card opening hand cannot be all of one kind.</summary>
        private static readonly string[] EchoProbeDeck =
            Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? Echo : Probe).ToArray();

        [Test, Category("Generator"), Explicit("Writes spell_cast_echo.replay.json — run locally to bake")]
        public void Write_SpellCastEcho_Fixture()
        {
            var snapshotPath = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            var db = JsonCardDatabase.FromSnapshotFile(snapshotPath);
            var setup = new MatchSetup(CardClass.EMBERWARD, EchoProbeDeck, CardClass.EMBERWARD, EchoProbeDeck);
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

            // ── Turn 1: first player lands an echo (1 mana) and ends ─────────────
            Play(host, Submit, state, firstSeat, Echo, slot: 0);
            Submit(new EndTurnIntent(firstId));

            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHeroStart),
                "Summoning an echo casts nothing — no health may move yet.");

            // ── Turn 2: second player casts (0 mana), then lands their own echo ──
            // The cast happens while ONLY the first player's echo is on the board, so the reaction here
            // is the both-sides scope on its own: the opponent's spell fires the first player's echo.
            Play(host, Submit, state, secondSeat, Probe, slot: 0);

            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstHeroStart - 2),
                "The probe's own ON_CAST hits the first player's hero for 2.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondHeroStart - 1),
                "The echo belongs to the FIRST player and must fire on the SECOND player's cast.");

            Play(host, Submit, state, secondSeat, Echo, slot: 0);
            Submit(new EndTurnIntent(secondId));

            // ── Turn 3: first player takes a HERO POWER — the trap ───────────────
            // searing_bolt deals 1 to a chosen character and raises Trigger.ON_CAST through an ephemeral
            // HERO_POWER CardInstance. If ON_SPELL_CAST were hooked to generic ON_CAST, BOTH echoes would
            // fire here. The assertions below are what make that regression a hash divergence forever.
            int beforePower = state.Players[secondSeat].Hero.CurrentHealth;
            int firstBeforePower = state.Players[firstSeat].Hero.CurrentHealth;
            Submit(new HeroPowerIntent(firstId,
                new[] { new TargetRef(state.Players[secondSeat].Hero.Id) }));

            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(beforePower - 1),
                "The hero power's own 1 damage and nothing else — a hero power is NOT a spell cast.");
            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstBeforePower),
                "The second player's echo must not have fired on a hero power either.");

            Submit(new EndTurnIntent(firstId));

            // ── Turn 4: second player casts again — now BOTH echoes react ────────
            int firstBeforeCast = state.Players[firstSeat].Hero.CurrentHealth;
            int secondBeforeCast = state.Players[secondSeat].Hero.CurrentHealth;
            Play(host, Submit, state, secondSeat, Probe, slot: 0);

            Assert.That(state.Players[firstSeat].Hero.CurrentHealth, Is.EqualTo(firstBeforeCast - 3),
                "2 from the probe + 1 from the caster's own echo.");
            Assert.That(state.Players[secondSeat].Hero.CurrentHealth, Is.EqualTo(secondBeforeCast - 1),
                "The first player's echo fires on the same cast — both boards are swept.");

            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "The scenario must not end the match — a decided outcome would truncate the replay.");

            // ── Write fixture JSON and bake ──────────────────────────────────────
            var fixturePath = Path.Combine(RepoPaths.FixturesDir, "spell_cast_echo.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);

            var fx = ReplayFixture.Load(fixturePath);
            ReplayHarness.Rebaseline(fx);

            TestContext.Out.WriteLine($"✓ Wrote and baked: {fixturePath}");
            TestContext.Out.WriteLine("Fill in spell_cast_echo.rebaseline.md, then commit both files.");
        }

        /// <summary>
        /// Play <paramref name="seat"/>'s first copy of <paramref name="cardId"/> from hand. The deck
        /// alternates echo/probe, so a hand always holds both — but assert rather than assume, because a
        /// missing card here would silently reshape the fixture rather than fail it.
        /// </summary>
        private static void Play(LocalMatchHost host, Action<PlayerIntent> submit, GameState state,
            int seat, string cardId, int slot)
        {
            var ps = state.Players[seat];
            var card = ps.Hand.FirstOrDefault(c => c.DefinitionId == cardId);
            Assert.That(card, Is.Not.Null,
                $"Seat {seat} has no '{cardId}' in hand at this point in the scenario — the shuffle moved. "
                + "Change the Seed constant and re-bake rather than reshaping the scenario.");
            submit(new PlayCardIntent(ps.Id, card!.Id, slot, Array.Empty<TargetRef>()));
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
                ["name"] = "spell_cast_echo",
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
                case HeroPowerIntent hp:
                    {
                        var tgts = new JsonArray();
                        foreach (var t in hp.Targets) tgts.Add((JsonNode)t.Instance.ToString());
                        return new JsonObject
                        {
                            ["$type"] = "hero_power",
                            ["player"] = hp.Player.ToString(),
                            ["targets"] = tgts,
                        };
                    }
                default:
                    throw new NotSupportedException(
                        $"SpellCastEchoFixtureGenerator.ToJson: unhandled intent type {intent.GetType().Name}.");
            }
        }
    }
}
