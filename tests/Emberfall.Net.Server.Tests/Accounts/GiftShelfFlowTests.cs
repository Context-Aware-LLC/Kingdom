using System;
using System.Collections.Generic;
using System.Linq;
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
                cards.Add(Card($"common_{i:000}", Rarity.COMMON, SetId.EMBERFALL));
            for (int i = 0; i < 6; i++)
                cards.Add(Card($"rare_{i:000}", Rarity.RARE, SetId.EMBERFALL));
            for (int i = 0; i < 3; i++)
                cards.Add(Card($"epic_{i:000}", Rarity.EPIC, SetId.EMBERFALL));
            cards.Add(Card("legendary_000", Rarity.LEGENDARY, SetId.EMBERFALL));
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
            for (int i = 0; i < OnboardingGrant.StarterPackCount; i++)
            {
                PackOpenEndpointResult opened = await PackOpenEndpoint.OpenAsync(
                    Token, StarterId, new StubVerifier(), store, ledger, opener, onboarding);
                Assert.That(opened.Status, Is.EqualTo(PackOpenEndpointStatus.Opened),
                            $"open {i + 1} of {OnboardingGrant.StarterPackCount}");
                instances.Add(opened.PackInstanceId);

                CollectionResult read = await CollectionEndpoint.ReadAsync(
                    Token, new StubVerifier(), store, ledger, economy: null, onboarding: onboarding);
                seen.Add(ShelfCount(read));
            }

            Assert.That(instances.Distinct().Count(), Is.EqualTo(OnboardingGrant.StarterPackCount),
                        "five taps must be five pack instances, or some were replays");
            Assert.That(seen, Is.EqualTo(new List<long> { 4, 3, 2, 1, 0 }),
                        "the shelf must count down one per open");
        }
    }
}
