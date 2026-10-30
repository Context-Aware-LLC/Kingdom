using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// Reverses a voided Play order, named by its order id alone. Shared by the two ways a refund
    /// reaches the server: <see cref="PlayNotificationEndpoint"/> (Pub/Sub, when Play sends one) and
    /// <see cref="PlayVoidedSweep"/> (Play's voided list, which it always keeps).
    ///
    /// <para>The ledger, not Play, says whose purchase it was: neither source carries a product or
    /// an account, and the grant row names both. Keyed on the order id, so whichever path arrives
    /// second, and any repeat, is a replay.</para>
    /// </summary>
    public sealed class PlayOrderRefunds
    {
        private readonly IEntitlementLedger _ledger;
        private readonly IAccountStore _accounts;
        private readonly RefundService _refunds;

        public PlayOrderRefunds(IEntitlementLedger ledger, IAccountStore accounts, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _refunds = new RefundService(ledger, time);
        }

        /// <summary>The outcome as a lowercase word: <c>refunded</c>, <c>replayed</c>,
        /// <c>not_granted</c> or <c>no_account</c>. Throws on a ledger or account-store failure.</summary>
        public async Task<string> ClawBackAsync(string orderId, string logTag, CancellationToken ct = default)
        {
            EntitlementGrant? purchase = await _ledger.FindAsync(GemGrantService.PurchaseRowId(orderId), ct)
                .ConfigureAwait(false);
            if (purchase == null)
            {
                // Paid and refunded before any grant, or never redeemed: nothing to claw back.
                return "not_granted";
            }
            Account? account = await _accounts.FindByPlayerIdAsync(purchase.PlayerId, ct).ConfigureAwait(false);
            if (account == null)
            {
                Console.Error.WriteLine("[{0}] voided order {1}: grant names player_id={2}, which has no account",
                    logTag, orderId, purchase.PlayerId);
                return "no_account";
            }

            // A quantity-based partial refund voids the same order again; gem tiers are single-quantity,
            // so the first void reverses the whole grant and the order id makes any repeat a replay.
            RefundOutcome outcome = await _refunds.RefundAsync(
                account, new ValidatedStoreRefund(orderId, orderId, account.PlayerId), ct).ConfigureAwait(false);
            if (outcome.Status == RefundStatus.Refunded)
                Console.WriteLine("[{0}] voided order {1}: clawed back for player_id={2}, gems now {3}",
                    logTag, orderId, account.PlayerId, outcome.Wallet.Gems);
            return outcome.Status.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Every <see cref="Interval"/>, reads Play's voided-purchases list for the last
    /// <see cref="Lookback"/> and claws back any order not yet reversed. The backstop under the
    /// Pub/Sub path: Play keeps the list whether or not it sent a notification, and on the first
    /// console refund of a license-tester purchase (2026-10-30) it listed the void at once and
    /// published nothing to the topic.
    ///
    /// <para>One list call per pass, plus one ledger lookup per voided order; Play allows 6,000
    /// list calls a day. A failed pass is logged and the next one retries.</para>
    /// </summary>
    public sealed class PlayVoidedSweep : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

        private readonly IPlayOrders _play;
        private readonly PlayOrderRefunds _refunds;
        private readonly ITimeSource _time;

        public PlayVoidedSweep(IPlayOrders play, PlayOrderRefunds refunds, ITimeSource time)
        {
            _play = play ?? throw new ArgumentNullException(nameof(play));
            _refunds = refunds ?? throw new ArgumentNullException(nameof(refunds));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[play-sweep] pass failed, next in {0}: {1}: {2}",
                        Interval, ex.GetType().Name, ex.Message);
                }
                try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>One pass. Returns how many orders it clawed back.</summary>
        public async Task<int> SweepAsync(CancellationToken ct = default)
        {
            long since = _time.NowMs - (long)Lookback.TotalMilliseconds;
            IReadOnlyList<PlayVoidedPurchase> voided = await _play.ListVoidedAsync(since, ct).ConfigureAwait(false);
            int clawed = 0;
            foreach (PlayVoidedPurchase v in voided)
            {
                if (await _refunds.ClawBackAsync(v.OrderId, "play-sweep", ct).ConfigureAwait(false) == "refunded")
                    clawed++;
            }
            return clawed;
        }
    }
}
