using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Decks;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests
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
            lines[0] = new DeckCardLine(FixtureCard.Id("no_such_card_zzz"), lines[0].Count);
            var msg = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            DeckSubmissionResult result = default;
            Assert.DoesNotThrow(() => result = DeckSubmission.Validate(msg, Db));
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain(FixtureCard.Id("no_such_card_zzz").ToString()));
        }

        [Test]
        public void Validate_Rejects_A_Deck_That_Is_Not_Thirty_Cards()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines.RemoveAt(lines.Count - 1);   // now < 30
            var msg = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "an under-size deck is a draft, not queue-eligible");
        }

        [Test]
        public void Validate_Rejects_An_Off_Class_Card()
        {
            // A card from a third class the character does not own is an L4 break even though it resolves
            // in the DB — "the id is real" is necessary and insufficient (VULN-001 §4 rule 2 spirit).
            int offClass = FirstCollectibleOfClass(CardClass.EMBERWARD);
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines[0] = new DeckCardLine(offClass, lines[0].Count);
            var msg = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

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
                .OrderBy(d => d.Id)
                .ToList();
            if (pool.Count < 27) Assert.Ignore("card pool too small to construct the split-entry case");

            var x = pool[0];
            var lines = new List<DeckCardLine> { new(x.Id, 2), new(x.Id, 2) };   // merges to 4 > cap 2
            foreach (var other in pool.Skip(1).Take(26)) lines.Add(new DeckCardLine(other.Id, 1));
            // Total across entries is 2+2+26 = 30, so WITHOUT the merge this passes L1 and per-entry L2.
            Assert.That(lines.Sum(l => l.Count), Is.EqualTo(DeckValidator.DeckSize));

            var msg = new SubmitDeckMsg(CardClass.EMBERWARD, CardClass.TIDECALLER, lines);
            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "four copies smuggled across two entries must be rejected");
        }

        [Test]
        public void Validate_Rejects_An_Invalid_Class_Pairing()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            // Same class twice is not a pairing; a NEUTRAL "hero" has no hero power.
            var samePair = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.GRAVEKEEP, lines);
            var neutralPrimary = new SubmitDeckMsg(CardClass.NEUTRAL, CardClass.VERDANT, lines);

            Assert.That(DeckSubmission.Validate(samePair, Db).Accepted, Is.False);
            Assert.That(DeckSubmission.Validate(neutralPrimary, Db).Accepted, Is.False);
        }

        [Test]
        public void Validate_Rejects_A_Non_Positive_Copy_Count()
        {
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            lines.Add(new DeckCardLine(lines[0].CardId, -5));
            var msg = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db);
            Assert.That(result.Accepted, Is.False, "a negative count cannot underflow the total past the gate");
        }

        [Test]
        public void Validate_Runs_L6_When_An_Ownership_Source_Is_Supplied()
        {
            // NOR-313: the seam MatchGate threads through AccountOwnedCounts.ForAsync. The SAME legal deck is
            // accepted with no source (null, a tooling seat) and rejected once an ownership source that
            // holds nothing is supplied — that flip is L6 actually reaching the submission path.
            var msg = LegalSubmission(CardClass.GRAVEKEEP, CardClass.VERDANT);
            Assert.That(DeckSubmission.Validate(msg, Db).Accepted, Is.True,
                "a null ownership source leaves L6 unenforced, exactly as the game ships");

            var ownsNothing = new OwnedCardCounts();   // present-but-empty: owns nothing, not "unknown"
            var rejected = DeckSubmission.Validate(msg, Db, ownsNothing);
            Assert.That(rejected.Accepted, Is.False, "an account that owns none of its cards fails L6");
            Assert.That(rejected.Reason, Does.StartWith("You don't own"), "the L6 reason reaches the caller verbatim");
        }

        [Test]
        public void Validate_Accepts_A_Legal_Deck_Under_The_Superuser_At_Limit_Source()
        {
            // AccountOwnedCounts.ForAsync hands a superuser AtLimitOwnedCounts: L6 runs and every card
            // is owned up to its rarity limit, so a legal deck passes.
            var msg = LegalSubmission(CardClass.GRAVEKEEP, CardClass.VERDANT);
            var result = DeckSubmission.Validate(msg, Db, new AtLimitOwnedCounts(Db));
            Assert.That(result.Accepted, Is.True, result.Reason);
        }

        [Test]
        public void Validate_Refuses_Three_Copies_Of_A_Common_Under_The_Superuser_At_Limit_Source()
        {
            // The superuser cap moved from int.MaxValue to the rarity limit; L2 refuses a third copy
            // exactly as it does for everyone.
            var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
            int idx = lines.FindIndex(l => Db.TryGet(l.CardId, out var d) && d.Rarity == Rarity.COMMON);
            Assert.That(idx, Is.GreaterThanOrEqualTo(0), "the generated deck holds a common");
            // Keep the deck at 30 cards so the size rule cannot be what refuses it: take the extra
            // copies back out of other lines.
            int extra = 3 - lines[idx].Count;
            lines[idx] = new DeckCardLine(lines[idx].CardId, 3);
            for (int k = 0; k < lines.Count && extra > 0; k++)
            {
                if (k == idx) continue;
                int take = Math.Min(extra, lines[k].Count);
                lines[k] = new DeckCardLine(lines[k].CardId, lines[k].Count - take);
                extra -= take;
            }
            lines.RemoveAll(l => l.Count == 0);
            Assert.That(extra, Is.EqualTo(0));
            idx = lines.FindIndex(l => l.Count == 3);
            var msg = new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines);

            var result = DeckSubmission.Validate(msg, Db, new AtLimitOwnedCounts(Db));

            Assert.That(result.Accepted, Is.False);
            // At-limit owns 2, so L6 refuses a third copy too; assert the copy-cap sentence ("Only 2 of
            // X.") is among the reasons, so this proves L2 fired and not merely that L6 did.
            Assert.That(result.Reason, Does.Contain("Only 2 of"), "the L2 copy-cap rule must be named");
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
                    new HelloMsg("device-evil", ProtocolContract.Version, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
                var lines = LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT);
                lines[0] = new DeckCardLine(FixtureCard.Id("no_such_card_zzz"), lines[0].Count);
                await SendAsync(attacker, ProtocolCodec.Encode(
                    new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT, lines), ProtocolCodec.PreMatch, 1));

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
                    new HelloMsg("device-a", ProtocolContract.Version, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendAsync(c1, ProtocolCodec.Encode(
                    new HelloMsg("device-b", ProtocolContract.Version, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));

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

        // ── Card backs (#421) ─────────────────────────────────────────────────────
        //
        // The submission's cardBackId is client-authored. The gate honours it only when the account
        // it already resolved owns that back, and otherwise seats the player on the default with no
        // close and nothing said to the client. The verdict tests tell owned, unowned and unknown
        // apart; the socket tests show what each verdict puts on both seats' match start.

        [Test]
        public async Task An_owned_back_is_honoured()
        {
            var (account, ledger) = await AccountWithLedgerAsync();
            CardBackChoice choice = await DeckSubmission.ResolveCardBackAsync(
                CardBackDefinitions.DefaultId, account, ledger);

            Assert.That(choice.Verdict, Is.EqualTo(CardBackVerdict.Owned), "every durable account owns the default");
            Assert.That(choice.BackId, Is.EqualTo(CardBackDefinitions.DefaultId));
        }

        [Test]
        public void An_owned_back_reaches_both_seats()
        {
            // The gate has already judged the back by the time a session holds it, so the session
            // carries whatever it was handed. 42 is a value nothing else produces: seeing it at
            // index 0 on BOTH seats' match start is the route, seat-indexed, not the default.
            var c0 = new FakeConnection();
            var c1 = new FakeConnection();
            var session = new MatchSession(
                new MatchId("m-backs"), SessionHarness.Seed, new ServerOptions(), new FakeTime(), Db,
                PrebuiltDecks.EmberwardVsTidecaller(), new InMemoryReplayStore(),
                new PlayerId("p0"), new PlayerId("p1"),
                p0CardBack: 42, p1CardBack: CardBackDefinitions.DefaultId);
            session.Start(c0, c1);

            foreach (FakeConnection conn in new[] { c0, c1 })
            {
                MatchStartMsg start = conn.Last<MatchStartMsg>();
                Assert.That(start.BackIds, Is.EqualTo(new[] { 42, CardBackDefinitions.DefaultId }),
                    $"seat {start.Seat} sees seat 0's back first and seat 1's second");
            }
        }

        [Test]
        public async Task An_owned_prize_back_reaches_both_seats_and_an_unowned_one_does_not()
        {
            int owned = CardBackDefinitions.PoolIds[0];
            int unowned = CardBackDefinitions.PoolIds[1];
            var (account, ledger) = await AccountWithLedgerAsync();
            await CardBackOwnership.GrantAsync(ledger, account, owned, "test", "grant-owned");
            Assert.That((await DeckSubmission.ResolveCardBackAsync(owned, account, ledger)).Verdict,
                Is.EqualTo(CardBackVerdict.Owned));
            CardBackChoice refused = await DeckSubmission.ResolveCardBackAsync(unowned, account, ledger);
            Assert.That((refused.Verdict, refused.BackId), Is.EqualTo((CardBackVerdict.Unowned, CardBackDefinitions.DefaultId)));

            // Over the wire: one socket asks for the back the account owns, the other for one it
            // does not. Seats are dealt by the queue, so the expectation is built from the seat the
            // owning socket landed in; both clients must agree on it.
            var starts = await SeatTwoAuthenticatedAsync(new[] { owned, unowned }, revokeDefault: false, granted: owned);
            int ownedSeat = starts[0].Start.Seat;
            Assert.That(starts[1].Start.Seat, Is.EqualTo(1 - ownedSeat));
            var expected = new int[2];
            expected[ownedSeat] = owned;
            expected[1 - ownedSeat] = CardBackDefinitions.DefaultId;
            foreach (var (start, json) in starts)
            {
                Assert.That(json, Does.Contain($"\"backIds\":[{expected[0]},{expected[1]}]"));
                Assert.That(start.BackIds, Is.EqualTo(expected), $"seat {start.Seat}");
            }
        }

        [Test]
        public async Task An_unowned_back_falls_back_to_the_default()
        {
            var (account, ledger) = await AccountWithLedgerAsync();
            await RevokeDefaultAsync(account, ledger);

            CardBackChoice choice = await DeckSubmission.ResolveCardBackAsync(
                CardBackDefinitions.DefaultId, account, ledger);
            Assert.That(choice.Verdict, Is.EqualTo(CardBackVerdict.Unowned));
            Assert.That(choice.BackId, Is.EqualTo(CardBackDefinitions.DefaultId));

            // Over the wire: no close, no error, both seats seated on the default.
            var starts = await SeatTwoAuthenticatedAsync(CardBackDefinitions.DefaultId, revokeDefault: true);
            foreach (var (start, json) in starts)
            {
                Assert.That(json, Does.Contain("\"backIds\":[1,1]"), "the server sent the backs it settled on");
                Assert.That(start.BackIds, Is.EqualTo(new[] { CardBackDefinitions.DefaultId, CardBackDefinitions.DefaultId }));
            }
        }

        [Test]
        public async Task An_unknown_back_falls_back_to_the_default()
        {
            var (account, ledger) = await AccountWithLedgerAsync();
            CardBackChoice choice = await DeckSubmission.ResolveCardBackAsync(999, account, ledger);
            Assert.That(choice.Verdict, Is.EqualTo(CardBackVerdict.Unknown));
            Assert.That(choice.BackId, Is.EqualTo(CardBackDefinitions.DefaultId));
            Assert.That(DeckSubmission.ResolveCardBack(-5, null).Verdict, Is.EqualTo(CardBackVerdict.Unknown),
                "a negative id is unknown, not an error");

            var starts = await SeatTwoAuthenticatedAsync(999, revokeDefault: false);
            foreach (var (start, json) in starts)
            {
                Assert.That(json, Does.Contain("\"backIds\":[1,1]"));
                Assert.That(start.BackIds, Is.EqualTo(new[] { CardBackDefinitions.DefaultId, CardBackDefinitions.DefaultId }));
            }
        }

        [Test]
        public async Task An_unset_back_or_an_unattributed_seat_uses_the_default()
        {
            Assert.That(DeckSubmission.ResolveCardBack(0, null).Verdict, Is.EqualTo(CardBackVerdict.Unset));
            Assert.That(DeckSubmission.ResolveCardBack(0, null).BackId, Is.EqualTo(CardBackDefinitions.DefaultId));

            CardBackChoice tokenless = await DeckSubmission.ResolveCardBackAsync(
                CardBackDefinitions.DefaultId, account: null, ledger: new InMemoryEntitlementLedger());
            Assert.That(tokenless.Verdict, Is.EqualTo(CardBackVerdict.OwnershipUnavailable));
            Assert.That(tokenless.BackId, Is.EqualTo(CardBackDefinitions.DefaultId));

            var (account, _) = await AccountWithLedgerAsync();
            CardBackChoice down = await DeckSubmission.ResolveCardBackAsync(
                CardBackDefinitions.DefaultId, account, new FailingLedger());
            Assert.That(down.Verdict, Is.EqualTo(CardBackVerdict.OwnershipUnavailable),
                "a ledger that cannot be read costs the player their back, never their match");
            Assert.That(down.BackId, Is.EqualTo(CardBackDefinitions.DefaultId));
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_bot_seat_uses_the_default()
        {
            Assert.That(new SeatDeck(CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck()).CardBackId,
                Is.EqualTo(CardBackDefinitions.DefaultId), "a bot's deck is built with no back of its own");

            var pool = new BotPool(Db);
            pool.MarkReady(new ResolvedBotAccount(
                "ember_knight", AccountId.FromAuthenticatedAccount("player-ember_knight"), "ember_knight",
                new SeatDeck(CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck())));
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-backs-bot-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions
                {
                    BotFillDelayMs = 200,
                    Matchmaking = new MatchmakingQueueOptions { BandWideningIntervalMs = 100 },
                },
            }, pool);
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                using var human = new ClientWebSocket();
                await human.ConnectAsync(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws?deck=1"), CancellationToken.None);
                await SendAsync(human, ProtocolCodec.Encode(
                    new HelloMsg("device-backs-bot", ProtocolContract.Version, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
                await SendAsync(human, ProtocolCodec.Encode(
                    new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT,
                        LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT), 999), ProtocolCodec.PreMatch, 1));

                var (start, json) = await ExpectRawAsync<MatchStartMsg>(human);
                // The bot seat's back is on the wire as a back, the same field a human's rides in.
                Assert.That(json, Does.Contain("\"backIds\":[1,1]"));
                Assert.That(start.BackIdOf(1 - start.Seat), Is.EqualTo(CardBackDefinitions.DefaultId));
            }
            finally
            {
                await built.App.StopAsync();
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
        }

        private const string BackSubject = "supabase-user-card-backs";

        private static async Task<(Account Account, InMemoryEntitlementLedger Ledger)> AccountWithLedgerAsync()
        {
            var store = new InMemoryAccountStore(new FakeTime());
            Account account = await store.ResolveOrCreateAsync("supabase", HashCredential(BackSubject), "backs@example.com");
            return (account, new InMemoryEntitlementLedger());
        }

        private static async Task RevokeDefaultAsync(Account account, IEntitlementLedger ledger)
        {
            // Granted first, so the starter-v1 grant cannot land again on top of the revocation.
            await CardBackOwnership.EnsureDefaultAsync(ledger, account);
            await ledger.GrantAsync(new EntitlementGrant(account.PlayerId, CardBackOwnership.Item(CardBackDefinitions.DefaultId),
                -1, "revoke", "revoke-default-back", account.CreatedAt));
        }

        /// <summary>Two authenticated <c>?deck=1</c> sockets of one Superuser account (so L6 passes
        /// a generated deck and the refusal under test can only be the back's), each asking for
        /// <paramref name="cardBackId"/>. Returns each seat's match start with its raw JSON.</summary>
        private static Task<List<(MatchStartMsg Start, string Json)>> SeatTwoAuthenticatedAsync(
            int cardBackId, bool revokeDefault)
            => SeatTwoAuthenticatedAsync(new[] { cardBackId, cardBackId }, revokeDefault, granted: 0);

        /// <summary>As above, the first socket asking for <paramref name="cardBackIds"/>[0] and the
        /// second for [1], with <paramref name="granted"/> (when positive) granted to the account
        /// first. Results are in socket order, not seat order.</summary>
        private static async Task<List<(MatchStartMsg Start, string Json)>> SeatTwoAuthenticatedAsync(
            int[] cardBackIds, bool revokeDefault, int granted)
        {
            var store = new InMemoryAccountStore(new FakeTime());
            var ledger = new InMemoryEntitlementLedger();
            Account account = await store.ResolveOrCreateAsync("supabase", HashCredential(BackSubject), "backs@example.com");
            account = (await store.SetKindAsync(account.PlayerId, AccountKind.Superuser))!;
            if (revokeDefault) await RevokeDefaultAsync(account, ledger);
            if (granted > 0) await CardBackOwnership.GrantAsync(ledger, account, granted, "test", "grant-" + granted);

            var starts = new List<(MatchStartMsg, string)>();
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-backs-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                TokenVerifier = new StubVerifier(),
                AccountStore = store,
                EntitlementLedger = ledger,
            });
            await app.StartAsync();
            try
            {
                var baseUri = new Uri(app.Urls.First());
                var deckUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws?deck=1");
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                var sockets = new[] { c0, c1 };
                for (int i = 0; i < sockets.Length; i++)
                {
                    ClientWebSocket ws = sockets[i];
                    await ws.ConnectAsync(deckUri, CancellationToken.None);
                    await SendAsync(ws, ProtocolCodec.Encode(
                        new HelloMsg("device-backs", ProtocolContract.Version, "test", "deck-tests",
                            DisplayName: "", AuthToken: "valid.token.here"), ProtocolCodec.PreMatch, 0));
                    await SendAsync(ws, ProtocolCodec.Encode(
                        new SubmitDeckMsg(CardClass.GRAVEKEEP, CardClass.VERDANT,
                            LegalLines(CardClass.GRAVEKEEP, CardClass.VERDANT), cardBackIds[i]), ProtocolCodec.PreMatch, 1));
                }
                starts.Add(await ExpectRawAsync<MatchStartMsg>(c0));
                starts.Add(await ExpectRawAsync<MatchStartMsg>(c1));
            }
            finally
            {
                await app.StopAsync();
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
            return starts;
        }

        private static string HashCredential(string value)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity(BackSubject, "backs@example.com", EmailVerified: true)));
        }

        /// <summary>A ledger whose every call fails, as a database outage would.</summary>
        private sealed class FailingLedger : IEntitlementLedger
        {
            private static InvalidOperationException Down() => new InvalidOperationException("ledger down");
            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default) => throw Down();
            public Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default) => throw Down();
            public Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(string playerId, CancellationToken ct = default) => throw Down();
            public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default) => throw Down();
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default) => throw Down();
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default) => throw Down();
            public Task<EntitlementGrant?> FindAsync(string externalTxnId, CancellationToken ct = default) => throw Down();
        }

        // ── Deck construction ─────────────────────────────────────────────────────

        /// <summary>The submission carried a character id as its first member until ADR-0009. The
        /// message's own doc had always said that id "is not an identity the server trusts", which is
        /// why removing it cost the wire nothing — the pairing was already the only part of it the
        /// server read.</summary>
        private static SubmitDeckMsg LegalSubmission(CardClass primary, CardClass secondary)
            => new SubmitDeckMsg(primary, secondary, LegalLines(primary, secondary));

        /// <summary>A legal 30-card decklist for the pairing, as wire lines. Generated (not hand-authored)
        /// via the same <see cref="DefaultDeckGenerator"/> a new deck opens on, so it always passes
        /// L1–L5 and the test asserts the SERVER'S verdict, not the generator's.</summary>
        private static List<DeckCardLine> LegalLines(CardClass primary, CardClass secondary)
        {
            var deck = DefaultDeckGenerator.Generate(
                primary, secondary, Db, new DeterministicRandom(0xD00DUL), "test-deck");
            return deck.Entries.Select(e => new DeckCardLine(e.CardId, e.Count)).ToList();
        }

        private static int FirstCollectibleOfClass(CardClass cls)
            => Db.AllCollectible().Where(d => d.Class == cls).OrderBy(d => d.Id).First().Id;

        // ── Socket harness (mirrors TokenlessIdentityTests) ───────────────────────────

        private static async Task WithServerAsync(Func<Uri, Task> body)
        {
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-deck-" + Guid.NewGuid().ToString("N"));
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
                // Training capture copies a completed game's .jsonl on a background task that
                // StopAsync does not await, so the file can still be open here. A temp dir we
                // could not clean is not a test failure -- the same guard the bot-fill
                // fixtures already carry. Identified 2026-09-02: this is the
                // ClientDeckSubmissionTests flake, an IOException out of Directory.Delete.
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
        }

        private static async Task SubmitAsync(ClientWebSocket ws, string token, CardClass primary, CardClass secondary)
        {
            await SendAsync(ws, ProtocolCodec.Encode(
                new HelloMsg(token, ProtocolContract.Version, "test", "deck-tests"), ProtocolCodec.PreMatch, 0));
            await SendAsync(ws, ProtocolCodec.Encode(
                new SubmitDeckMsg(primary, secondary, LegalLines(primary, secondary)), ProtocolCodec.PreMatch, 1));
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
            => (await ExpectRawAsync<T>(ws)).Message;

        /// <summary>The next <typeparamref name="T"/> and the JSON it arrived as, so a test can tell a
        /// field the server sent from one the decoder defaulted.</summary>
        private static async Task<(T Message, string Json)> ExpectRawAsync<T>(ClientWebSocket ws) where T : NetMessage
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

                byte[] frame = new ReadOnlySpan<byte>(buffer, 0, total).ToArray();
                var res = ProtocolCodec.Decode(frame);
                if (!res.Ok) throw new InvalidOperationException($"Server sent undecodable frame: {res.Error} {res.Detail}");
                if (res.Message is T t) return (t, Encoding.UTF8.GetString(frame));
            }
        }
    }
}
