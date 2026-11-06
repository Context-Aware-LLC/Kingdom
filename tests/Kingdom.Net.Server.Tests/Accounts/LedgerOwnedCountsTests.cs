using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Tests.Support;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The projection that finally gives L6 something to constrain against (spec:
    /// <c>docs/dev-docs/specs/card-ownership.md</c>, slice 2).
    ///
    /// <para>Until this landed, <c>AccountOwnedCounts.For</c> returned <c>null</c> for every standard
    /// account, and null means "ownership UNKNOWN, do not constrain". So the server's card-ownership
    /// rule was wired end to end and inert — and the failure was invisible, because a full pool and an
    /// unconstrained pool render identically.</para>
    ///
    /// <para>The three states that must stay distinct are the whole point of this fixture: <b>null</b>
    /// (unknown — a tokenless tooling seat, or a box with no economy), <b>an empty projection</b> (a real account that
    /// owns nothing), and <b><c>AtLimitOwnedCounts</c></b> (an identity that owns every pool-eligible
    /// card at its rarity limit). Collapsing any two of them either locks legitimate players out of their decks or
    /// hands everyone every card.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class LedgerOwnedCountsTests
    {
        private const string Player = "player-0001";

        private static string CardItem(int cardId) => BoosterPackGrantService.CardItemPrefix + cardId;

        private static Task Grant(IEntitlementLedger ledger, string item, long qty, string txn)
            => ledger.GrantAsync(new EntitlementGrant(
                Player, item, qty, "test", txn, DateTimeOffset.UnixEpoch));

        private static Account Standard(string playerId = Player)
            => new Account(playerId, "google", "subject-hash", DateTimeOffset.UnixEpoch);

        // ── the projection ───────────────────────────────────────────────────────────────────

        [Test]
        public async Task Copies_Are_The_Signed_Sum_Of_A_Cards_Grants()
        {
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, CardItem(42), 1, "t1");
            await Grant(ledger, CardItem(42), 1, "t2");

            OwnedCardCounts owned = await LedgerOwnedCounts.LoadAsync(ledger, Player);

            Assert.That(owned.CopiesOwned(42), Is.EqualTo(2));
        }

        [Test]
        public async Task A_Card_With_No_Grants_Is_Zero_Not_An_Error()
        {
            OwnedCardCounts owned =
                await LedgerOwnedCounts.LoadAsync(new InMemoryEntitlementLedger(), Player);

            Assert.That(owned.CopiesOwned(999), Is.Zero);
        }

        /// <summary>
        /// The ledger holds currency, unopened packs, pity seeds and completion markers alongside
        /// cards. Only <c>card:</c> rows are a collection — a projection that swept the rest in would
        /// report gold as a card id.
        /// </summary>
        [Test]
        public async Task Non_Card_Items_Are_Ignored()
        {
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, EconomyItems.Gold, 500, "t1");
            await Grant(ledger, new BoosterPack(Kingdom.Core.Contracts.SetId.PENDINGHAM).Id, 3, "t2");
            await Grant(ledger, BoosterPackGrantService.OpenedItemPrefix + "pack.pendingham", 1, "t3");

            OwnedCardCounts owned = await LedgerOwnedCounts.LoadAsync(ledger, Player);

            Assert.That(owned.RecordedCards, Is.Zero, "gold, packs and markers are not cards");
        }

        [Test]
        public async Task A_Negative_Balance_Clamps_To_Zero()
        {
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, CardItem(7), 1, "t1");
            await Grant(ledger, CardItem(7), -3, "t2");

            OwnedCardCounts owned = await LedgerOwnedCounts.LoadAsync(ledger, Player);

            Assert.That(owned.CopiesOwned(7), Is.Zero,
                "a negative net is a ledger bug; it must not become a negative copy limit");
        }

        /// <summary>
        /// One unparseable row must not cost the player their whole collection. Same posture as
        /// <c>BoosterPackGrantService.ProjectOwnership</c>: skipped, never thrown on.
        /// </summary>
        [Test]
        public async Task An_Unparseable_Card_Item_Is_Skipped_Rather_Than_Thrown_On()
        {
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, BoosterPackGrantService.CardItemPrefix + "ember_acolyte", 1, "t1");
            await Grant(ledger, CardItem(5), 1, "t2");

            OwnedCardCounts owned = await LedgerOwnedCounts.LoadAsync(ledger, Player);

            Assert.That(owned.CopiesOwned(5), Is.EqualTo(1), "one bad row must not lose the good ones");
            Assert.That(owned.RecordedCards, Is.EqualTo(1));
        }

        private static ICardDatabase Db => SessionHarness.Db;

        // ── AccountOwnedCounts: the three states ─────────────────────────────────────────────

        [Test]
        public async Task A_Superuser_Owns_Everything_Without_Consulting_The_Ledger()
        {
            var account = new Account("su", "google", "h", DateTimeOffset.UnixEpoch,
                                      Kind: AccountKind.Superuser);

            var result = await AccountOwnedCounts.ForAsync(account, new InMemoryEntitlementLedger(), Db);

            Assert.That(result, Is.TypeOf<AtLimitOwnedCounts>());
        }

        [Test]
        public async Task ForAsync_Is_RecordForAsync_Resolved_For_Both_Account_Kinds()
        {
            // Pins the gate's view and the client's view to one decision.
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, CardItem(3), 2, "t1");
            var su = new Account("su", "google", "h", DateTimeOffset.UnixEpoch, Kind: AccountKind.Superuser);
            int sampled = 0;
            foreach (Account account in new[] { su, Standard() })
            {
                var record = await AccountOwnedCounts.RecordForAsync(account, ledger);
                var viaRecord = record!.Resolve(Db);
                var viaFor = await AccountOwnedCounts.ForAsync(account, ledger, Db);
                foreach (var def in Db.AllCollectible().Take(25))
                {
                    Assert.That(viaFor!.CopiesOwned(def.Id), Is.EqualTo(viaRecord.CopiesOwned(def.Id)));
                    sampled++;
                }
                Assert.That(viaFor!.CopiesOwned(3), Is.EqualTo(viaRecord.CopiesOwned(3)));
            }
            Assert.That(sampled, Is.GreaterThan(0));
        }

        [Test]
        public async Task RecordForAsync_Is_Null_When_Ownership_Is_Unknown()
        {
            Assert.That(await AccountOwnedCounts.RecordForAsync(null, new InMemoryEntitlementLedger()), Is.Null);
            Assert.That(await AccountOwnedCounts.RecordForAsync(Standard(), null), Is.Null);
        }

        [Test]
        public async Task No_Account_Is_Unknown_Rather_Than_Empty()
        {
            Assert.That(await AccountOwnedCounts.ForAsync(null, new InMemoryEntitlementLedger(), Db),
                Is.Null, "a tokenless seat's ownership is unknown; treating it as empty undecks the player");
        }

        [Test]
        public async Task No_Ledger_Is_Unknown_So_A_Dev_Box_Is_Not_Constrained()
        {
            Assert.That(await AccountOwnedCounts.ForAsync(Standard(), null, Db),
                Is.Null, "a box with no economy configured must not start refusing decks");
        }

        /// <summary>
        /// The assertion this whole plan exists for: a standard account with a ledger is CONSTRAINED.
        /// This is the null that used to make L6 inert for every real player.
        /// </summary>
        [Test]
        public async Task A_Standard_Account_With_A_Ledger_Is_Constrained()
        {
            var ledger = new InMemoryEntitlementLedger();
            await Grant(ledger, CardItem(3), 2, "t1");

            var result = await AccountOwnedCounts.ForAsync(Standard(), ledger, Db);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.CopiesOwned(3), Is.EqualTo(2));
            Assert.That(result.CopiesOwned(4), Is.Zero, "an ungranted card is genuinely not owned");
        }

        /// <summary>
        /// A real account that has been granted nothing yet projects EMPTY, not null. That is the
        /// state that makes every deck a draft, and it must be reachable — otherwise "owns nothing"
        /// could never be expressed and ownership would be unenforceable by omission.
        /// </summary>
        [Test]
        public async Task An_Ungranted_Standard_Account_Projects_Empty_Not_Null()
        {
            var result = await AccountOwnedCounts.ForAsync(Standard(), new InMemoryEntitlementLedger(), Db);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.CopiesOwned(1), Is.Zero);
        }
    }
}
