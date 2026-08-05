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
    /// One-shot generator for the first GoldenReplay scenario that equips a weapon. The replay
    /// covers the populated weapon serializer branch, the same card's public-zone lifecycle, the
    /// fixed rider order, the Aegis no-rider precedent, and the weapon's fourth live carrier keyword.
    /// </summary>
    [TestFixture]
    public sealed class WeaponIdentityFixtureGenerator
    {
        private const ulong Seed = 1_618_033_988_749_894UL;
        private const string Blade = "test_rider_blade";
        private const string Brute = "test_rider_brute";
        private const string Aegis = "test_rider_aegis";
        private const string Probe = "test_spell_cast_probe";

        private static readonly string[] RiderDeck =
        {
            Blade, Probe, Brute, Aegis,
            Blade, Probe, Brute, Aegis,
            Blade, Probe, Brute, Aegis,
        };

        [Test, Category("Generator"), Explicit("Writes weapon_identity.replay.json — run locally to bake")]
        public void Write_WeaponIdentity_Fixture()
        {
            var db = JsonCardDatabase.FromSnapshotFile(
                Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json"));
            var setup = new MatchSetup(CardClass.EMBERWARD, RiderDeck,
                CardClass.EMBERWARD, RiderDeck);
            var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            host.BeginMatch(new MatchId("GOLD-0009"), p0, p1, Seed);
            var state = host.State;

            var recorded = new List<JsonObject>();
            void Submit(PlayerIntent intent)
            {
                var result = host.Submit(intent);
                Assert.That(result.Accepted, Is.True,
                    $"{intent.GetType().Name} rejected: {result.Reason}");
                recorded.Add(ToJson(intent));
            }

            int leadSeat = state.SeatOf(state.ActivePlayer);
            int followSeat = 1 - leadSeat;
            var leadId = state.Players[leadSeat].Id;
            var followId = state.Players[followSeat].Id;
            var leadHero = state.Players[leadSeat].Hero;
            var followHero = state.Players[followSeat].Hero;

            CardInstance Hand(int seat, string definitionId)
                => state.Players[seat].Hand.First(c => c.DefinitionId == definitionId);

            Submit(new MulliganIntent(p0, Array.Empty<InstanceId>()));
            Submit(new MulliganIntent(p1, Array.Empty<InstanceId>()));

            int fullHealth = leadHero.CurrentHealth;
            var bladeInHand = Hand(leadSeat, Blade);
            var bladeId = bladeInHand.Id;
            Submit(new PlayCardIntent(leadId, bladeId, 0, Array.Empty<TargetRef>()));
            Assert.That(state.Players[leadSeat].Weapon, Is.SameAs(bladeInHand));
            Assert.That(state.Players[leadSeat].Weapon!.Id, Is.EqualTo(bladeId));
            Assert.That(state.Players[leadSeat].Weapon!.Zone, Is.EqualTo(Zone.Weapon));
            Assert.That(state.Players[leadSeat].Weapon!.Keywords, Is.EquivalentTo(new[]
            {
                Keyword.LIFESTEAL, Keyword.WINDFURY, Keyword.POISONOUS, Keyword.FREEZE,
            }));
            Submit(new EndTurnIntent(leadId));

            Submit(new PlayCardIntent(followId, Hand(followSeat, Probe).Id, 0,
                Array.Empty<TargetRef>()));
            Assert.That(leadHero.CurrentHealth, Is.EqualTo(fullHealth - 2));
            Submit(new PlayCardIntent(followId, Hand(followSeat, Brute).Id, 0,
                Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(followId));

            var brute = state.Players[followSeat].Board.First(c => c.DefinitionId == Brute);
            Submit(new AttackIntent(leadId, leadHero.Id, new TargetRef(brute.Id)));
            Assert.That(state.Players[followSeat].Board.Contains(brute), Is.False,
                "Poisonous made the two-damage hit lethal");
            Assert.That(leadHero.CurrentHealth, Is.EqualTo(fullHealth),
                "Lifesteal restored the missing health");
            Submit(new EndTurnIntent(leadId));

            Submit(new PlayCardIntent(followId, Hand(followSeat, Aegis).Id, 0,
                Array.Empty<TargetRef>()));
            Submit(new PlayCardIntent(followId, Hand(followSeat, Probe).Id, 0,
                Array.Empty<TargetRef>()));
            Submit(new EndTurnIntent(followId));

            var aegis = state.Players[followSeat].Board.First(c => c.DefinitionId == Aegis);
            Submit(new AttackIntent(leadId, leadHero.Id, new TargetRef(aegis.Id)));
            Assert.That(aegis.HasAegisShield, Is.False);
            Assert.That(aegis.CurrentHealth, Is.EqualTo(6));
            Assert.That(aegis.IsFrozen, Is.False);
            Assert.That(leadHero.CurrentHealth, Is.EqualTo(fullHealth - 2));
            Submit(new EndTurnIntent(leadId));

            Submit(new EndTurnIntent(followId));

            Submit(new AttackIntent(leadId, leadHero.Id, new TargetRef(followHero.Id)));
            Assert.That(followHero.IsFrozen, Is.True);
            Assert.That(leadHero.CurrentHealth, Is.EqualTo(fullHealth));
            Assert.That(state.Players[leadSeat].Weapon, Is.Null);
            Assert.That(state.Players[leadSeat].Graveyard.Single(c => c.Id == bladeId),
                Is.SameAs(bladeInHand));
            Assert.That(bladeInHand.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress));

            string fixturePath = Path.Combine(RepoPaths.FixturesDir,
                "weapon_identity.replay.json");
            WriteFixtureJson(fixturePath, setup, recorded);
            ReplayHarness.Rebaseline(ReplayFixture.Load(fixturePath));
        }

        private static void WriteFixtureJson(string path, MatchSetup setup,
            List<JsonObject> intents)
        {
            var intentsArray = new JsonArray();
            foreach (var intent in intents) intentsArray.Add(intent.DeepClone());
            var p0Deck = new JsonArray();
            foreach (var id in setup.P0Deck) p0Deck.Add((JsonNode)id);
            var p1Deck = new JsonArray();
            foreach (var id in setup.P1Deck) p1Deck.Add((JsonNode)id);

            var root = new JsonObject
            {
                ["schemaVersion"] = ReplayFixture.CurrentSchemaVersion,
                ["name"] = "weapon_identity",
                ["engineContractVersion"] = "interfaces.v3",
                ["cardSetSnapshot"] = "M1.cards.snapshot.json",
                ["matchId"] = "GOLD-0009",
                ["p0"] = "P0",
                ["p1"] = "P1",
                ["p0Class"] = setup.P0Class.ToString(),
                ["p1Class"] = setup.P1Class.ToString(),
                ["heroMaxHealth"] = setup.HeroMaxHealth,
                ["sparkTokenId"] = setup.SparkTokenId,
                ["seed"] = Seed,
                ["p0Deck"] = p0Deck,
                ["p1Deck"] = p1Deck,
                ["intents"] = intentsArray,
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
                    foreach (var id in mulligan.ToReplace)
                        replaced.Add((JsonNode)id.ToString());
                    return new JsonObject
                    {
                        ["$type"] = "mulligan",
                        ["player"] = mulligan.Player.ToString(),
                        ["toReplace"] = replaced,
                    };
                case EndTurnIntent endTurn:
                    return new JsonObject
                    {
                        ["$type"] = "end_turn",
                        ["player"] = endTurn.Player.ToString(),
                    };
                case PlayCardIntent play:
                    var targets = new JsonArray();
                    foreach (var target in play.Targets)
                        targets.Add((JsonNode)target.Instance.ToString());
                    return new JsonObject
                    {
                        ["$type"] = "play_card",
                        ["player"] = play.Player.ToString(),
                        ["card"] = play.Card.ToString(),
                        ["slot"] = play.BoardSlot,
                        ["targets"] = targets,
                    };
                case AttackIntent attack:
                    return new JsonObject
                    {
                        ["$type"] = "attack",
                        ["player"] = attack.Player.ToString(),
                        ["attacker"] = attack.Attacker.ToString(),
                        ["defender"] = attack.Defender.Instance.ToString(),
                    };
                default:
                    throw new NotSupportedException(
                        $"Unsupported fixture intent: {intent.GetType().Name}");
            }
        }
    }
}
