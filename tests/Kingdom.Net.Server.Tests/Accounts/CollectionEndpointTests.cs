using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>GET /collection</c> — the read-down that makes a reinstall recoverable (spec
    /// <c>docs/dev-docs/specs/card-ownership.md</c>, slice 3).
    ///
    /// <para><b>The three answers this endpoint can give are the whole test surface</b>, because the
    /// client acts on them irreversibly: <c>known</c> is written over the device's ownership record,
    /// while <c>unconstrained</c> and <c>unavailable</c> must leave it alone. Serve "unavailable" as
    /// an empty "known" and every deck on that device becomes a draft; serve a real empty collection
    /// as "unavailable" and ownership is unenforceable. Neither mistake produces an error anywhere.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CollectionEndpointTests
    {
        private const string Subject = "supabase-subject-0001";
        private const string Token = "a.jwt.token";

        private static DateTimeOffset Now =>
            DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs);

        // ── Support ─────────────────────────────────────────────────────────────────────

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult _result;
            public StubVerifier(TokenVerificationResult result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(_result);
        }

        private static IIdentityTokenVerifier Accepts()
            => new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, "player@example.com", EmailVerified: true)));

        private static IIdentityTokenVerifier Rejects()
            => new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));

        /// <summary>A ledger that answers every read by throwing — the "database blip" case.</summary>
        private sealed class FaultingLedger : IEntitlementLedger
        {
            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
            public Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
            public Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
            public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
            public Task<EntitlementGrant?> FindAsync(string externalTxnId, CancellationToken ct = default)
                => throw new InvalidOperationException("ledger is down");
        }

        private sealed class StubCardDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new Dictionary<int, CardDefinition>();
            private readonly List<CardDefinition> _collectible = new List<CardDefinition>();

            public StubCardDatabase(IEnumerable<CardDefinition> defs)
            {
                foreach (var d in defs)
                {
                    _byId[d.Id] = d;
                    if (d.PoolEligible) _collectible.Add(d);
                }
            }

            public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(int cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible() => _collectible;
        }

        private static ICardDatabase Cards()
        {
            var cards = new List<CardDefinition>();
            for (int i = 0; i < 4; i++) cards.Add(Card($"common_{i:000}", Rarity.COMMON));
            cards.Add(Card("rare_000", Rarity.RARE));
            return new StubCardDatabase(cards);
        }

        private static CardDefinition Card(string handle, Rarity rarity)
            => new CardDefinition(
                id: FixtureCard.Id(handle), handle: handle, name: handle, type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art", setId: SetId.PENDINGHAM,
                collectible: true, tribe: null, factionId: "");

        private static string CardItem(int cardId) => BoosterPackGrantService.CardItemPrefix + cardId;

        // ── Refusals ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task No_bearer_is_unauthenticated_and_never_touches_the_store()
        {
            CollectionResult result = await CollectionEndpoint.ReadAsync(
                bearerToken: null,
                verifier: Accepts(),
                accountStore: new InMemoryAccountStore(new FixedTime()),
                ledger: new InMemoryEntitlementLedger(),
                economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unauthenticated));
            Assert.That(result.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task A_token_supabase_refuses_is_unauthenticated()
        {
            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Rejects(), new InMemoryAccountStore(new FixedTime()),
                new InMemoryEntitlementLedger(), economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unauthenticated));
        }

        [Test]
        public async Task A_box_with_no_verifier_serves_nobody_rather_than_everybody()
        {
            // The dangerous default. "No authentication configured" must never mean "let them read",
            // because what is behind this route is somebody else's collection.
            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, verifier: null, accountStore: new InMemoryAccountStore(new FixedTime()),
                ledger: new InMemoryEntitlementLedger(), economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unavailable));
            Assert.That(result.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task A_box_with_no_ledger_is_unavailable_not_an_empty_collection()
        {
            // 503, never a 200 with no cards. A dev box without an economy is a supported
            // configuration, and the client must leave whatever it already had alone.
            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), new InMemoryAccountStore(new FixedTime()),
                ledger: null, economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unavailable));
            Assert.That(result.Cards, Is.Empty);
        }

        // ── The reads ───────────────────────────────────────────────────────────────────

        [Test]
        public async Task An_account_with_grants_gets_its_collection_and_its_wallet()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");

            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, CardItem(1001), 2, "test", "txn-cards-1", Now));
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 150, "test", "txn-gold-1", Now));

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger,
                new EconomyService(ledger, new FixedTime()), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Known));
            Assert.That(result.HttpStatus, Is.EqualTo(200));
            Assert.That(result.Cards, Is.EqualTo(new[] { new KeyValuePair<int, int>(1001, 2) }));
            Assert.That(result.Wallet.Gold, Is.EqualTo(150));
        }

        [Test]
        public async Task The_same_subject_resolves_to_the_account_the_match_handshake_would_resolve()
        {
            // The account split this endpoint could have caused. MatchGate hashes the Supabase subject
            // through CredentialHash before the store lookup; if this route hashed it any other way,
            // the same human would hold two player_ids — one with their collection, one empty — and
            // nothing would report a fault.
            var store = new InMemoryAccountStore(new FixedTime());
            Account viaHandshake = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, new InMemoryEntitlementLedger(),
                economy: null, onboarding: null);

            Assert.That(result.PlayerId, Is.EqualTo(viaHandshake.PlayerId));
        }

        [Test]
        public async Task Reading_the_collection_stamps_last_seen()
        {
            // The only authenticated route every launch reaches. GET /session stamps as well, but no
            // client calls it, so without this the column stays null for every player for ever.
            var store = new InMemoryAccountStore(new FixedTime());

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, new InMemoryEntitlementLedger(),
                economy: null, onboarding: null);

            Account? account = await store.FindByPlayerIdAsync(result.PlayerId);
            Assert.That(account!.LastSeen,
                Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs)));
        }

        [Test]
        public async Task A_refused_token_stamps_nothing()
        {
            var store = new InMemoryAccountStore(new FixedTime());
            Account a = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));

            await CollectionEndpoint.ReadAsync(
                Token, Rejects(), store, new InMemoryEntitlementLedger(),
                economy: null, onboarding: null);

            Account? account = await store.FindByPlayerIdAsync(a.PlayerId);
            Assert.That(account!.LastSeen, Is.Null);
        }

        [Test]
        public async Task A_superuser_is_unconstrained_and_no_card_list_is_served()
        {
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "sam@example.com");
            await store.SetKindAsync(account.PlayerId, AccountKind.Superuser);

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, new InMemoryEntitlementLedger(),
                economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unconstrained));
            // Empty because there is nothing to enumerate — NOT because they own nothing. The status
            // is what stops the client caching this as an empty collection.
            Assert.That(result.Cards, Is.Empty);
        }

        [Test]
        public async Task Existing_accounts_receive_one_default_back_and_revocation_stays_account_scoped()
        {
            var store = new InMemoryAccountStore(new FixedTime());
            var ledger = new InMemoryEntitlementLedger();
            var account = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            await ledger.GrantAsync(new EntitlementGrant(account.PlayerId, "card:1001", 2, "old_signup", "old-signup", Now));
            var other = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of("another-subject"));
            await CardBackOwnership.EnsureDefaultAsync(ledger, other);
            for (int i = 0; i < 2; ++i)
            {
                var result = await CollectionEndpoint.ReadAsync(Token, Accepts(), store, ledger, null, null);
                Assert.That(result.CardBacks![1], Is.EqualTo(1));
                Assert.That(result.Cards, Is.EqualTo(new[] { new KeyValuePair<int, int>(1001, 2) }));
            }
            Assert.That((await ledger.HistoryAsync(account.PlayerId)).Count, Is.EqualTo(2));
            await ledger.GrantAsync(new EntitlementGrant(account.PlayerId, "cardback:1", -1, "revoke", "revoke-1", Now));
            var revoked = await CollectionEndpoint.ReadAsync(Token, Accepts(), store, ledger, null, null);
            Assert.That(revoked.CardBacks, Is.Empty);
            Assert.That(await ledger.BalanceAsync(other.PlayerId, "cardback:1"), Is.EqualTo(1));
        }

        [Test]
        public void Card_back_grants_reject_unknown_ids_and_empty_event_ids()
        {
            var ledger = new InMemoryEntitlementLedger();
            var account = new Account("p1", "supabase", "hash", Now);
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await CardBackOwnership.GrantAsync(ledger, account, 999, "test", "event"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await CardBackOwnership.GrantAsync(ledger, account, 1, "test", ""));
        }

        // ── The onboarding grant ────────────────────────────────────────────────────────

        [Test]
        public async Task Reading_the_collection_fires_the_onboarding_grant()
        {
            // Load-bearing, not tidy. The grant fires when an account RESOLVES, and until this route
            // existed the only place one resolved was a match join. A reinstalled player signs in and
            // opens their collection before ever queueing — so without this the ledger is legitimately
            // empty at exactly the moment the client asks, and an empty "known" is cached as
            // "owns nothing".
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null,
                onboarding: new OnboardingGrantService(ledger, cards, new FixedTime()));

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Known));
            Assert.That(result.Cards, Is.Not.Empty,
                "a first read must come back with the starter grant, not with an empty collection");
        }

        [Test]
        public async Task A_second_read_grants_nothing_further()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());

            CollectionResult first = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: onboarding);
            CollectionResult second = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: onboarding);

            // Idempotent on signup:{playerId}. Firing on every read AND every match join is only safe
            // because a replay writes nothing.
            Assert.That(second.Cards, Is.EqualTo(first.Cards));
        }

        [Test]
        public async Task A_failing_grant_still_serves_the_collection()
        {
            // Same posture as the handshake: a database blip must not become an empty collection
            // screen. The read is served from whatever the ledger does hold.
            var ledger = new FaultingLedger();
            var store = new InMemoryAccountStore(new FixedTime());

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null,
                onboarding: new OnboardingGrantService(ledger, Cards(), new FixedTime()));

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Known));
            Assert.That(result.CardBacks, Is.Null);
        }

        // ── Unopened packs ──────────────────────────────────────────────────────────────
        //
        // Carried for RESUME, not for browsing (first-run-and-pack-opening.md, slice 2). Until this
        // landed the five starter packs were a ledger balance nothing on the wire reported, so the
        // client could not know they existed and the gift had no way to route — #346, measured on a
        // device as a first deck stuck at 28 of 30.

        [Test]
        public async Task The_starter_packs_the_grant_wrote_come_back_on_the_wire()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null,
                onboarding: new OnboardingGrantService(ledger, Cards(), new FixedTime()));

            string starter = OnboardingGrant.StarterPack().Id;
            Assert.That(result.Packs, Is.EqualTo(new[]
            {
                new KeyValuePair<string, long>(starter, OnboardingGrant.StarterPackCount),
            }));
        }

        [Test]
        public async Task A_spent_pack_line_is_dropped_rather_than_reported_as_zero()
        {
            // The ledger is append-only, so opening every pack leaves a zero row behind rather than
            // deleting the grant. A zero-count shelf entry is a tile that cannot be tapped, and — more
            // to the point — the gift routes on "> 0", so a zero that reached the client as a present
            // key would be one bug away from replaying the congratulations beat for ever.
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");

            string starter = OnboardingGrant.StarterPack().Id;
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, starter, 2, "test", "txn-packs", Now));
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, starter, -2, "test", "txn-opened-both", Now));

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Known));
            Assert.That(result.Packs, Is.Empty);
        }

        [Test]
        public async Task Only_pack_lines_reach_the_shelf()
        {
            // Every balance is filtered through PackCatalog. A card row or a currency arriving as a
            // "pack" would render as an unopenable tile, and POST /packs/open would refuse it.
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");

            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, CardItem(1001), 3, "test", "txn-cards", Now));
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 500, "test", "txn-gold", Now));

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: null);

            Assert.That(result.Packs, Is.Empty);
        }

        [Test]
        public async Task A_superuser_gets_no_card_list_but_does_get_their_packs()
        {
            // Unlike the cards, where there is nothing to enumerate. Sam's own account is the one the
            // gift is developed on, and a superuser who could not open their starter packs would make
            // the whole first-run flow untestable on the only device it is built on.
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");
            await store.SetKindAsync(account.PlayerId, AccountKind.Superuser);

            string starter = OnboardingGrant.StarterPack().Id;
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, starter, 5, "test", "txn-packs", Now));

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unconstrained));
            Assert.That(result.Cards, Is.Empty);
            Assert.That(result.Packs, Is.EqualTo(new[]
            {
                new KeyValuePair<string, long>(starter, 5),
            }));
        }

        [Test]
        public async Task A_pack_read_that_throws_costs_the_packs_and_not_the_answer()
        {
            // Same posture as the wallet: a shelf coming back empty for one launch is a missed gift,
            // while a throw would be a 500 on the screen the player just opened.
            //
            // A SUPERUSER, deliberately, because that is the one path where this catch is the only
            // thing standing between a bad sweep and a failed read. For a standard account
            // LedgerOwnedCounts already ran the same BalancesAsync to build the card list and would
            // have thrown first — the two projections share one ledger call and this endpoint makes
            // it twice. Superusers skip the card projection entirely (AccountOwnedCounts.ForAsync
            // never consults the ledger for one), so the pack sweep is their only one.
            var ledger = new ThrowsOnBalances();
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");
            await store.SetKindAsync(account.PlayerId, AccountKind.Superuser);

            CollectionResult result = await CollectionEndpoint.ReadAsync(
                Token, Accepts(), store, ledger, economy: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(CollectionResultStatus.Unconstrained));
            Assert.That(result.Packs, Is.Empty);
        }

        /// <summary>A ledger whose grants and history work and whose balance sweep does not — the one
        /// fault shape that isolates the pack read.</summary>
        private sealed class ThrowsOnBalances : IEntitlementLedger
        {
            private readonly InMemoryEntitlementLedger _inner = new InMemoryEntitlementLedger();

            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default)
                => _inner.GrantAsync(grant, ct);
            public Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default)
                => _inner.ApplyBatchAsync(batch, ct);
            public Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(string playerId, CancellationToken ct = default)
                => _inner.AcquirePlayerMutationLockAsync(playerId, ct);
            public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default)
                => _inner.BalanceAsync(playerId, item, ct);
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("balance sweep is down");
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default)
                => _inner.HistoryAsync(playerId, ct);
            public Task<EntitlementGrant?> FindAsync(string externalTxnId, CancellationToken ct = default)
                => _inner.FindAsync(externalTxnId, ct);
        }
    }
}
