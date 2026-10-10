using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>POST /packs/open</c> — the verb the ledger had been missing (spec
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, slice 0; closes the blocking half of
    /// #346).
    ///
    /// <para><b>What this fixture is really guarding is the instance id.</b>
    /// <see cref="BoosterPackGrantService"/> was already exactly-once, transactional and resumable
    /// before this route existed — every property it has is anchored on a pack instance id that must
    /// be minted server-side. So the tests that matter here are not "does it open a pack": they are
    /// that the id is derived from ledger state and never from the request, that a second call
    /// derives the SAME id (and therefore spends nothing), and that a body carrying an instance id
    /// cannot make the server honour it. A refactor that re-adds that field "for convenience" is the
    /// failure this file exists to catch, and the ledger would not complain about it.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PackOpenEndpointTests
    {
        private const string Subject = "supabase-subject-packs";
        private const string Token = "a.jwt.token";

        private static string StarterId => OnboardingGrant.StarterPack().Id;

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

        /// <summary>A pool wide enough that every rarity a starter pack can roll has somewhere to land.
        /// A slot whose rarity has an empty pool is a different failure, and not this one.</summary>
        private static ICardDatabase Cards()
        {
            var cards = new List<CardDefinition>();
            for (int i = 0; i < 12; i++) cards.Add(Card($"common_{i:000}", Rarity.COMMON));
            for (int i = 0; i < 8; i++) cards.Add(Card($"rare_{i:000}", Rarity.RARE));
            for (int i = 0; i < 5; i++) cards.Add(Card($"epic_{i:000}", Rarity.EPIC));
            for (int i = 0; i < 4; i++) cards.Add(Card($"legend_{i:000}", Rarity.LEGENDARY));
            return new StubCardDatabase(cards);
        }

        private static CardDefinition Card(string handle, Rarity rarity)
            => new CardDefinition(
                id: FixtureCard.Id(handle), handle: handle, name: handle, type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art", setId: SetId.EMBERFALL,
                collectible: true, tribe: null, factionId: "");

        private static BoosterPackGrantService Opener(IEntitlementLedger ledger, ICardDatabase cards)
            => new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE),
                cards, new FixedTime());

        /// <summary>An account resolved exactly as the endpoint will resolve it, holding
        /// <paramref name="packs"/> unopened starter packs.</summary>
        private static async Task<(InMemoryAccountStore Store, Account Account)> AccountWithPacks(
            IEntitlementLedger ledger, int packs)
        {
            var store = new InMemoryAccountStore(new FixedTime());
            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");

            if (packs > 0)
                await ledger.GrantAsync(new EntitlementGrant(
                    account.PlayerId, StarterId, packs, "test", "txn-grant-packs", Now));

            return (store, account);
        }

        // ── Refusals ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task No_bearer_is_unauthenticated()
        {
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                bearerToken: null, packId: StarterId, verifier: Accepts(),
                accountStore: new InMemoryAccountStore(new FixedTime()), ledger: ledger,
                packs: Opener(ledger, cards), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Unauthenticated));
            Assert.That(result.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task A_token_supabase_refuses_is_unauthenticated()
        {
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, _) = await AccountWithPacks(ledger, 5);

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Rejects(), store, ledger, Opener(ledger, cards), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Unauthenticated));
        }

        [Test]
        public async Task A_box_with_no_pack_line_is_unavailable_not_a_failed_open()
        {
            // The seed secret is unset, so ServerApp built no grant service. 503 — this box cannot
            // open a pack at all, which is a deployment fact rather than anything the player did.
            var ledger = new InMemoryEntitlementLedger();
            (var store, _) = await AccountWithPacks(ledger, 5);

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, packs: null, onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Unavailable));
            Assert.That(result.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task An_unknown_pack_line_is_refused_rather_than_reserved_against()
        {
            // The validation the spec asks for: packId is checked against the known lines, never
            // trusted. Without it a request naming any ledger item at all — "gold", "card:1042" —
            // would reach a reservation that debits it.
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, 5);
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 500, "test", "txn-gold", Now));

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, EconomyItems.Gold, Accepts(), store, ledger, Opener(ledger, cards),
                onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.UnknownPack));
            Assert.That(result.HttpStatus, Is.EqualTo(400));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, EconomyItems.Gold), Is.EqualTo(500),
                "an unknown pack line must not have debited anything");
        }

        [Test]
        public async Task A_player_holding_no_packs_gets_none_owned_and_nothing_is_granted()
        {
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, packs: 0);

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, Opener(ledger, cards), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.NoneOwned));
            Assert.That(result.HttpStatus, Is.EqualTo(409));
            Assert.That(result.Awards, Is.Empty);

            IReadOnlyList<EntitlementGrant> history = await ledger.HistoryAsync(account.PlayerId);
            Assert.That(history.Any(g => g.Item.StartsWith(BoosterPackGrantService.CardItemPrefix,
                                                           StringComparison.Ordinal)),
                Is.False, "a refused open must not have granted a card");
        }

        // ── The open ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task An_owned_pack_opens_and_spends_exactly_one()
        {
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, 5);

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, Opener(ledger, cards), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Opened));
            Assert.That(result.HttpStatus, Is.EqualTo(200));
            Assert.That(result.Awards.Count, Is.EqualTo(OnboardingGrant.StarterPackCardCount),
                "the SERVER decides the pack shape; a starter pack is 8 cards, not the retail 5");
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId), Is.EqualTo(4));
        }

        [Test]
        public async Task One_batch_call_opens_every_requested_owned_pack()
        {
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, 5);

            PackOpenBatchEndpointResult result = await PackOpenEndpoint.OpenManyAsync(
                Token, StarterId, 5, Accepts(), store, ledger, Opener(ledger, cards), onboarding: null);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Opened));
            Assert.That(result.Opens, Has.Count.EqualTo(5));
            Assert.That(result.Opens.Select(open => open.PackInstanceId).Distinct().Count(), Is.EqualTo(5));
            Assert.That(result.Opens.All(open => open.Awards.Count == OnboardingGrant.StarterPackCardCount),
                Is.True);
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId), Is.Zero);
        }

        [Test]
        public async Task The_instance_id_is_derived_from_the_ledger_and_advances_with_each_open()
        {
            // The minting rule (Sam, 2026-09-03): open:{playerId}:{packId}:{n}, where n is how many
            // completion markers this line already carries. Pinned because the whole exactly-once
            // property of BoosterPackGrantService hangs off this string being stable across a retry
            // and different across two genuine opens.
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, 5);
            BoosterPackGrantService opener = Opener(ledger, cards);

            PackOpenEndpointResult first = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, opener, onboarding: null);
            PackOpenEndpointResult second = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, opener, onboarding: null);

            string expectedFirst =
                PackOpenEndpoint.InstancePrefix + account.PlayerId + ":" + StarterId + ":0";
            string expectedSecond =
                PackOpenEndpoint.InstancePrefix + account.PlayerId + ":" + StarterId + ":1";

            Assert.That(first.PackInstanceId, Is.EqualTo(expectedFirst));
            Assert.That(second.PackInstanceId, Is.EqualTo(expectedSecond));
            Assert.That(second.Status, Is.EqualTo(PackOpenEndpointStatus.Opened),
                "a second genuine open is a NEW pack, not a replay");
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId), Is.EqualTo(3));
        }

        [Test]
        public async Task Five_granted_packs_open_five_times_and_then_run_out()
        {
            // The gift, end to end. The sixth tap is NoneOwned rather than an exception or a free
            // pack, which is what makes the balance self-clearing: nothing puts a starter pack back.
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, OnboardingGrant.StarterPackCount);
            BoosterPackGrantService opener = Opener(ledger, cards);

            var opened = new List<string>();
            for (int i = 0; i < OnboardingGrant.StarterPackCount; i++)
            {
                PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                    Token, StarterId, Accepts(), store, ledger, opener, onboarding: null);
                Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Opened));
                opened.Add(result.PackInstanceId);
            }

            Assert.That(opened.Distinct().Count(), Is.EqualTo(OnboardingGrant.StarterPackCount),
                "five opens must be five instance ids, or four of them were replays");

            PackOpenEndpointResult sixth = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, opener, onboarding: null);
            Assert.That(sixth.Status, Is.EqualTo(PackOpenEndpointStatus.NoneOwned));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId), Is.EqualTo(0));
        }

        [Test]
        public async Task A_replayed_open_returns_the_same_cards_and_spends_nothing()
        {
            // The lost-response case the derived id is built for: the client re-sends before the
            // completion marker moved n along. Same id, same cards, one pack gone rather than two.
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            (var store, Account account) = await AccountWithPacks(ledger, 5);
            BoosterPackGrantService opener = Opener(ledger, cards);

            PackOpenEndpointResult first = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, opener, onboarding: null);

            // Replay the SERVICE under the id the endpoint minted — which is exactly what a second
            // request would do if it read n before the first open's marker landed.
            Account resolved = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");
            PackOpenOutcome replay = await opener.OpenAsync(
                resolved, OnboardingGrant.StarterPack(), first.PackInstanceId);

            Assert.That(replay.Status, Is.EqualTo(PackOpenStatus.AlreadyOpened));
            Assert.That(replay.Awards.Select(a => a.CardId),
                Is.EqualTo(first.Awards.Select(a => a.CardId)));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId), Is.EqualTo(4),
                "a replay must not have spent a second pack");
        }

        [Test]
        public async Task The_endpoint_fires_the_onboarding_grant_so_the_gift_can_be_the_first_call()
        {
            // A player taken straight from character creation to the gift has never hit /collection.
            // Without this they arrive at a screen that is about to hand them five packs they do not
            // yet own, and the first tap is NoneOwned.
            var ledger = new InMemoryEntitlementLedger();
            var cards = Cards();
            var store = new InMemoryAccountStore(new FixedTime());
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());

            PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, Accepts(), store, ledger, Opener(ledger, cards), onboarding);

            Assert.That(result.Status, Is.EqualTo(PackOpenEndpointStatus.Opened));

            Account account = await store.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");
            Assert.That(await ledger.BalanceAsync(account.PlayerId, StarterId),
                Is.EqualTo(OnboardingGrant.StarterPackCount - 1));
        }

        // ── The correction this route was written under ─────────────────────────────────

        [Test]
        public void The_endpoint_takes_no_pack_instance_id_from_anywhere()
        {
            // CORRECTED 2026-09-02: an earlier draft of the spec put packInstanceId in the request
            // body and told the implementer to bound its length and charset — which would have
            // satisfied a security review while leaving the actual hole open. A client that chooses
            // the instance id can replay one pack into unbounded opens by varying it, or collide with
            // another player's.
            //
            // This asserts the shape rather than the behaviour on purpose: the field cannot be
            // honoured if there is nowhere for it to bind, and a future refactor adding it "for
            // convenience" fails here rather than in production.
            MethodInfo open = typeof(PackOpenEndpoint).GetMethod(nameof(PackOpenEndpoint.OpenAsync))!;
            string[] names = open.GetParameters().Select(p => p.Name!).ToArray();

            Assert.That(names.Any(n => n.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.False,
                "POST /packs/open must never accept a pack instance id: it is the idempotency anchor " +
                "and BoosterPackGrantService.OpenAsync requires it to be minted server-side. " +
                "Parameters were: " + string.Join(", ", names));
        }
    }
}
