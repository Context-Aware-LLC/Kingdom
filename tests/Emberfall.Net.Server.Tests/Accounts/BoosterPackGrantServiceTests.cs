using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// NOR-109 §5 — the ledger binding. A pack open is one logical transaction over an append-only
    /// ledger, and opening the same pack instance twice must not grant twice.
    ///
    /// <para>The interesting case is not the clean replay (that one is easy); it is the <b>half-written
    /// open</b>. The service writes slot rows first and the consume row last, and projects ownership
    /// while ignoring its own instance's rows, precisely so a crash mid-write re-rolls to a bit-identical
    /// pack and finishes. <see cref="A_Crash_Mid_Grant_Resumes_To_The_Identical_Pack"/> is the fixture
    /// that pins it.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class BoosterPackGrantServiceTests
    {
        private const string Player = "player-0001";
        private const ulong Seed = 0xB005_7E12_3456_789AUL;

        private static readonly BoosterPack Pack = new BoosterPack(SetId.EMBERFALL);

        // ── fixture plumbing ─────────────────────────────────────────────────────────────────

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        /// <summary>A ledger that stops accepting writes after N grants — a crash, in one class.</summary>
        private sealed class CrashingLedger : IEntitlementLedger
        {
            private readonly IEntitlementLedger _inner;
            private int _remaining;

            public CrashingLedger(IEntitlementLedger inner, int allowedGrants)
            {
                _inner = inner;
                _remaining = allowedGrants;
            }

            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default)
            {
                if (_remaining-- <= 0) throw new SimulatedLedgerOutage();
                return _inner.GrantAsync(grant, ct);
            }

            public Task<long> BalanceAsync(string p, string i, CancellationToken ct = default) => _inner.BalanceAsync(p, i, ct);
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string p, CancellationToken ct = default) => _inner.BalancesAsync(p, ct);
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string p, CancellationToken ct = default) => _inner.HistoryAsync(p, ct);
        }

        private sealed class SimulatedLedgerOutage : Exception
        {
            public SimulatedLedgerOutage() : base("simulated ledger outage") { }
        }

        private static ICardDatabase Cards()
        {
            var cards = new List<CardDefinition>();
            foreach (Rarity rarity in new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                for (int i = 0; i < 12; i++)
                    cards.Add(new CardDefinition(
                        id: $"{rarity.ToString().ToLowerInvariant()}_{i:000}",
                        name: $"{rarity} {i}", type: CardType.MINION, cost: 1,
                        attack: 1, health: 1, durability: null,
                        @class: CardClass.NEUTRAL, rarity: rarity,
                        keywords: Array.Empty<Keyword>(), effects: Array.Empty<EffectDef>(),
                        cardText: "", flavorText: "", artId: "art", setId: i % 3 == 0 ? SetId.CORE : SetId.EMBERFALL,
                        collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false));
            return new StubCardDatabase(cards);
        }

        private sealed class StubCardDatabase : ICardDatabase
        {
            private readonly Dictionary<string, CardDefinition> _byId = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
            public StubCardDatabase(IEnumerable<CardDefinition> defs) { foreach (var d in defs) _byId[d.Id] = d; }
            public bool TryGet(string cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(string cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible() => new List<CardDefinition>(_byId.Values);
        }

        private static BoosterPackGrantService Service(IEntitlementLedger ledger, IPackPityStore pity, ICardDatabase cards)
            => new BoosterPackGrantService(ledger, pity, new FixedPackSeedSource(Seed), cards, new FixedTime());

        // ── the happy path ───────────────────────────────────────────────────────────────────

        [Test]
        public async Task An_Open_Writes_One_Row_Per_Slot_Plus_The_Consume_Row()
        {
            var ledger = new InMemoryEntitlementLedger();
            var service = Service(ledger, new InMemoryPackPityStore(), Cards());

            var outcome = await service.OpenAsync(Player, Pack, "inst-1");

            Assert.That(outcome.Status, Is.EqualTo(PackOpenStatus.Opened));
            Assert.That(outcome.Awards, Has.Count.EqualTo(Pack.CardCount));

            var history = await ledger.HistoryAsync(Player);
            TestContext.Out.WriteLine("ledger rows:");
            foreach (var row in history)
                TestContext.Out.WriteLine($"  {row.ExternalTxnId,-24} {row.Item,-24} {row.Qty,5}  {row.Source}");

            Assert.That(history, Has.Count.EqualTo(Pack.CardCount + 1), "One row per slot, plus the consume row.");
            Assert.That(await ledger.BalanceAsync(Player, Pack.Id), Is.EqualTo(-1),
                "Opening a pack consumes the pack entitlement itself.");
        }

        [Test]
        public async Task Cards_And_Shards_Land_As_Ledger_Balances()
        {
            var ledger = new InMemoryEntitlementLedger();
            var service = Service(ledger, new InMemoryPackPityStore(), Cards());

            var outcome = await service.OpenAsync(Player, Pack, "inst-1");

            foreach (var award in outcome.Awards)
            {
                if (award.ConvertedToShards) continue;
                long held = await ledger.BalanceAsync(Player, BoosterPackGrantService.CardItemPrefix + award.CardId);
                Assert.That(held, Is.GreaterThanOrEqualTo(1), $"{award.CardId} was not granted.");
            }

            long shards = await ledger.BalanceAsync(Player, ShardValues.LedgerItem);
            Assert.That(shards, Is.EqualTo(outcome.TotalShards));
        }

        [Test]
        public async Task Pity_Advances_Exactly_Once_Per_Open()
        {
            var ledger = new InMemoryEntitlementLedger();
            var pityStore = new InMemoryPackPityStore();
            var service = Service(ledger, pityStore, Cards());

            for (int i = 1; i <= 5; i++)
            {
                await service.OpenAsync(Player, Pack, "inst-" + i);
                var pity = await pityStore.LoadAsync(Player, Pack.Id);
                Assert.That(pity.PacksOpened, Is.EqualTo(i));
            }
        }

        // ── idempotency (§5) ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task Opening_The_Same_Instance_Twice_Grants_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            var pityStore = new InMemoryPackPityStore();
            var service = Service(ledger, pityStore, Cards());

            var first = await service.OpenAsync(Player, Pack, "inst-1");
            string balancesAfterFirst = await RenderBalances(ledger, Player);
            var pityAfterFirst = await pityStore.LoadAsync(Player, Pack.Id);

            var second = await service.OpenAsync(Player, Pack, "inst-1");
            string balancesAfterSecond = await RenderBalances(ledger, Player);
            var pityAfterSecond = await pityStore.LoadAsync(Player, Pack.Id);

            Assert.That(second.Status, Is.EqualTo(PackOpenStatus.AlreadyOpened));
            Assert.That(balancesAfterSecond, Is.EqualTo(balancesAfterFirst), "A replay changed a balance.");
            Assert.That(pityAfterSecond.PacksOpened, Is.EqualTo(pityAfterFirst.PacksOpened), "A replay moved a pity clock.");

            // The replay is reconstructed from the ledger, so the cards, rarities and shards must match
            // what was actually granted. Only the reveal chrome (which guarantee fired) is lost.
            Assert.That(second.Awards.Count, Is.EqualTo(first.Awards.Count));
            for (int i = 0; i < first.Awards.Count; i++)
            {
                Assert.That(second.Awards[i].CardId, Is.EqualTo(first.Awards[i].CardId), $"slot {i}");
                Assert.That(second.Awards[i].Rarity, Is.EqualTo(first.Awards[i].Rarity), $"slot {i}");
                Assert.That(second.Awards[i].ConvertedToShards, Is.EqualTo(first.Awards[i].ConvertedToShards), $"slot {i}");
                Assert.That(second.Awards[i].Shards, Is.EqualTo(first.Awards[i].Shards), $"slot {i}");
            }
        }

        [Test]
        public async Task Ten_Replays_Of_One_Instance_Still_Grant_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            var service = Service(ledger, new InMemoryPackPityStore(), Cards());

            await service.OpenAsync(Player, Pack, "inst-1");
            string expected = await RenderBalances(ledger, Player);

            for (int i = 0; i < 10; i++) await service.OpenAsync(Player, Pack, "inst-1");

            Assert.That(await RenderBalances(ledger, Player), Is.EqualTo(expected));
            Assert.That(await ledger.HistoryAsync(Player), Has.Count.EqualTo(Pack.CardCount + 1));
        }

        [Test]
        public async Task Distinct_Instances_Each_Grant()
        {
            var ledger = new InMemoryEntitlementLedger();
            var service = Service(ledger, new InMemoryPackPityStore(), Cards());

            await service.OpenAsync(Player, Pack, "inst-1");
            await service.OpenAsync(Player, Pack, "inst-2");

            Assert.That(await ledger.HistoryAsync(Player), Has.Count.EqualTo(2 * (Pack.CardCount + 1)));
            Assert.That(await ledger.BalanceAsync(Player, Pack.Id), Is.EqualTo(-2));
        }

        // ── the crash window ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// A crash partway through the slot writes must resume to the <b>same</b> pack, not a fresh
        /// roll. Ownership is projected while ignoring this instance's own rows, so the second attempt
        /// sees the exact inputs the first did.
        /// </summary>
        [Test]
        public async Task A_Crash_Mid_Grant_Resumes_To_The_Identical_Pack()
        {
            var reference = new InMemoryEntitlementLedger();
            var referenceOutcome = await Service(reference, new InMemoryPackPityStore(), Cards())
                .OpenAsync(Player, Pack, "inst-1");

            for (int allowed = 0; allowed < Pack.CardCount; allowed++)
            {
                var ledger = new InMemoryEntitlementLedger();
                var pityStore = new InMemoryPackPityStore();
                var cards = Cards();

                var crashing = Service(new CrashingLedger(ledger, allowed), pityStore, cards);
                Assert.ThrowsAsync<SimulatedLedgerOutage>(async () => await crashing.OpenAsync(Player, Pack, "inst-1"));
                Assert.That(await pityStore.LoadAsync(Player, Pack.Id), Is.EqualTo(PackPityState.Fresh),
                    "Pity advanced despite the open never completing.");

                var resumed = await Service(ledger, pityStore, cards).OpenAsync(Player, Pack, "inst-1");

                Assert.That(resumed.Status, Is.EqualTo(PackOpenStatus.Opened));
                Assert.That(Render(resumed.Awards), Is.EqualTo(Render(referenceOutcome.Awards)),
                    $"Resuming after {allowed} written rows produced a different pack.");
                Assert.That(await ledger.HistoryAsync(Player), Has.Count.EqualTo(Pack.CardCount + 1),
                    "The resume duplicated a row instead of colliding on its transaction id.");
                Assert.That((await pityStore.LoadAsync(Player, Pack.Id)).PacksOpened, Is.EqualTo(1));
            }
        }

        // ── input hardening ──────────────────────────────────────────────────────────────────

        [Test]
        public void A_Missing_Pack_Instance_Id_Is_Refused()
        {
            var service = Service(new InMemoryEntitlementLedger(), new InMemoryPackPityStore(), Cards());
            Assert.ThrowsAsync<ArgumentException>(async () => await service.OpenAsync(Player, Pack, ""));
            Assert.ThrowsAsync<ArgumentException>(async () => await service.OpenAsync("", Pack, "inst-1"));
        }

        [Test]
        public async Task Two_Players_Do_Not_Share_A_Pity_Clock()
        {
            var ledger = new InMemoryEntitlementLedger();
            var pityStore = new InMemoryPackPityStore();
            var service = Service(ledger, pityStore, Cards());

            await service.OpenAsync("player-a", Pack, "a-1");
            await service.OpenAsync("player-a", Pack, "a-2");
            await service.OpenAsync("player-b", Pack, "b-1");

            Assert.That((await pityStore.LoadAsync("player-a", Pack.Id)).PacksOpened, Is.EqualTo(2));
            Assert.That((await pityStore.LoadAsync("player-b", Pack.Id)).PacksOpened, Is.EqualTo(1));
        }

        [Test]
        public async Task Two_Pack_Lines_Do_Not_Share_A_Pity_Clock()
        {
            var ledger = new InMemoryEntitlementLedger();
            var pityStore = new InMemoryPackPityStore();
            var service = Service(ledger, pityStore, Cards());

            var rekindling = new BoosterPack(SetId.REKINDLING);
            await service.OpenAsync(Player, Pack, "e-1");
            await service.OpenAsync(Player, Pack, "e-2");

            Assert.That((await pityStore.LoadAsync(Player, Pack.Id)).PacksOpened, Is.EqualTo(2));
            Assert.That((await pityStore.LoadAsync(Player, rekindling.Id)).PacksOpened, Is.EqualTo(0),
                "Grinding one set matured another set's clock.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>Every balance as one order-independent string — a replay must not move any of them.</summary>
        private static async Task<string> RenderBalances(IEntitlementLedger ledger, string playerId)
        {
            var balances = await ledger.BalancesAsync(playerId);
            var lines = new List<string>();
            foreach (var kv in balances) lines.Add($"{kv.Key}={kv.Value}");
            lines.Sort(StringComparer.Ordinal);
            return string.Join(";", lines);
        }

        private static string Render(IReadOnlyList<PackSlotAward> awards)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in awards)
                sb.Append(a.CardId).Append(':').Append(a.Rarity).Append(':')
                  .Append(a.ConvertedToShards ? "S" + a.Shards : "C").Append('|');
            return sb.ToString();
        }
    }
}
