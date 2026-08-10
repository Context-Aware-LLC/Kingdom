using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Collection;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// NOR-126 — CLOCKWRIGHT's signature weapon, <c>clock_mainspring_halberd</c>. Deliberately driven off the
    /// <b>live</b> Emberfall rows rather than a hand-built fixture: the thing at risk here is not the engine (EF-020
    /// already proved <c>RulesEngine.BreakWeapon</c> raises <c>ON_DEATH</c>) but the shipped card row. A weapon
    /// authored onto a dead trigger parses, validates, loads and then does nothing forever, silently — so the
    /// only test worth writing is one that would go red if someone edited the row in the JSON.
    ///
    /// <para>Both doors a weapon leaves play by are covered, because a draw-rattle is worth exactly nothing if
    /// it only fires on one of them.</para>
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class MainspringHalberdTests
    {
        private const string Halberd = "clock_mainspring_halberd";
        private const string Replacement = "ember_cinder_lance"; // 1/2, no effects — cannot muddy the read

        private static IReadOnlyList<CardDefinition>? _rekindlingCards;
        private static ICardDatabase? _liveDb;

        private static IReadOnlyList<CardDefinition> RekindlingCards => _rekindlingCards ??= LoadRekindling();

        private static ICardDatabase LiveDb
            => _liveDb ??= new JsonCardDatabase(LiveCardSet.Cards.Concat(RekindlingCards));

        private static IReadOnlyList<CardDefinition> LoadRekindling()
        {
            string path = Path.Combine(RepoPaths.RepoRoot,
                "unity", "Emberfall", "Assets", "CardData", "Rekindling.cards.json");
            Assert.That(File.Exists(path), Is.True, $"Live Rekindling card set not found at {path}");
            return CardJson.DeserializeArray(File.ReadAllText(path));
        }

        private static CardDefinition Def(string id)
        {
            var card = LiveCardSet.Cards.Concat(RekindlingCards).FirstOrDefault(c => c.Id == id);
            Assert.That(card, Is.Not.Null, $"'{id}' is missing from the live Emberfall+Rekindling sets.");
            return card!;
        }

        // ── The row itself ───────────────────────────────────────────────────────

        [Test]
        public void The_Shipped_Row_Is_A_Clockwright_Weapon_On_A_Live_Trigger()
        {
            var def = Def(Halberd);

            Assert.That(def.Type, Is.EqualTo(CardType.WEAPON));
            Assert.That(def.Class, Is.EqualTo(CardClass.CLOCKWRIGHT));
            Assert.That(def.Collectible, Is.True, "a signature weapon has to be deck-buildable to be signature.");
            Assert.That(def.Name, Is.EqualTo("Mainspring Halberd"));
            Assert.That(def.Cost, Is.EqualTo(3));
            Assert.That(def.Attack, Is.EqualTo(2));
            Assert.That(def.Durability, Is.EqualTo(3));
            Assert.That(def.Rarity, Is.EqualTo(Rarity.RARE));
            Assert.That(def.SetId, Is.EqualTo(SetId.EMBERFALL));
            Assert.That(def.Keywords, Is.Empty, "Toll is derived presentation, not a carrier keyword.");
            Assert.That(CardTextGenerator.Generate(def), Is.EqualTo("[Toll]: Draw a card."));

            Assert.That(def.Rules.Count, Is.EqualTo(1));
            RuleDefinition rule = def.Rules[0];
            Assert.That(RuleSemantics.TriggerOf(rule), Is.EqualTo(Trigger.ON_DEATH),
                "ON_DEATH is the only trigger that reaches a weapon leaving play (RulesEngine.BreakWeapon, EF-020).");
            Assert.That(rule.Action, Is.TypeOf<DrawAction>());
            Assert.That(((DrawAction)rule.Action).Count, Is.EqualTo(1));
        }

        [Test]
        public void The_Shipped_Row_Validates_Clean()
        {
            var report = new CardValidator().Validate(Def(Halberd), LiveDb);
            Assert.That(report.Ok, Is.True,
                "validation errors: " + string.Join(", ", report.Issues
                    .Where(i => i.Severity == Severity.Error).Select(i => i.Code + " " + i.Message)));
        }

        // ── Door 1: durability exhaustion ────────────────────────────────────────

        [Test]
        public void Breaking_On_Durability_Exhaustion_Draws_Exactly_One_Card()
        {
            var s = MainPhase();
            Equip(s, 0, Def(Halberd), durabilityOverride: 1); // one swing from breaking
            StockDeck(s, 0, 5);

            int deckBefore = s.Players[0].Deck.Count;
            int handBefore = s.Players[0].Hand.Count;

            // The enemy hero carries no weapon, so it deals no retaliation damage back.
            var r = new CommandProcessor(LiveDb).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(s.Players[1].Hero.Id)));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Weapon, Is.Null, "durability hit zero — the halberd broke");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28), "the swing itself landed for 2");
            Assert.That(s.Players[0].Hand.Count, Is.EqualTo(handBefore + 1),
                "the Toll drew a card — before EF-020 gave BreakWeapon a raise site this stayed flat, silently.");
            Assert.That(s.Players[0].Deck.Count, Is.EqualTo(deckBefore - 1), "and it came off the deck");
        }

        // ── Door 2: equip-replacement ────────────────────────────────────────────

        [Test]
        public void Being_Replaced_Also_Draws_Exactly_One_Card()
        {
            var s = MainPhase();
            Equip(s, 0, Def(Halberd));                      // full durability — this door ignores it
            var lance = ToHand(s, 0, Def(Replacement));
            StockDeck(s, 0, 5);

            int deckBefore = s.Players[0].Deck.Count;
            int handBefore = s.Players[0].Hand.Count;       // includes the lance, which leaves hand as it is played

            var r = new CommandProcessor(LiveDb).Execute(s,
                new PlayCardIntent(s.ActivePlayer, lance.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Weapon, Is.Not.Null);
            Assert.That(s.Players[0].Weapon!.DefinitionId, Is.EqualTo(Replacement), "the new weapon is equipped");
            Assert.That(s.Players[0].Deck.Count, Is.EqualTo(deckBefore - 1),
                "arming over the halberd destroys it, so its Toll must pay out — the second of the two doors.");
            Assert.That(s.Players[0].Hand.Count, Is.EqualTo(handBefore), "one card left hand (the lance), one entered (the draw)");
        }

        [Test]
        public void The_Draw_Rattle_Does_Not_Chip_The_Replacement()
        {
            // The EF-020 ordering note: a weapon's rattle ENQUEUES, so the NEW weapon is already in the slot
            // when the OLD one resolves. That is a live hazard for a rattle that touches the weapon slot — this
            // design deliberately does not, and this test is what would catch it if the design ever changed.
            var s = MainPhase();
            Equip(s, 0, Def(Halberd));
            var lance = ToHand(s, 0, Def(Replacement));
            StockDeck(s, 0, 5);

            new CommandProcessor(LiveDb).Execute(s,
                new PlayCardIntent(s.ActivePlayer, lance.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(s.Players[0].Weapon!.CurrentDurability, Is.EqualTo(2),
                "the incoming lance keeps its full durability — the outgoing rattle must not touch it.");
        }

        // ── Harness (shape borrowed from Combat/WeaponTollTests) ──────────

        private static GameState MainPhase()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.CLOCKWRIGHT, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("NOR126"), new DeterministicRandom(126UL), ps0, ps1)
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            ps0.Mana.Max = ps0.Mana.Available = 10;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static void Equip(GameState s, int seat, CardDefinition def, int? durabilityOverride = null)
            => s.Players[seat].Weapon = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id)
            {
                Zone = Zone.Weapon,
                CurrentAttack = def.Attack ?? 0,
                CurrentDurability = durabilityOverride ?? def.Durability ?? 1,
            };

        private static CardInstance ToHand(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id) { Zone = Zone.Hand };
            ps.Hand.Add(ci);
            return ci;
        }

        /// <summary>Enough real cards in the deck that a draw is a draw and not fatigue.</summary>
        private static void StockDeck(GameState s, int seat, int count)
        {
            var ps = s.Players[seat];
            var filler = LiveCardSet.AnyCollectible(CardClass.CLOCKWRIGHT);
            for (int i = 0; i < count; i++)
                ps.Deck.Add(new CardInstance(s.NextInstanceId(), filler.Id, ps.Id) { Zone = Zone.Deck });
        }
    }
}
