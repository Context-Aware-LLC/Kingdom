using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Projection;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Projection
{
    /// <summary>
    /// WS A3 event-stream suite (dev-docs/architecture/determinism-and-projection.md:24, plus the
    /// §10-mandated corpus additions: hero-power-sourced batch and weapon-break batch).
    /// Drives the concrete <see cref="CommandProcessor"/> to read the emit-time
    /// <see cref="CommandProcessor.LastEventVisibility"/> sidecar.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class EventProjectionTests
    {
        // ── scaffolding (HeroPowerTests house style) ─────────────────────────────

        private static GameState MainPhase(int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30) { HeroPowerId = "hp_bolt" };
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30) { HeroPowerId = "hp_bolt" };
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL),
                new PlayerState(p0, hero0), new PlayerState(p1, hero1))
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = p0Mana;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardInstance PutInHand(GameState s, int seat, string defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance PutInDeck(GameState s, int seat, string defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Deck };
            s.Players[seat].Deck.Add(ci);
            return ci;
        }

        private static readonly CardDefinition HpBolt = new CardDefinition(
            "hp_bolt", "hp_bolt", CardType.HERO_POWER, 2, null, null, null,
            CardClass.EMBERWARD, Rarity.COMMON,
            Array.Empty<Keyword>(),
            new[] { TestRules.Rule("bolt", Trigger.ON_CAST,
                TestRules.Damage(Trigger.ON_CAST, 1, "ENEMY_HERO")) },
            "", "", "art/hp_bolt", SetId.CORE, false, null, "", false);

        private static readonly CardDefinition Hatchet = new CardDefinition(
            "hatchet", "hatchet", CardType.WEAPON, 1, 2, null, 2,
            CardClass.NEUTRAL, Rarity.COMMON,
            Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(),
            "", "", "art/hatchet", SetId.CORE, true, null, "", false);

        private static (IReadOnlyList<GameEvent> events, IReadOnlyList<EventVisibility> vis)
            Exec(CommandProcessor proc, GameState s, PlayerIntent intent)
        {
            var r = proc.Execute(s, intent);
            Assert.That(r.Accepted, Is.True, $"{intent.GetType().Name} rejected: {r.Reason}");
            Assert.That(proc.LastEventVisibility, Has.Count.EqualTo(r.Events.Count),
                "Visibility sidecar must be index-parallel to the batch.");
            return (r.Events, proc.LastEventVisibility);
        }

        private static List<GameEvent> ProjectAll(IReadOnlyList<GameEvent> events,
            IReadOnlyList<EventVisibility> vis, int viewerSeat)
            => new List<GameEvent>(GameStateProjector.ProjectEvents(events, vis, viewerSeat));

        private static GameEvent ProjectOne(GameEvent e, int viewerSeat, EventVisibility vis)
            => GameStateProjector.ProjectEvents(new[] { e }, new[] { vis }, viewerSeat)[0];

        // ── never dropped, sequences identical (§5.3) ────────────────────────────

        [Test]
        public void Events_Are_Never_Dropped_And_Sequences_Match_For_Both_Viewers()
        {
            var db = new FakeCardDatabase();
            var proc = new CommandProcessor(db);
            var s = MainPhase();
            PutInDeck(s, 1, "tide_secret");
            var (events, vis) = Exec(proc, s, new EndTurnIntent(s.Players[0].Id)); // P1 turn start: draw

            var v0 = ProjectAll(events, vis, 0);
            var v1 = ProjectAll(events, vis, 1);
            Assert.That(v0, Has.Count.EqualTo(events.Count));
            Assert.That(v1, Has.Count.EqualTo(events.Count));
            for (int i = 0; i < events.Count; i++)
            {
                Assert.That(v0[i].Sequence, Is.EqualTo(events[i].Sequence));
                Assert.That(v1[i].Sequence, Is.EqualTo(events[i].Sequence));
                Assert.That(v0[i].Kind, Is.EqualTo(events[i].Kind));
            }
        }

        // ── the load-bearing CardDrawn row ───────────────────────────────────────

        [Test]
        public void Draw_Event_Owner_Sees_Card_Opponent_Sees_Sentinel()
        {
            var db = new FakeCardDatabase();
            var proc = new CommandProcessor(db);
            var s = MainPhase();
            var planted = PutInDeck(s, 1, "tide_secret");
            var (events, vis) = Exec(proc, s, new EndTurnIntent(s.Players[0].Id));

            int drawIdx = -1;
            for (int i = 0; i < events.Count; i++)
                if (events[i].Kind == GameEventKind.CardDrawn) drawIdx = i;
            Assert.That(drawIdx, Is.GreaterThanOrEqualTo(0), "Turn start must have drawn.");

            var forOwner = ProjectOne(events[drawIdx], 1, vis[drawIdx]);
            var forOpponent = ProjectOne(events[drawIdx], 0, vis[drawIdx]);

            Assert.That(forOwner.Source!.Value.Value, Is.EqualTo(planted.Id.Value),
                "The drawing player sees their own card.");
            Assert.That(forOpponent.Source!.Value.Value, Is.Zero,
                "The opponent's copy carries the sentinel (§5.3 identity rule).");
        }

        // ── §7.1 test 11 — multi-draw batch, each value-redacted individually ────

        [Test]
        public void Mulligan_Multi_Draw_Batch_Redacts_Each_Draw_Individually()
        {
            var db = new FakeCardDatabase();
            var proc = new CommandProcessor(db);
            var s = MainPhase();
            s.Phase = TurnPhase.Mulligan;
            s.Mulligan0Done = false;
            s.Mulligan1Done = false;
            var keep = PutInHand(s, 0, "ember_keep");
            var toss1 = PutInHand(s, 0, "ember_toss1");
            var toss2 = PutInHand(s, 0, "ember_toss2");
            for (int i = 0; i < 4; i++) PutInDeck(s, 0, $"ember_deck{i}");

            var (events, vis) = Exec(proc, s,
                new MulliganIntent(s.Players[0].Id, new List<InstanceId> { toss1.Id, toss2.Id }));

            int draws = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind != GameEventKind.CardDrawn) continue;
                draws++;
                var own = ProjectOne(events[i], 0, vis[i]);
                var opp = ProjectOne(events[i], 1, vis[i]);
                Assert.That(own.Source!.Value.Value, Is.Not.Zero, "Owner sees each replacement draw.");
                Assert.That(opp.Source!.Value.Value, Is.Zero, "Opponent gets a sentinel per draw — count stays public.");
            }
            Assert.That(draws, Is.EqualTo(2), "Two replacements → two draw events for BOTH viewers.");
        }

        // ── §10 corpus addition — hero-power-sourced batch stays public ─────────

        [Test]
        public void HeroPower_Sourced_Damage_Resolves_Public_For_Both_Viewers()
        {
            var db = new FakeCardDatabase().Register(HpBolt);
            var proc = new CommandProcessor(db);
            var s = MainPhase();
            var (events, vis) = Exec(proc, s,
                new HeroPowerIntent(s.Players[0].Id, Array.Empty<TargetRef>()));

            int dmgIdx = -1;
            for (int i = 0; i < events.Count; i++)
                if (events[i].Kind == GameEventKind.DamageApplied) dmgIdx = i;
            Assert.That(dmgIdx, Is.GreaterThanOrEqualTo(0), "The bolt must have dealt damage.");

            foreach (int seat in new[] { 0, 1 })
            {
                var p = ProjectOne(events[dmgIdx], seat, vis[dmgIdx]);
                Assert.That(p.Source!.Value.Value, Is.Not.Zero,
                    "The ephemeral hero-power source is public (its act is public) — the emit-time " +
                    "MarkPublic covers what post-state resolution never could (§10 finding 2).");
                Assert.That(p.Target!.Value.Value, Is.EqualTo(s.Players[1].Hero.Id.Value));
                Assert.That(p.Amount, Is.EqualTo(1), "Damage magnitude is table-public.");
            }
        }

        // ── §10 corpus addition — weapon-break CardDestroyed stays public ───────

        [Test]
        public void Weapon_Break_Death_Event_Resolves_Public_For_Both_Viewers()
        {
            var db = new FakeCardDatabase().Register(Hatchet);
            var proc = new CommandProcessor(db);
            var s = MainPhase();
            var w1 = PutInHand(s, 0, "hatchet");
            var w2 = PutInHand(s, 0, "hatchet");

            Exec(proc, s, new PlayCardIntent(s.Players[0].Id, w1.Id, 0, Array.Empty<TargetRef>()));
            int oldWeaponId = s.Players[0].Weapon!.Id.Value;

            var (events, vis) = Exec(proc, s,
                new PlayCardIntent(s.Players[0].Id, w2.Id, 0, Array.Empty<TargetRef>()));

            int deathIdx = -1;
            for (int i = 0; i < events.Count; i++)
                if (events[i].Kind == GameEventKind.CardDestroyed) deathIdx = i;
            Assert.That(deathIdx, Is.GreaterThanOrEqualTo(0), "Equipping over must break the old weapon.");
            Assert.That(events[deathIdx].Source!.Value.Value, Is.EqualTo(oldWeaponId));

            foreach (int seat in new[] { 0, 1 })
            {
                var p = ProjectOne(events[deathIdx], seat, vis[deathIdx]);
                Assert.That(p.Source!.Value.Value, Is.EqualTo(oldWeaponId),
                    "The weapon was equipped (public) at emit time; post-state nulls it — emit-time capture wins.");
            }
        }

        // ── §7.1 test 10 — numeric side-channels (OBL-8 Amount sentinels) ───────

        [Test]
        public void Projection_Uses_Explicit_Field_Policy_And_Drops_Invalid_Cue()
        {
            var e = new GameEvent(
                GameEventKind.UnearthOffered,
                7,
                new InstanceId(4),
                new InstanceId(5),
                3,
                EventFlags.Lethal,
                (PresentationCue)int.MaxValue);
            var vis = new EventVisibility(IdVisibility.Public, IdVisibility.Public);

            foreach (int seat in new[] { 0, 1 })
            {
                var projected = ProjectOne(e, seat, vis);
                Assert.Multiple(() =>
                {
                    Assert.That(projected.Amount, Is.Null, "Unearth does not disclose a pool-size magnitude.");
                    Assert.That(projected.Flags, Is.EqualTo(EventFlags.None));
                    Assert.That(projected.CueOverride, Is.Null, "Unknown cues fall back to the semantic default.");
                });
            }
        }

        [Test]
        public void Projection_Rejects_An_Unknown_Semantic_Kind_For_The_Whole_Batch()
        {
            var events = new[]
            {
                new GameEvent(GameEventKind.CardPlayed, 1, null, null, null, EventFlags.None),
                new GameEvent((GameEventKind)int.MaxValue, 2, null, null, null, EventFlags.None),
            };
            var visibility = new[]
            {
                new EventVisibility(IdVisibility.Public, IdVisibility.Public),
                new EventVisibility(IdVisibility.Public, IdVisibility.Public),
            };

            Assert.That(
                () => GameStateProjector.ProjectEvents(events, visibility, 0),
                Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void Projection_Rejects_A_Nonparallel_Visibility_Batch()
        {
            var events = new[] { new GameEvent(GameEventKind.CardPlayed, 1, null, null, null, EventFlags.None) };

            Assert.That(
                () => GameStateProjector.ProjectEvents(events, Array.Empty<EventVisibility>(), 0),
                Throws.TypeOf<ArgumentException>());
        }

        // ── D-OQ1 — bounce event: owner sees the re-minted id, opponent sentinel ─

        [Test]
        public void Bounce_Event_Target_Is_OwnerOnly_After_ReMint()
        {
            var bounce = new CardDefinition(
                "bounce", "bounce", CardType.SPELL, 2, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                Array.Empty<Keyword>(),
                new[] { TestRules.Rule("bounce", Trigger.ON_CAST,
                    new ReturnToHandAction(new TargetSpec("ANY_MINION"))) },
                "", "", "art/bounce", SetId.CORE, true, null, "", false);
            var db = new FakeCardDatabase().Register(bounce);
            var proc = new CommandProcessor(db);
            var s = MainPhase();

            var minion = new CardInstance(s.NextInstanceId(), "vanilla", s.Players[1].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = 1,
                CurrentHealth = 1,
                MaxHealth = 1,
                Position = 0,
            };
            s.Players[1].Board.Add(minion);
            var spell = PutInHand(s, 0, "bounce");

            var (events, vis) = Exec(proc, s,
                new PlayCardIntent(s.Players[0].Id, spell.Id, 0, new[] { new TargetRef(minion.Id) }));

            int retIdx = -1;
            for (int i = 0; i < events.Count; i++)
                if (events[i].Kind == GameEventKind.CardBounced) retIdx = i;
            Assert.That(retIdx, Is.GreaterThanOrEqualTo(0));

            int newHandId = s.Players[1].Hand[0].Id.Value;
            var forOwner = ProjectOne(events[retIdx], 1, vis[retIdx]);
            var forCaster = ProjectOne(events[retIdx], 0, vis[retIdx]);

            Assert.That(forOwner.Target!.Value.Value, Is.EqualTo(newHandId),
                "Owner's event carries the re-minted id — event and snapshot agree (D-OQ1 ordering).");
            Assert.That(forCaster.Target!.Value.Value, Is.Zero,
                "The re-minted id is a hidden-hand id — passing it to the caster would reopen the D-OQ1 channel.");
        }
    }
}
