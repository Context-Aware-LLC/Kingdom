using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The gift shelf as the CLIENT drives it: <c>GET /collection</c>, <c>POST /packs/open</c>,
    /// <c>GET /collection</c> again — which is exactly what PackVaultController does on every open.
    ///
    /// <para><b>Why this is not covered by the two endpoint fixtures it spans.</b>
    /// <c>PackOpenEndpointTests</c> proves the balance falls, and reads it off the ledger.
    /// <c>CollectionEndpointTests</c> proves the shelf is projected, from a ledger it wrote by hand.
    /// Neither passes an <see cref="OnboardingGrantService"/>, and production passes one to BOTH
    /// routes — so the one composition a player actually meets is the one nothing exercised. The
    /// symptom that would follow is silent and awful to diagnose: a shelf whose count never moves
    /// while the cards keep arriving, because the grant re-lands what the open just spent.</para>
    ///
    /// <para>Both routes run against ONE ledger and ONE account store, as
    /// <c>ServerApp</c> composes them.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class GiftShelfFlowTests
    {
        private const string Subject = "supabase-subject-gift-shelf";
        private const string Token = "a.jwt.token";

        private static string StarterId => OnboardingGrant.StarterPack().Id;

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity(Subject, "player@example.com", EmailVerified: true)));
        }

        private sealed class StubCardDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new Dictionary<int, CardDefinition>();
            private readonly List<CardDefinition> _collectible = new List<CardDefinition>();

            public StubCardDatabase(IEnumerable<CardDefinition> defs)
            {
                foreach (CardDefinition d in defs)
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
            for (int i = 0; i < 12; i++)
                cards.Add(Card($"common_{i:000}", Rarity.COMMON, SetId.PENDINGHAM));
            for (int i = 0; i < 6; i++)
                cards.Add(Card($"rare_{i:000}", Rarity.RARE, SetId.PENDINGHAM));
            for (int i = 0; i < 3; i++)
                cards.Add(Card($"epic_{i:000}", Rarity.EPIC, SetId.PENDINGHAM));
            cards.Add(Card("legendary_000", Rarity.LEGENDARY, SetId.PENDINGHAM));
            return new StubCardDatabase(cards);
        }

        private static CardDefinition Card(string handle, Rarity rarity, SetId setId)
            => new CardDefinition(
                id: FixtureCard.Id(handle), handle: handle, name: handle, type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art", setId: setId,
                collectible: true, tribe: null, factionId: "");

        /// <summary>The shelf line the vault draws its tiles from — what <c>packs</c> says for the
        /// starter, or 0 when the endpoint sent no row for it at all (which the client reads as an
        /// empty shelf, not as a fault).</summary>
        private static long ShelfCount(CollectionResult result)
        {
            foreach (KeyValuePair<string, long> row in result.Packs)
                if (string.Equals(row.Key, StarterId, StringComparison.Ordinal)) return row.Value;
            return 0;
        }

        [Test]
        public async Task Opening_a_gifted_pack_takes_it_off_the_shelf_the_client_reads_back()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());
            var opener = new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE),
                cards, new FixedTime());

            // 1 — the first read. This is what grants the gift, exactly as it does on a device: the
            //     client has never called anything else.
            CollectionResult first = await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);
            Assert.That(first.Status, Is.EqualTo(CollectionResultStatus.Known));
            Assert.That(ShelfCount(first), Is.EqualTo(OnboardingGrant.StarterPackCount),
                        "the gift must arrive on the first read, or the vault has nothing to draw");

            // 2 — one open, through the route the tile taps.
            PackOpenEndpointResult opened = await PackOpenEndpoint.OpenAsync(
                Token, StarterId, new StubVerifier(), store, ledger, opener, onboarding);
            Assert.That(opened.Status, Is.EqualTo(PackOpenEndpointStatus.Opened));

            // 3 — the re-read the reveal's Done button triggers. THE ASSERTION THIS FILE EXISTS FOR.
            CollectionResult second = await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);
            Assert.That(ShelfCount(second), Is.EqualTo(OnboardingGrant.StarterPackCount - 1),
                        "the shelf must lose the pack that was just opened");
        }

        [Test]
        public async Task The_whole_gift_empties_and_the_shelf_reaches_zero()
        {
            // Five taps, five reads. A shelf that stalls at any point strands the rest of the gift
            // behind a tile the player has already spent.
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());
            var opener = new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE),
                cards, new FixedTime());

            await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);

            var seen = new List<long>();
            var instances = new List<string>();
            var onboarded = new List<bool>();
            for (int i = 0; i < OnboardingGrant.StarterPackCount; i++)
            {
                PackOpenEndpointResult opened = await PackOpenEndpoint.OpenAsync(
                    Token, StarterId, new StubVerifier(), store, ledger, opener, onboarding);
                Assert.That(opened.Status, Is.EqualTo(PackOpenEndpointStatus.Opened),
                            $"open {i + 1} of {OnboardingGrant.StarterPackCount}");
                instances.Add(opened.PackInstanceId);
                // Read before the collection re-read, so this is the OPEN's doing.
                onboarded.Add(await OnboardedAsync(store));

                CollectionResult read = await CollectionEndpoint.ReadAsync(
                    Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);
                seen.Add(ShelfCount(read));
            }

            Assert.That(instances.Distinct().Count(), Is.EqualTo(OnboardingGrant.StarterPackCount),
                        "five taps must be five pack instances, or some were replays");
            Assert.That(seen, Is.EqualTo(new List<long> { 4, 3, 2, 1, 0 }),
                        "the shelf must count down one per open");
            Assert.That(onboarded, Is.EqualTo(new List<bool> { false, false, false, false, true }),
                        "onboarded means the gift was given AND opened — the last open sets it, no earlier one");
        }

        [Test]
        public async Task A_granted_but_unopened_gift_is_not_onboarded()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            var onboarding = new OnboardingGrantService(ledger, Cards(), new FixedTime());

            SessionResult session = await SessionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, onboarding);

            Assert.That(session.Onboarded, Is.False, "granted is half of it; opened is the other half");
        }

        [Test]
        public async Task Opening_the_whole_gift_in_one_batch_onboards()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());
            var opener = new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE),
                cards, new FixedTime());

            PackOpenBatchEndpointResult opened = await PackOpenEndpoint.OpenManyAsync(
                Token, StarterId, OnboardingGrant.StarterPackCount, new StubVerifier(), store, ledger,
                opener, onboarding);
            Assert.That(opened.Opens.Count, Is.EqualTo(OnboardingGrant.StarterPackCount));

            SessionResult session = await SessionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, onboarding);
            Assert.That(session.Onboarded, Is.True);
        }

        [Test]
        public async Task A_shelf_emptied_before_the_flag_was_derived_is_backfilled_on_launch()
        {
            // The accounts that exist today: every pack opened by a server that never set the flag.
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase cards = Cards();
            var onboarding = new OnboardingGrantService(ledger, cards, new FixedTime());
            var opener = new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE),
                cards, new FixedTime());

            await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);
            for (int i = 0; i < OnboardingGrant.StarterPackCount; i++)
                await PackOpenEndpoint.OpenAsync(
                    Token, StarterId, new StubVerifier(), store, ledger, opener, onboarding: null);
            Assert.That(await OnboardedAsync(store), Is.False, "precondition: nothing has set it yet");

            await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);

            Assert.That(await OnboardedAsync(store), Is.True,
                        "GET /collection is the route every launch reaches, so it is where the backfill lands");
        }

        /// <summary>The card pool one release later: one more common, so the grant's indexed rows no
        /// longer match what an older account was given and the signup batch comes back Conflict.</summary>
        private static ICardDatabase CardsAfterAContentChange()
        {
            var cards = new List<CardDefinition>(Cards().AllCollectible())
            {
                Card("common_added_later", Rarity.COMMON, SetId.PENDINGHAM),
            };
            return new StubCardDatabase(cards);
        }

        /// <summary>An account onboarded under the old grant, every starter opened, flag never set —
        /// then served by a build whose grant resolves differently. Precondition-checked, so a test
        /// that passes here is not passing because the grant quietly succeeded.</summary>
        private static async Task<(InMemoryAccountStore store, InMemoryEntitlementLedger ledger,
                                   OnboardingGrantService current)> DriftedAndFullyOpenedAsync()
        {
            var ledger = new InMemoryEntitlementLedger();
            var store = new InMemoryAccountStore(new FixedTime());
            ICardDatabase old = Cards();
            var then = new OnboardingGrantService(ledger, old, new FixedTime());
            var opener = new BoosterPackGrantService(
                ledger, new InMemoryPackPityStore(), new FixedPackSeedSource(0xC0FFEE), old, new FixedTime());

            Account account = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            await then.EnsureGrantedAsync(account);
            for (int i = 0; i < OnboardingGrant.StarterPackCount; i++)
                await PackOpenEndpoint.OpenAsync(
                    Token, StarterId, new StubVerifier(), store, ledger, opener, onboarding: null);
            Assert.That(await OnboardedAsync(store), Is.False, "precondition: nothing has set it yet");

            var current = new OnboardingGrantService(ledger, CardsAfterAContentChange(), new FixedTime());
            Assert.ThrowsAsync<InvalidOperationException>(() => current.EnsureGrantedAsync(account),
                "precondition: the drifted grant must conflict, or this test proves nothing");
            return (store, ledger, current);
        }

        [Test]
        public async Task A_drifted_grant_does_not_block_the_collection_backfill()
        {
            // Every account onboarded before the launch set last changed is in this state: its signup
            // batch Conflicts on every resolve (by design — it keeps what it was given). The flag is
            // derived from the opened starter alone, so the conflict must not stop it being set.
            var (store, ledger, current) = await DriftedAndFullyOpenedAsync();

            await CollectionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, ledger, economy: null, onboarding: current);

            Assert.That(await OnboardedAsync(store), Is.True,
                        "a refused re-grant is not an unopened gift; the backfill must still land");
        }

        [Test]
        public async Task A_drifted_grant_does_not_block_the_session_backfill()
        {
            var (store, _, current) = await DriftedAndFullyOpenedAsync();

            SessionResult session = await SessionEndpoint.ReadAsync(
                Token, new StubVerifier(), store, current);

            Assert.That(session.Onboarded, Is.True,
                        "/session must report the flag the ledger already proves");
        }

        private static async Task<bool> OnboardedAsync(InMemoryAccountStore store)
        {
            Account account = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            return account.Onboarded;
        }
    }
}
