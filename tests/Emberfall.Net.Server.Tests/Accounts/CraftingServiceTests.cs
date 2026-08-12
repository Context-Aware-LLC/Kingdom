using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// Render (GH#157) and Reduce (GH#158) against the authoritative ledger. The decision half lives
    /// in <c>CraftingTests</c>; what is pinned here is that shards and cards move exactly once, that
    /// a refusal writes nothing at all, and that a retry of either direction is inert.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CraftingServiceTests
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000);

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => At.ToUnixTimeMilliseconds();
        }

        private const string CommonId = "test_common";
        private const string LegendaryId = "test_legendary";
        private const string TokenId = "test_token";

        private static Account AuthenticatedAccount(string playerId = "player-1")
            => new Account(playerId, "google", "subject-hash", At);

        private static CardDefinition Card(string id, Rarity rarity, bool collectible = true) => new CardDefinition(
            id: id, name: id, type: CardType.MINION, cost: 1,
            attack: 1, health: 1, durability: null,
            @class: CardClass.NEUTRAL, rarity: rarity,
            keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
            flavorText: "", artId: id, setId: SetId.EMBERFALL,
            collectible: collectible, tribe: null, factionId: "");

        private static ICardDatabase Cards() => new StubCardDatabase(new[]
        {
            Card(CommonId, Rarity.COMMON),
            Card(LegendaryId, Rarity.LEGENDARY),
            Card(TokenId, Rarity.COMMON, collectible: false),
        });

        private sealed class StubCardDatabase : ICardDatabase
        {
            private readonly Dictionary<string, CardDefinition> _byId =
                new Dictionary<string, CardDefinition>(StringComparer.Ordinal);

            public StubCardDatabase(IEnumerable<CardDefinition> defs)
            {
                foreach (CardDefinition d in defs) _byId[d.Id] = d;
            }

            public bool TryGet(string cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(string cardId) => _byId[cardId];

            public IReadOnlyCollection<CardDefinition> AllCollectible()
            {
                var rows = new List<CardDefinition>();
                foreach (CardDefinition d in _byId.Values)
                    if (d.PoolEligible) rows.Add(d);
                return rows;
            }
        }

        private static CraftingService Service(IEntitlementLedger ledger)
            => new CraftingService(ledger, Cards(), new FixedTime());

        private static string CardItem(string cardId) => BoosterPackGrantService.CardItemPrefix + cardId;

        private static async Task GiveShardsAsync(IEntitlementLedger ledger, string playerId, long shards)
            => await ledger.GrantAsync(new EntitlementGrant(
                playerId, ShardValues.LedgerItem, shards, "test_seed", "seed-shards-" + playerId, At));

        private static async Task GiveCardAsync(IEntitlementLedger ledger, string playerId, string cardId, int copies)
            => await ledger.GrantAsync(new EntitlementGrant(
                playerId, CardItem(cardId), copies, "test_seed", "seed-card-" + cardId + "-" + playerId, At));

        // ── Render ───────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Render_Spends_The_Cost_And_Mints_One_Copy()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Common);

            RenderOutcome result = await Service(ledger).RenderAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(RenderStatus.Rendered));
                Assert.That(result.Craft.Shards, Is.EqualTo(0));
                Assert.That(result.Craft.CopiesOwned, Is.EqualTo(1));
                Assert.That(result.Craft.ShardDelta, Is.EqualTo(-ShardCosts.Common));
            });
            Assert.That(await ledger.BalanceAsync(account.PlayerId, CardItem(CommonId)), Is.EqualTo(1));
        }

        [Test]
        public async Task Render_One_Shard_Short_Writes_Nothing()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Legendary - 1);

            RenderOutcome result = await Service(ledger).RenderAsync(account, LegendaryId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(RenderStatus.InsufficientShards));
                Assert.That(result.Craft.Shards, Is.EqualTo(ShardCosts.Legendary - 1));
                Assert.That(result.Craft.CopiesOwned, Is.EqualTo(0));
                Assert.That(result.Craft.ShardDelta, Is.EqualTo(0));
            });
            Assert.That((await ledger.HistoryAsync(account.PlayerId)).Count, Is.EqualTo(1)); // the seed grant only
        }

        [Test]
        public async Task Render_At_The_Copy_Cap_Is_Refused_With_The_Shards_Untouched()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, 10_000);
            await GiveCardAsync(ledger, account.PlayerId, LegendaryId, 1);

            RenderOutcome result = await Service(ledger).RenderAsync(account, LegendaryId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(RenderStatus.AtCopyCap));
                Assert.That(result.Craft.Shards, Is.EqualTo(10_000));
                Assert.That(result.Craft.CopiesOwned, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Render_Of_A_Non_Collectible_Row_Is_Refused()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, 10_000);

            RenderOutcome result = await Service(ledger).RenderAsync(account, TokenId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(RenderStatus.NotCraftable));
                Assert.That(result.Craft.Shards, Is.EqualTo(10_000));
            });
        }

        [Test]
        public async Task Render_Of_An_Unknown_Card_Id_Is_Refused()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, 10_000);

            RenderOutcome result = await Service(ledger).RenderAsync(account, "no_such_card", "craft-1");

            Assert.That(result.Status, Is.EqualTo(RenderStatus.NotCraftable));
        }

        [Test]
        public async Task A_Double_Submitted_Render_Mints_One_Card_And_Charges_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Common * 2);
            CraftingService crafting = Service(ledger);

            RenderOutcome first = await crafting.RenderAsync(account, CommonId, "craft-1");
            RenderOutcome replay = await crafting.RenderAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(RenderStatus.Rendered));
                Assert.That(replay.Status, Is.EqualTo(RenderStatus.Replayed));
                Assert.That(replay.Craft.ShardDelta, Is.EqualTo(0));
                Assert.That(replay.Craft.Shards, Is.EqualTo(ShardCosts.Common));
                Assert.That(replay.Craft.CopiesOwned, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The reason the guards are skipped on a replay: the second copy of a common takes the player
        /// TO the cap, so re-running the cap check against the world the first call produced would
        /// report AtCopyCap for a craft that succeeded.
        /// </summary>
        [Test]
        public async Task A_Replayed_Render_That_Reached_The_Cap_Still_Reports_Replayed()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Common * 4);
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 1);
            CraftingService crafting = Service(ledger);

            RenderOutcome first = await crafting.RenderAsync(account, CommonId, "craft-1");
            RenderOutcome replay = await crafting.RenderAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(RenderStatus.Rendered));
                Assert.That(first.Craft.CopiesOwned, Is.EqualTo(2));
                Assert.That(replay.Status, Is.EqualTo(RenderStatus.Replayed));
                Assert.That(replay.Craft.CopiesOwned, Is.EqualTo(2));
            });
        }

        /// <summary>The other half of the same reason: a replay must not spend the shards again.</summary>
        [Test]
        public async Task A_Replayed_Render_That_Emptied_The_Wallet_Still_Reports_Replayed()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Common);
            CraftingService crafting = Service(ledger);

            await crafting.RenderAsync(account, CommonId, "craft-1");
            RenderOutcome replay = await crafting.RenderAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(replay.Status, Is.EqualTo(RenderStatus.Replayed));
                Assert.That(replay.Craft.Shards, Is.EqualTo(0));
                Assert.That(replay.Craft.CopiesOwned, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task A_Craft_Transaction_Id_Reused_For_A_Different_Card_Is_A_Conflict()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, 10_000);
            CraftingService crafting = Service(ledger);

            await crafting.RenderAsync(account, CommonId, "craft-1");

            Assert.That(async () => await crafting.RenderAsync(account, LegendaryId, "craft-1"),
                        Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task One_Craft_Id_Cannot_Be_Spent_In_Both_Directions()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, 10_000);
            CraftingService crafting = Service(ledger);

            await crafting.RenderAsync(account, CommonId, "craft-1");

            Assert.That(async () => await crafting.ReduceAsync(account, CommonId, "craft-1"),
                        Throws.InstanceOf<InvalidOperationException>());
        }

        // ── Reduce ───────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Reduce_Destroys_One_Copy_And_Pays_The_Locked_Value()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, LegendaryId, 1);

            ReduceOutcome result = await Service(ledger).ReduceAsync(account, LegendaryId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ReduceStatus.Reduced));
                Assert.That(result.Craft.CopiesOwned, Is.EqualTo(0));
                Assert.That(result.Craft.Shards, Is.EqualTo(ShardValues.Legendary));
                Assert.That(result.Craft.ShardDelta, Is.EqualTo(ShardValues.Legendary));
            });
        }

        [Test]
        public async Task Reduce_Of_One_Of_Two_Copies_Leaves_The_Other()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 2);

            ReduceOutcome result = await Service(ledger).ReduceAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ReduceStatus.Reduced));
                Assert.That(result.Craft.CopiesOwned, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Reduce_Of_A_Card_The_Player_Does_Not_Own_Writes_Nothing()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();

            ReduceOutcome result = await Service(ledger).ReduceAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ReduceStatus.OwnsNone));
                Assert.That(result.Craft.Shards, Is.EqualTo(0));
            });
            Assert.That((await ledger.HistoryAsync(account.PlayerId)).Count, Is.EqualTo(0));
        }

        [Test]
        public async Task A_Double_Submitted_Reduce_Destroys_One_Copy_And_Pays_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 2);
            CraftingService crafting = Service(ledger);

            ReduceOutcome first = await crafting.ReduceAsync(account, CommonId, "craft-1");
            ReduceOutcome replay = await crafting.ReduceAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(ReduceStatus.Reduced));
                Assert.That(replay.Status, Is.EqualTo(ReduceStatus.Replayed));
                Assert.That(replay.Craft.CopiesOwned, Is.EqualTo(1));
                Assert.That(replay.Craft.Shards, Is.EqualTo(ShardValues.Common));
                Assert.That(replay.Craft.ShardDelta, Is.EqualTo(0));
            });
        }

        /// <summary>
        /// The Reduce mirror of the cap case: the last copy is the one whose replay would otherwise
        /// report OwnsNone for a Reduce that in fact happened.
        /// </summary>
        [Test]
        public async Task A_Replayed_Reduce_Of_The_Last_Copy_Still_Reports_Replayed()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 1);
            CraftingService crafting = Service(ledger);

            await crafting.ReduceAsync(account, CommonId, "craft-1");
            ReduceOutcome replay = await crafting.ReduceAsync(account, CommonId, "craft-1");

            Assert.Multiple(() =>
            {
                Assert.That(replay.Status, Is.EqualTo(ReduceStatus.Replayed));
                Assert.That(replay.Craft.CopiesOwned, Is.EqualTo(0));
                Assert.That(replay.Craft.Shards, Is.EqualTo(ShardValues.Common));
            });
        }

        [Test]
        public async Task Reduce_Of_A_Non_Collectible_Row_Is_Refused()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, TokenId, 1);

            ReduceOutcome result = await Service(ledger).ReduceAsync(account, TokenId, "craft-1");

            Assert.That(result.Status, Is.EqualTo(ReduceStatus.NotReducible));
        }

        // ── Round trip and the seams the screen reads ────────────────────────────────────────

        [Test]
        public async Task Reduce_Then_Render_Loses_Shards_Because_The_Cost_Exceeds_The_Payout()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 1);
            CraftingService crafting = Service(ledger);

            ReduceOutcome reduced = await crafting.ReduceAsync(account, CommonId, "craft-1");
            RenderOutcome rendered = await crafting.RenderAsync(account, CommonId, "craft-2");

            Assert.Multiple(() =>
            {
                Assert.That(reduced.Craft.Shards, Is.EqualTo(ShardValues.Common));
                // The sink: the payout does not buy the card back.
                Assert.That(rendered.Status, Is.EqualTo(RenderStatus.InsufficientShards));
                Assert.That(ShardCosts.Common, Is.GreaterThan(ShardValues.Common));
            });
        }

        [Test]
        public async Task Offers_Report_The_Same_Verdicts_The_Mutating_Calls_Enforce()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await GiveShardsAsync(ledger, account.PlayerId, ShardCosts.Common);
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 1);

            (RenderOffer render, ReduceOffer reduce) = await Service(ledger).OffersAsync(account, CommonId);

            Assert.Multiple(() =>
            {
                Assert.That(render.Offerable, Is.True);
                Assert.That(render.Cost, Is.EqualTo(ShardCosts.Common));
                Assert.That(render.CopiesOwned, Is.EqualTo(1));
                Assert.That(reduce.Offerable, Is.True);
                Assert.That(reduce.Payout, Is.EqualTo(ShardValues.Common));
                Assert.That(reduce.CopiesAfter, Is.EqualTo(0));
            });
        }

        [Test]
        public async Task Shard_Balance_Projects_Both_Faucets_Onto_One_Number()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            // The pack-open dupe faucet writes the same ledger item a Reduce does.
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, ShardValues.LedgerItem, ShardValues.Epic,
                "pack_open:pack.emberfall:" + CommonId, "packopen:instance-1:0", At));
            await GiveCardAsync(ledger, account.PlayerId, CommonId, 1);
            CraftingService crafting = Service(ledger);

            await crafting.ReduceAsync(account, CommonId, "craft-1");

            Assert.That(await crafting.ShardBalanceAsync(account),
                        Is.EqualTo(ShardValues.Epic + ShardValues.Common));
        }

        [Test]
        public void A_Device_Token_Account_Cannot_Craft()
        {
            var ledger = new InMemoryEntitlementLedger();
            var device = new Account("player-1", "device_token", "subject-hash", At);
            CraftingService crafting = Service(ledger);

            Assert.Multiple(() =>
            {
                Assert.That(async () => await crafting.RenderAsync(device, CommonId, "craft-1"),
                            Throws.InstanceOf<InvalidOperationException>());
                Assert.That(async () => await crafting.ReduceAsync(device, CommonId, "craft-2"),
                            Throws.InstanceOf<InvalidOperationException>());
            });
        }

        [Test]
        public void A_Craft_Without_A_Server_Minted_Transaction_Id_Is_Rejected()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            CraftingService crafting = Service(ledger);

            Assert.Multiple(() =>
            {
                Assert.That(async () => await crafting.RenderAsync(account, CommonId, ""),
                            Throws.InstanceOf<ArgumentException>());
                Assert.That(async () => await crafting.ReduceAsync(account, CommonId, "  "),
                            Throws.InstanceOf<ArgumentException>());
            });
        }
    }
}
