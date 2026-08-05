using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Projection
{
    /// <summary>
    /// WS A3 snapshot suite (dev-docs/architecture/determinism-and-projection.md:20 +
    /// counter-normalization; dev-docs/architecture/determinism-and-projection.md:20). Test 6 (honest AI) is
    /// sequenced behind the IIntentSource→Snapshot binding and deliberately absent.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ProjectorTests
    {
        private const int DeckSize = 12;

        private static (LocalMatchHost host, GameState state) NewMatch(ulong seed)
        {
            var db = new FakeCardDatabase();
            var processor = new CommandProcessor(db);
            var setup = new MatchSetup(
                CardClass.EMBERWARD, FakeCardDatabase.Deck("embermark", DeckSize),
                CardClass.TIDECALLER, FakeCardDatabase.Deck("tidemark", DeckSize));
            var host = new LocalMatchHost(processor, db, setup);
            var state = host.BeginMatch(new MatchId("M"), new PlayerId("P0"), new PlayerId("P1"), seed);
            return (host, state);
        }

        /// <summary>Play a short deterministic line so the corpus has draws, plays and turns.</summary>
        private static void PlayOpening(LocalMatchHost host, GameState s)
        {
            host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
            host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));
            host.Submit(new EndTurnIntent(s.ActivePlayer));
            host.Submit(new EndTurnIntent(s.ActivePlayer));
        }

        private static byte[] Ser(GameState s)
        {
            var sink = new BufferSink();
            GameStateSerializer.Serialize(s, sink);
            return sink.ToArray();
        }

        private static bool ContainsToken(byte[] haystack, string token)
        {
            var needle = Encoding.UTF8.GetBytes(token);
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                if (hit) return true;
            }
            return false;
        }

        // ── §7.1 test 1 — golden leak scan over serialized snapshots ─────────────

        [Test]
        public void Leak_Scan_Serialized_Snapshot_Contains_No_Hidden_Tokens()
        {
            var (host, s) = NewMatch(0xA3A3UL);
            PlayOpening(host, s);

            for (int viewerSeat = 0; viewerSeat < 2; viewerSeat++)
            {
                var viewer = s.Players[viewerSeat].Id;
                var proj = host.Snapshot(viewer);
                var bytes = Ser(proj);

                // Hidden tokens for THIS viewer: every DefinitionId currently resident in a
                // hidden zone (either deck, opponent hand), excluding ids that also appear in a
                // zone this viewer can see (public zones or own hand) — those are legitimately
                // in the payload.
                var visibleDefs = new HashSet<string>();
                for (int seat = 0; seat < 2; seat++)
                {
                    foreach (var c in s.Players[seat].Board) visibleDefs.Add(c.DefinitionId);
                    foreach (var c in s.Players[seat].Graveyard) visibleDefs.Add(c.DefinitionId);
                }
                foreach (var c in s.Players[viewerSeat].Hand) visibleDefs.Add(c.DefinitionId);

                var hiddenDefs = new HashSet<string>();
                for (int seat = 0; seat < 2; seat++)
                    foreach (var c in s.Players[seat].Deck) hiddenDefs.Add(c.DefinitionId);
                foreach (var c in s.Players[1 - viewerSeat].Hand) hiddenDefs.Add(c.DefinitionId);
                hiddenDefs.ExceptWith(visibleDefs);

                Assert.That(hiddenDefs, Is.Not.Empty, "Corpus sanity: there must be hidden cards to plant.");
                foreach (var def in hiddenDefs)
                    Assert.That(ContainsToken(bytes, def), Is.False,
                        $"LEAK: hidden DefinitionId '{def}' surfaced in Snapshot({viewer}).");

                Assert.That(proj.Rng.RawState, Is.Zero, "Live RNG cursor must never leave the server.");
            }
        }

        [Test]
        public void Hidden_Zone_Cards_Are_Exactly_The_CardBack_Form()
        {
            var (host, s) = NewMatch(0xBEE5UL);
            PlayOpening(host, s);

            var viewer = s.Players[0].Id;
            var proj = host.Snapshot(viewer);

            void AssertCardBack(CardInstance c, Zone zone)
            {
                Assert.That(c.Id.Value, Is.Zero);
                Assert.That(c.DefinitionId, Is.Empty);
                Assert.That(c.CurrentAttack, Is.Zero);
                Assert.That(c.CurrentHealth, Is.Zero);
                Assert.That(c.MaxHealth, Is.Zero);
                Assert.That(c.CurrentDurability, Is.Null);
                Assert.That(c.Keywords, Is.Empty);
                Assert.That(c.Enchantments, Is.Empty);
                Assert.That(c.Position, Is.Zero);
                Assert.That(c.Zone, Is.EqualTo(zone), "Zone is preserved (shape, not information).");
            }

            foreach (var seat in new[] { 0, 1 })
            {
                Assert.That(proj.Players[seat].Deck, Has.Count.EqualTo(s.Players[seat].Deck.Count),
                    "Deck.Count is public and survives as list length.");
                foreach (var c in proj.Players[seat].Deck) AssertCardBack(c, Zone.Deck);
            }
            Assert.That(proj.Players[1].Hand, Has.Count.EqualTo(s.Players[1].Hand.Count));
            foreach (var c in proj.Players[1].Hand) AssertCardBack(c, Zone.Hand);
        }

        // ── §7.1 test 2 — null → null ────────────────────────────────────────────

        [Test]
        public void Null_PendingChoice_Projects_To_Null_For_Both_Viewers()
        {
            var (host, s) = NewMatch(0x11UL);
            Assert.That(host.Snapshot(s.Players[0].Id).PendingChoice, Is.Null);
            Assert.That(host.Snapshot(s.Players[1].Id).PendingChoice, Is.Null);
        }

        [Test]
        public void Ongoing_Effects_Project_Public_Status_But_Not_The_Rule_Reference()
        {
            var (host, s) = NewMatch(0x0EUL);
            var effect = OngoingEffectInstance.CreateFinite(s.NextInstanceId(), s.Players[0].Id,
                "public_source_definition", "private_rule_reference", 3);
            s.OngoingEffects.Add(effect);

            GameState p0 = host.Snapshot(s.Players[0].Id);
            GameState p1 = host.Snapshot(s.Players[1].Id);

            foreach (GameState projected in new[] { p0, p1 })
            {
                OngoingEffectInstance visible = projected.OngoingEffects.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(visible.Id, Is.EqualTo(effect.Id));
                    Assert.That(visible.Controller, Is.EqualTo(effect.Controller));
                    Assert.That(visible.SourceDefinitionId, Is.EqualTo("public_source_definition"));
                    Assert.That(visible.Lifetime, Is.EqualTo(OngoingEffectLifetime.FINITE));
                    Assert.That(visible.RemainingUses, Is.EqualTo(3));
                    Assert.That(visible.RuleId, Is.Empty,
                        "The engine-only lookup key must not survive default-deny projection.");
                    Assert.That(ContainsToken(Ser(projected), "private_rule_reference"), Is.False);
                });
            }
        }

        // ── §7.1 test 3 — purity ─────────────────────────────────────────────────

        [Test]
        public void Projection_Never_Mutates_The_Authoritative_State()
        {
            var (host, s) = NewMatch(0x99UL);
            PlayOpening(host, s);
            string before = GameStateHasher.Hash(s);
            host.Snapshot(s.Players[0].Id);
            host.Snapshot(s.Players[1].Id);
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(before));
        }

        // ── §7.1 test 4 — determinism ────────────────────────────────────────────

        [Test]
        public void Same_State_Same_Viewer_Is_ByteIdentical()
        {
            var (host, s) = NewMatch(0x77UL);
            PlayOpening(host, s);
            var a = Ser(host.Snapshot(s.Players[0].Id));
            var b = Ser(host.Snapshot(s.Players[0].Id));
            Assert.That(a, Is.EqualTo(b));
        }

        // ── §7.1 test 7 — owner-own-deck ─────────────────────────────────────────

        [Test]
        public void Owners_Own_Deck_Is_Redacted_Too()
        {
            // Guarded separately from the opponent case: this is the ruling a future PR most
            // plausibly "fixes" with `if (viewer owns zone) don't redact` (§7.1 test 7).
            var (host, s) = NewMatch(0x55UL);
            var proj = host.Snapshot(s.Players[0].Id);
            Assert.That(s.Players[0].Deck, Is.Not.Empty, "Corpus sanity.");
            foreach (var c in proj.Players[0].Deck)
            {
                Assert.That(c.Id.Value, Is.Zero, "You know your decklist, never your draw order.");
                Assert.That(c.DefinitionId, Is.Empty);
            }
        }

        // ── §7.1 test 8 — sequence stability ─────────────────────────────────────

        [Test]
        public void Visible_Entities_Keep_Their_Ids_Across_Consecutive_Snapshots()
        {
            var (host, s) = NewMatch(0x1234UL);
            host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
            host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));

            var viewer = s.Players[0].Id;
            var heroIds = new int[2];
            var prev = host.Snapshot(viewer);
            heroIds[0] = prev.Players[0].Hero.Id.Value;
            heroIds[1] = prev.Players[1].Hero.Id.Value;

            for (int turn = 0; turn < 4; turn++)
            {
                host.Submit(new EndTurnIntent(s.ActivePlayer));
                var next = host.Snapshot(viewer);

                Assert.That(next.Players[0].Hero.Id.Value, Is.EqualTo(heroIds[0]), "No id churn frame-to-frame.");
                Assert.That(next.Players[1].Hero.Id.Value, Is.EqualTo(heroIds[1]));
                foreach (var c in next.Players[1].Hand)
                    Assert.That(c.Id.Value, Is.Zero, "Opponent card-backs are sentinel in EVERY frame.");
                prev = next;
            }
        }

        // ── §7.1 test 9 — reconnect/rebuild equivalence ──────────────────────────

        [Test]
        public void Snapshot_From_A_Replay_Rebuilt_Session_Is_ByteIdentical()
        {
            byte[] Run(int viewerSeat)
            {
                var (host, s) = NewMatch(0xFEEDUL);
                PlayOpening(host, s);
                return Ser(host.Snapshot(s.Players[viewerSeat].Id));
            }
            var firstBuild = Run(0);
            var rebuild = Run(0);
            Assert.That(rebuild, Is.EqualTo(firstBuild), "Rebuild via (seed, intents) replay must project identically.");
            var firstBuildSeat1 = Run(1);
            var rebuildSeat1 = Run(1);
            Assert.That(rebuildSeat1, Is.EqualTo(firstBuildSeat1));
        }

        // ── counter normalization (§5.2 / design §5) ─────────────────────────────

        [Test]
        public void NextInstanceCounter_Is_Max_Visible_Plus_One_And_Hides_Hidden_Mints()
        {
            var (host, s) = NewMatch(0xC0DEUL);
            PlayOpening(host, s);

            for (int viewerSeat = 0; viewerSeat < 2; viewerSeat++)
            {
                var proj = host.Snapshot(s.Players[viewerSeat].Id);

                int max = 0;
                foreach (var ps in proj.Players)
                {
                    if (ps.Hero.Id.Value > max) max = ps.Hero.Id.Value;
                    if (ps.Weapon != null && ps.Weapon.Id.Value > max) max = ps.Weapon.Id.Value;
                    foreach (var c in ps.Board) if (c.Id.Value > max) max = c.Id.Value;
                    foreach (var c in ps.Graveyard) if (c.Id.Value > max) max = c.Id.Value;
                    foreach (var c in ps.Hand) if (c.Id.Value > max) max = c.Id.Value;
                }
                Assert.That(proj.NextInstanceCounter, Is.EqualTo(max + 1),
                    "Projected counter must be a pure function of the projected id set.");
                Assert.That(proj.NextInstanceCounter, Is.LessThanOrEqualTo(s.NextInstanceCounter),
                    "Never above the authoritative counter (nothing invented).");
            }
        }
    }
}
