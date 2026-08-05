using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// C-G1-7 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-G1-REVIEW.md §3) — fixture schema v2. <c>heroMaxHealth</c> and <c>sparkTokenId</c> are
    /// hash-load-bearing <see cref="MatchSetup"/> inputs (they reach the digest via
    /// <c>GameStateSerializer.WalkHero</c> and the second player's hand), but schema v1 never recorded
    /// them and the harness never passed them — so the R1 gate's promise, <i>these recorded inputs
    /// reproduce this hash</i>, was false. v2 records both. Reads DEFAULT to the v1 implicit values, so
    /// no existing hash moves. Schema v3 records semantic event kinds independently of presentation.
    /// </summary>
    [TestFixture]
    [Category("GoldenReplay")]
    public sealed class ReplayFixtureSchemaTests
    {
        private readonly List<string> _temp = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var p in _temp) { try { File.Delete(p); } catch (IOException) { } }
            _temp.Clear();
        }

        /// <summary>Writes a scratch fixture derived from the committed opening fixture, with <paramref name="mutate"/> applied.</summary>
        private string WriteVariant(string name, Action<JsonObject> mutate)
        {
            var src = Path.Combine(RepoPaths.FixturesDir, "opening_pyra_maren.replay.json");
            var root = JsonNode.Parse(File.ReadAllText(src))!.AsObject();
            root["name"] = name;
            root.Remove("expected");   // scratch variants assert on live state, not a baked hash
            mutate(root);

            var path = Path.Combine(Path.GetTempPath(), $"{name}.replay.json");
            File.WriteAllText(path, root.ToJsonString());
            _temp.Add(path);
            return path;
        }

        // ── Load ─────────────────────────────────────────────────────────────────

        [Test]
        public void Load_Reads_HeroMaxHealth_And_SparkTokenId()
        {
            var path = WriteVariant("schema_v2_explicit", root =>
            {
                root["schemaVersion"] = 2;
                root["heroMaxHealth"] = 20;
                root["sparkTokenId"] = "wandering_scout";
            });

            var fx = ReplayFixture.Load(path);

            Assert.That(fx.SchemaVersion, Is.EqualTo(2));
            Assert.That(fx.HeroMaxHealth, Is.EqualTo(20));
            Assert.That(fx.SparkTokenId, Is.EqualTo("wandering_scout"));
        }

        [Test]
        public void Load_Defaults_To_The_V1_Implicit_Values_When_Absent()
        {
            // The whole point of "default on read": a v1 fixture keeps producing the v1 hash.
            var path = WriteVariant("schema_v1_silent", root =>
            {
                root["schemaVersion"] = 1;
                root.Remove("heroMaxHealth");
                root.Remove("sparkTokenId");
            });

            var fx = ReplayFixture.Load(path);

            Assert.That(fx.HeroMaxHealth, Is.EqualTo(30));
            Assert.That(fx.SparkTokenId, Is.EqualTo("token_spark_coin"));
        }

        [Test]
        public void Load_Rejects_An_Unknown_SchemaVersion()
        {
            var path = WriteVariant("schema_v99", root => root["schemaVersion"] = 99);

            var ex = Assert.Throws<NotSupportedException>(() => ReplayFixture.Load(path));
            Assert.That(ex!.Message, Does.Contain("99"));
        }

        [Test]
        public void Load_Reads_A_Correlated_Unearth_Choice_Intent()
        {
            var path = WriteVariant("unearth_choice_intent", root =>
            {
                root["intents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["$type"] = "unearth_choice",
                        ["player"] = "P0",
                        ["chosenCardId"] = "grave_spark",
                        ["choiceSequence"] = 37,
                    },
                };
            });

            var fx = ReplayFixture.Load(path);

            Assert.That(fx.Intents, Has.Count.EqualTo(1));
            Assert.That(fx.Intents[0], Is.EqualTo(
                new UnearthChoiceIntent(new PlayerId("P0"), "grave_spark", 37)));
        }

        [Test]
        public void Expected_Event_Rejects_A_Previous_Contract_Kind()
        {
            using var document = JsonDocument.Parse("{\"seq\":1,\"kind\":\"onDraw\"}");

            Assert.Throws<FormatException>(() => ExpectedEvent.Parse(document.RootElement));
        }

        [Test]
        public void Expected_Event_Parses_A_Semantic_Kind()
        {
            using var document = JsonDocument.Parse("{\"seq\":1,\"kind\":\"CardDrawn\"}");

            ExpectedEvent expected = ExpectedEvent.Parse(document.RootElement);

            Assert.That(expected.Kind, Is.EqualTo(GameEventKind.CardDrawn));
        }

        // ── Harness passthrough — the actual C-G1-7 defect ───────────────────────

        [Test]
        public void Harness_Applies_The_Fixtures_HeroMaxHealth()
        {
            var path = WriteVariant("hero_hp_20", root =>
            {
                root["schemaVersion"] = 2;
                root["heroMaxHealth"] = 20;
            });
            var fx = ReplayFixture.Load(path);

            GameState? opening = null;
            ReplayHarness.Run(fx, s => opening ??= s);

            Assert.That(opening, Is.Not.Null);
            Assert.That(opening!.Players.Select(p => p.Hero.MaxHealth), Is.All.EqualTo(20));
            Assert.That(opening.Players.Select(p => p.Hero.CurrentHealth), Is.All.EqualTo(20));
        }

        [Test]
        public void Harness_Applies_The_Fixtures_SparkTokenId()
        {
            var path = WriteVariant("spark_override", root =>
            {
                root["schemaVersion"] = 2;
                root["sparkTokenId"] = "wandering_scout";
            });
            var fx = ReplayFixture.Load(path);

            GameState? opening = null;
            ReplayHarness.Run(fx, s => opening ??= s);

            Assert.That(opening, Is.Not.Null);
            var allHandDefs = opening!.Players.SelectMany(p => p.Hand).Select(c => c.DefinitionId).ToList();
            Assert.That(allHandDefs, Does.Contain("wandering_scout"));
            Assert.That(allHandDefs, Does.Not.Contain("token_spark_coin"));
        }

        // ── The standing guard ───────────────────────────────────────────────────

        [TestCaseSource(typeof(GoldenReplayTests), nameof(GoldenReplayTests.FixtureFiles))]
        public void Committed_Fixture_Records_All_Of_Its_Own_Setup_Inputs(string fixturePath)
        {
            // Guards the R1 gate promise itself: a new fixture must not reintroduce an implicit,
            // hash-load-bearing input that lives only in a C# default.
            var root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();

            Assert.That((int?)root["schemaVersion"], Is.EqualTo(3),
                $"'{Path.GetFileName(fixturePath)}' must declare schemaVersion 3.");
            Assert.That((string?)root["engineContractVersion"], Is.EqualTo("interfaces.v4"),
                $"'{Path.GetFileName(fixturePath)}' must declare engine contract interfaces.v4.");
            Assert.That(root.ContainsKey("heroMaxHealth"), Is.True,
                $"'{Path.GetFileName(fixturePath)}' must record heroMaxHealth explicitly.");
            Assert.That(root.ContainsKey("sparkTokenId"), Is.True,
                $"'{Path.GetFileName(fixturePath)}' must record sparkTokenId explicitly.");
        }
    }
}
