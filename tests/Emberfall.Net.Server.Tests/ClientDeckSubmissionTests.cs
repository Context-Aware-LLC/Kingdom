using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// EF-071 — client-supplied decks, revalidated server-side. Two halves:
    ///
    /// <para><b>Pure validation</b> (no sockets): <see cref="DeckSubmission.Validate"/> runs the same
    /// L1–L5 predicate the deckbuilder gates Save with, resolving every id through
    /// <c>ICardDatabase.TryGet</c> — the closure for VULN-001, trigger "Client-supplied deck lists". An
    /// unknown id is a rejection, never an exception; a copy-cap overflow smuggled across two entries is
    /// caught by canonicalizing the untrusted list first.</para>
    ///
    /// <para><b>Over real sockets</b>: a <c>/ws?deck=1</c> client's legal deck is dealt in place of the
    /// prebuilt pair (proved by the hero class in the initial snapshot), and an illegal deck is refused
    /// at the handshake — the socket is closed and no match is created.</para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]   // boots real Kestrel hosts on ephemeral ports, like the other socket fixtures
    public sealed class ClientDeckSubmissionTests
    {
        private static ICardDatabase Db => SessionHarness.Db;

        // ── Pure validation (no sockets) ─────────────────────────────────────────

        [Test]
        public void Validate_Accepts_A_Legal_Generated_Deck()
        {
            var msg = LegalSubmission(CardClass.GRAVEKEEP, CardClass.VERDANT);
            var result = DeckSubmission.Validate(msg, Db);

            Assert.That(result.Accepted, Is.True, result.Reason);
            Assert.That(result.Seat.HeroClass, Is.EqualTo(CardClass.GRAVEKEEP), "hero is the PRIMARY class");
            Assert.That(result.Seat.Deck, Has.Count.EqualTo(DeckValidator.DeckSize), "the dealt deck is the expanded 30");
        }

        [Test]
        public void Validate_Rejects_An_Unknown_Card_Id_Without_Throwing()
        {
            // The VULN-001 core: a client-authored id that is not in the DB must be a rejection resolved
            // through TryGet, never a KeyNotFoundException and never a mutation that reaches match state.
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines[0] = new DeckCardLine("no_such_card_zzz", lines[0].Count);
            var msg = new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            DeckSubmissionResult result = default;
            Assert.DoesNotThrow(() => result = DeckSubmission.Validate(msg, Db));
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("no_such_card_zzz").IgnoreCase);
        }

        [Test]
        public void Validate_Rejects_A_Deck_That_Is_Not_Thirty_Cards()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines.RemoveAt(lines.Count - 1);   // now < 30
            var msg = new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "an under-size deck is a draft, not queue-eligible");
        }

        [Test]
        public void Validate_Rejects_An_Off_Class_Card()
        {
            // A card from a third class the character does not own is an L4 break even though it resolves
            // in the DB — "the id is real" is necessary and insufficient (VULN-001 §4 rule 2 spirit).
            string offClass = FirstCollectibleOfClass(CardClass.EMBERWARD);
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines[0] = new DeckCardLine(offClass, lines[0].Count);
            var msg = new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "an EMBERWARD card is illegal for a GRAVEKEEP/VERDANT character");
        }

        [Test]
        public void Validate_Rejects_A_Copy_Cap_Overflow_Split_Across_Two_Entries()
        {
            // The untrusted-wire hardening: DeckValidator checks the cap PER entry, so {X:2}{X:2} would
            // each pass while summing to four. DeckSubmission canonicalizes to one-entry-per-id first, so
            // the merged count trips L2. A legal deckbuilder never emits this; the wire is not the builder.
            var pool = Db.AllCollectible()
                .Where(d => d.Class == CardClass.EMBERWARD || d.Class == CardClass.TIDECALLER || d.Class == CardClass.NEUTRAL)
                .Where(d => d.Rarity != Rarity.LEGENDARY)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            if (pool.Count < 27) Assert.Ignore("card pool too small to construct the split-entry case");

            var x = pool[0];
            var lines = new List<DeckCardLine> { new(x.Id, 2), new(x.Id, 2) };   // merges to 4 > cap 2
            foreach (var other in pool.Skip(1).Take(26)) lines.Add(new DeckCardLine(other.Id, 1));
            // Total across entries is 2+2+26 = 30, so WITHOUT the merge this passes L1 and per-entry L2.
            Assert.That(lines.Sum(l => l.Count), Is.EqualTo(DeckValidator.DeckSize));

            var msg = new SubmitDeckMsg("c", CardClass.EMBERWARD, CardClass.TIDECALLER, lines);
            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "four copies smuggled across two entries must be rejected");
        }

        [Test]
        public void Validate_Rejects_An_Invalid_Class_Pairing()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            // Same class twice is not a pairing; a NEUTRAL "hero" has no hero power.
            var samePair = new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.GRAVEKEEP, lines);
            var neutralPrimary = new SubmitDeckMsg("c", CardClass.NEUTRAL, CardClass.VERDANT, lines);

            Assert.That(DeckSubmission.Validate(samePair, Db).Accepted, Is.False);
            Assert.That(DeckSubmission.Validate(neutralPrimary, Db).Accepted, Is.False);
        }

        [Test]
        public void Validate_Rejects_A_Non_Positive_Copy_Count()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines.Add(new DeckCardLine(lines[0].CardId, -5));
            var msg = new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "a negative count cannot underflow the total past the gate");
        }

        // ── Over real sockets ─────────────────────────────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Submitted_Legal_Deck_Is_Dealt_In_Place_Of_The_Prebuilt_Pair()
        {
            await WithServerAsync(async wsUri =>
            {
                // Both seats submit; neither prebuilt class (EMBERWARD/TIDECALLER) may appear. Each viewer
                // reads its OWN hero out of its own initial snapshot, so the assertion never depends on
                // opponent-hero projection, and it is order-independent across the pairing race.
                var deckUri = new Uri(wsUri + "?deck=1");
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(deckUri, CancellationToken.None);
                await SubmitAsync(c0, "device-gk", CardClass.GRAVEKEEP, CardClass.VERDANT);
                await c1.ConnectAsync(deckUri, CancellationToken.None);
                await SubmitAsync(c1, "device-vd", CardClass.VERDANT, CardClass.GRAVEKEEP);

                var s0 = await ExpectAsync<MatchStartMsg>(c0);
                var s1 = await ExpectAsync<MatchStartMsg>(c1);

                var own0 = s0.Snapshot.State.Players[s0.Seat].Hero.Class;
                var own1 = s1.Snapshot.State.Players[s1.Seat].Hero.Class;
                Assert.That(new[] { own0, own1 }, Is.EquivalentTo(new[] { CardClass.GRAVEKEEP, CardClass.VERDANT }),
                    "both seats were dealt their submitted deck's PRIMARY class, not the prebuilt EMBERWARD/TIDECALLER");
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task An_Illegal_Deck_Is_Refused_At_The_Handshake()
        {
            await WithServerAsync(async wsUri =>
            {
                var deckUri = new Uri(wsUri + "?deck=1");
                using var attacker = new ClientWebSocket();
                await attacker.ConnectAsync(deckUri, CancellationToken.None);

                // A deck carrying an id that is not in the server DB — the injection VULN-001 warns about.
                await SendAsync(attacker, ProtocolCodec.Encode(
                    new HelloMsg("device-evil", SessionHarness.CurrentContractVersion, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
                var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
                lines[0] = new DeckCardLine("no_such_card_zzz", lines[0].Count);
                await SendAsync(attacker, ProtocolCodec.Encode(
                    new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT, lines), ProtocolCodec.PreMatch, 1));

                bool closed = await WaitForCloseAsync(attacker, TimeSpan.FromSeconds(10));
                Assert.That(closed, Is.True, "an illegal deck must be refused at the handshake, never seated");
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Plain_Socket_Still_Gets_The_Prebuilt_Deck()
        {
            // The additive guarantee: a client that never opts into ?deck=1 is dealt exactly today's
            // prebuilt pair, so EF-071 does not break the shipping Unity/NetSoak clients.
            await WithServerAsync(async wsUri =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendAsync(c0, ProtocolCodec.Encode(
                    new HelloMsg("device-a", SessionHarness.CurrentContractVersion, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendAsync(c1, ProtocolCodec.Encode(
                    new HelloMsg("device-b", SessionHarness.CurrentContractVersion, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));

                var s0 = await ExpectAsync<MatchStartMsg>(c0);
                var s1 = await ExpectAsync<MatchStartMsg>(c1);
                var classes = new[]
                {
                    s0.Snapshot.State.Players[s0.Seat].Hero.Class,
                    s1.Snapshot.State.Players[s1.Seat].Hero.Class,
                };
                Assert.That(classes, Is.EquivalentTo(new[] { CardClass.EMBERWARD, CardClass.TIDECALLER }));
            });
        }

        // ── Deck construction ─────────────────────────────────────────────────────

        private static SubmitDeckMsg LegalSubmission(CardClass primary, CardClass secondary)
            => new SubmitDeckMsg("c", primary, secondary, LegalLines(primary, secondary));

        /// <summary>A legal 30-card decklist for the pairing, as wire lines. Generated (not hand-authored)
        /// via the same <see cref="DefaultDeckGenerator"/> a new character opens on, so it always passes
        /// L1–L5 and the test asserts the SERVER'S verdict, not the generator's.</summary>
        private static List<DeckCardLine> LegalLines(CardClass primary, CardClass secondary)
        {
            var character = new Character("c", "Test", primary, secondary);
            var deck = DefaultDeckGenerator.Generate(character, Db, new DeterministicRandom(0xD00DUL), "test-deck");
            return deck.Entries.Select(e => new DeckCardLine(e.CardId, e.Count)).ToList();
        }

        private static string FirstCollectibleOfClass(CardClass cls)
            => Db.AllCollectible().Where(d => d.Class == cls).OrderBy(d => d.Id, StringComparer.Ordinal).First().Id;

        // ── Socket harness (mirrors GuestIdentityTests) ───────────────────────────

        private static async Task WithServerAsync(Func<Uri, Task> body)
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-deck-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
            });
            await app.StartAsync();
            try
            {
                var baseUri = new Uri(app.Urls.First());
                await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"));
            }
            finally
            {
                await app.StopAsync();
                if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true);
            }
        }

        private static async Task SubmitAsync(ClientWebSocket ws, string token, CardClass primary, CardClass secondary)
        {
            await SendAsync(ws, ProtocolCodec.Encode(
                new HelloMsg(token, SessionHarness.CurrentContractVersion, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
            await SendAsync(ws, ProtocolCodec.Encode(
                new SubmitDeckMsg("c", primary, secondary, LegalLines(primary, secondary)), ProtocolCodec.PreMatch, 1));
        }

        private static Task SendAsync(ClientWebSocket ws, byte[] frame)
            => ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        private static async Task<bool> WaitForCloseAsync(ClientWebSocket ws, TimeSpan timeout)
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return true;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (WebSocketException) { return true; }
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var res = ProtocolCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (!res.Ok) throw new InvalidOperationException($"Server sent undecodable frame: {res.Error} {res.Detail}");
                if (res.Message is T t) return t;
            }
        }
    }
}
