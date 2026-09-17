using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The write path that makes an unowned card a real thing — <see cref="OnboardingGrantService"/>
    /// binding the pure <c>OnboardingGrant</c> to the append-only ledger (spec:
    /// <c>docs/dev-docs/specs/card-ownership.md</c>, slice 1).
    ///
    /// <para>The interesting cases are the two states that look identical from outside and must not
    /// be confused: <b>never granted</b> and <b>granted then spent</b>. The first must be granted on
    /// sight; the second must never be re-granted, however empty the balance looks. The ledger
    /// distinguishes them because it keeps the grant rows forever — which is the improvement over the
    /// client-file key this replaces, where an empty record and an absent one were one careful
    /// convention away from each other.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class OnboardingGrantServiceTests
    {
        private const string Player = "player-0001";

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        /// <summary>
        /// A stub corpus with collectible commons in both halves of the pairing, plus a
        /// non-collectible row and an off-pairing row that the grant must exclude.
        /// </summary>
        private static ICardDatabase Cards()
        {
            var cards = new List<CardDefinition>();
            for (int i = 0; i < 8; i++)
                cards.Add(Card($"common_{i:000}", Rarity.COMMON, i % 2 == 0 ? SetId.CORE : SetId.EMBERFALL));
            for (int i = 0; i < 4; i++)
                cards.Add(Card($"rare_{i:000}", Rarity.RARE, SetId.EMBERFALL));
            cards.Add(Card("token_000", Rarity.COMMON, SetId.CORE, collectible: false));
            return new StubCardDatabase(cards);
        }

        private static CardDefinition Card(string handle, Rarity rarity, SetId setId, bool collectible = true)
            => new CardDefinition(
                id: FixtureCard.Id(handle), handle: handle, name: handle, type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: rarity,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "art", setId: setId,
                collectible: collectible, tribe: null, factionId: "");

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

        private static OnboardingGrantService Service(IEntitlementLedger ledger, ICardDatabase cards)
            => new OnboardingGrantService(ledger, cards, new FixedTime());

        private static Account Authenticated(string playerId = Player)
            => new Account(playerId, "google", "subject-hash",
                           DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs));

        private static Account Guest(string playerId = "guest-0001")
            => new Account(playerId, "device_token", "device-hash",
                           DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs));

        private static string CardItem(int cardId) => BoosterPackGrantService.CardItemPrefix + cardId;

        // ── the grant lands ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task An_Authenticated_Account_Is_Granted_Every_Common_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            ICardDatabase cards = Cards();
            var account = Authenticated();

            Assert.That(await Service(ledger, cards).EnsureGrantedAsync(account),
                        Is.EqualTo(OnboardingOutcome.Granted));

            IReadOnlyList<int> commons = OnboardingGrant.CardGrant(cards);
            Assert.That(commons, Is.Not.Empty, "the fixture corpus has no grantable commons");
            foreach (int cardId in commons)
                Assert.That(await ledger.BalanceAsync(account.PlayerId, CardItem(cardId)), Is.EqualTo(1),
                            $"card {cardId} should hold exactly one granted copy");
        }

        [Test]
        public async Task Nothing_Outside_The_Commons_Is_Granted()
        {
            var ledger = new InMemoryEntitlementLedger();
            ICardDatabase cards = Cards();
            var account = Authenticated();
            await Service(ledger, cards).EnsureGrantedAsync(account);

            var granted = new HashSet<int>(OnboardingGrant.CardGrant(cards));
            foreach (CardDefinition card in cards.AllCollectible())
                if (!granted.Contains(card.Id))
                    Assert.That(await ledger.BalanceAsync(account.PlayerId, CardItem(card.Id)), Is.Zero,
                                $"{card.Handle} is not part of the commons half and must not be granted");
        }

        /// <summary>
        /// The packs land on the STARTER entitlement, never the retail one. If these shared an item
        /// the grant would silently top up a saleable balance — the collision the sku exists to stop.
        /// </summary>
        [Test]
        public async Task The_Starter_Packs_Are_Granted_As_Their_Own_Entitlement()
        {
            var ledger = new InMemoryEntitlementLedger();
            var account = Authenticated();
            await Service(ledger, Cards()).EnsureGrantedAsync(account);

            Assert.That(await ledger.BalanceAsync(account.PlayerId, OnboardingGrant.StarterPack().Id),
                        Is.EqualTo(OnboardingGrant.StarterPackCount));
            Assert.That(await ledger.BalanceAsync(account.PlayerId,
                            new BoosterPack(OnboardingGrant.LaunchExpansion).Id),
                        Is.Zero, "the grant must not top up the retail pack entitlement");
        }

        [Test]
        public async Task Every_Row_Is_Attributed_To_The_Signup_Source()
        {
            var ledger = new InMemoryEntitlementLedger();
            var account = Authenticated();
            await Service(ledger, Cards()).EnsureGrantedAsync(account);

            IReadOnlyList<EntitlementGrant> history = await ledger.HistoryAsync(account.PlayerId);
            Assert.That(history, Is.Not.Empty);
            int shardRows = 0;
            foreach (EntitlementGrant row in history)
            {
                Assert.That(row.Source, Is.EqualTo(OnboardingGrantService.GrantSource));
                bool starterShards = row.Item == EconomyItems.Shards;
                if (starterShards) shardRows++;
                Assert.That(row.ExternalTxnId,
                            Does.StartWith((starterShards
                                ? OnboardingGrantService.StarterShardsTxnPrefix
                                : OnboardingGrantService.TxnPrefix) + account.PlayerId),
                            "the once-per-account key must be the player's own signup transaction");
            }
            Assert.That(shardRows, Is.EqualTo(1), "the starter credit has its own once-only transaction");
        }

        // ── once, and only once ──────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Second_Call_Grants_Nothing_More()
        {
            var ledger = new InMemoryEntitlementLedger();
            ICardDatabase cards = Cards();
            OnboardingGrantService service = Service(ledger, cards);
            var account = Authenticated();

            await service.EnsureGrantedAsync(account);
            OnboardingOutcome second = await service.EnsureGrantedAsync(account);

            Assert.That(second, Is.EqualTo(OnboardingOutcome.AlreadyGranted));
            int cardId = OnboardingGrant.CardGrant(cards)[0];
            Assert.That(await ledger.BalanceAsync(account.PlayerId, CardItem(cardId)), Is.EqualTo(1),
                        "the grant is once per account, not once per call");
        }

        /// <summary>
        /// Granted-then-spent is a DIFFERENT state from never-granted, and the ledger is what keeps
        /// them apart: the original grant rows survive the spend, so the transaction id still
        /// conflicts even though every balance has fallen to zero.
        /// </summary>
        [Test]
        public async Task An_Account_That_Spent_Everything_Is_Not_Re_Granted()
        {
            var ledger = new InMemoryEntitlementLedger();
            ICardDatabase cards = Cards();
            OnboardingGrantService service = Service(ledger, cards);
            var account = Authenticated();
            await service.EnsureGrantedAsync(account);

            foreach (int cardId in OnboardingGrant.CardGrant(cards))
                await ledger.GrantAsync(new EntitlementGrant(
                    account.PlayerId, CardItem(cardId), -1, "test_spend",
                    $"spend:{account.PlayerId}:{cardId}",
                    DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs)));

            Assert.That(await service.EnsureGrantedAsync(account),
                        Is.EqualTo(OnboardingOutcome.AlreadyGranted));

            int first = OnboardingGrant.CardGrant(cards)[0];
            Assert.That(await ledger.BalanceAsync(account.PlayerId, CardItem(first)), Is.Zero,
                        "an emptied collection stays empty; it is not refilled");
        }

        [Test]
        public async Task Two_Accounts_Are_Granted_Independently()
        {
            var ledger = new InMemoryEntitlementLedger();
            ICardDatabase cards = Cards();
            OnboardingGrantService service = Service(ledger, cards);

            await service.EnsureGrantedAsync(Authenticated("account-a"));

            Assert.That(await service.EnsureGrantedAsync(Authenticated("account-b")),
                        Is.EqualTo(OnboardingOutcome.Granted),
                        "the transaction id is keyed on the player, not global");
            int cardId = OnboardingGrant.CardGrant(cards)[0];
            Assert.That(await ledger.BalanceAsync("account-b", CardItem(cardId)), Is.EqualTo(1));
        }

        // ── guests ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>EconomyAccountPolicy.RequireDurable</c> THROWS for a device-token credential — inventory
        /// may not move before account linking. An unauthenticated player joining a match is ordinary,
        /// not an error, so the service skips them: their ownership stays unknown, which is the
        /// "do not constrain" state they already had.
        /// </summary>
        [Test]
        public async Task A_Guest_Is_Skipped_Rather_Than_Thrown_On()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account guest = Guest();

            Assert.That(await Service(ledger, Cards()).EnsureGrantedAsync(guest),
                        Is.EqualTo(OnboardingOutcome.SkippedGuest));
            Assert.That(await ledger.HistoryAsync(guest.PlayerId), Is.Empty,
                        "a skipped guest must leave no trace in the ledger");
        }
    }
}
