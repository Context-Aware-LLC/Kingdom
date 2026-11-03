using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.King
{
    /// <summary>Grants and reads the King's prize card backs. The seam exists so the sweep can be
    /// tested with backs the catalog does not hold yet; production is <see cref="LedgerPrizeBacks"/>.</summary>
    public interface IPrizeBacks
    {
        /// <summary>Grants the back, once per player, back and <paramref name="eventId"/>.</summary>
        Task GrantAsync(Account account, int backId, string eventId, CancellationToken ct);

        Task<bool> OwnsAsync(Account account, int backId, CancellationToken ct);
    }

    /// <summary>The prize backs in the entitlement ledger, through <see cref="CardBackOwnership"/>.
    /// A grant's key is <c>cardback:{id}:{player}:{eventId}</c>, which reverse-favor-prize.sql matches.</summary>
    public sealed class LedgerPrizeBacks : IPrizeBacks
    {
        public const string Source = "kings_favor";

        private readonly IEntitlementLedger _ledger;

        public LedgerPrizeBacks(IEntitlementLedger ledger)
            => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

        public Task GrantAsync(Account account, int backId, string eventId, CancellationToken ct)
            => CardBackOwnership.GrantAsync(_ledger, account, backId, Source, eventId, ct);

        public async Task<bool> OwnsAsync(Account account, int backId, CancellationToken ct)
            => await _ledger.BalanceAsync(account.PlayerId, CardBackOwnership.Item(backId), ct).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// The Tuesday sweep (#421). Every <see cref="Interval"/> it pays each closed reign at least
    /// <see cref="Grace"/> past its close, so matches running across Tuesday midnight can bank
    /// first. It reads unpaid reigns from the store rather than waking at midnight, so a server
    /// that was down across one Tuesday or several pays them all on its next pass.
    ///
    /// <para><b>Two passes.</b> The gold pass pays gold by place, writes a prize row per paid
    /// place, and closes the reign; <c>paid_at</c> is written last, and every grant is keyed on
    /// the reign, so a pass killed halfway re-runs the whole reign and pays nobody twice. The back
    /// pass then settles each top-three row's card back, and is skipped entirely while no place
    /// backs are in the catalog: the rows wait, and the first sweep after the art lands pays them.</para>
    ///
    /// <para>A player whose account is gone is skipped and their place left empty; nobody is
    /// promoted into it. So is a Favorite the operator struck before the sweep. Superusers never
    /// reach the board at all (<see cref="FavorService"/>). An account deleted through the
    /// deletion tool is different: that tool removes its scores, so the next player moves up.</para>
    ///
    /// <para><b>One window it does not close:</b> a match that banks a score after the grace, between
    /// a killed gold pass and its re-run, can move a paid player down a place. Their first grant
    /// stands, since a conflict counts as paid, and the player promoted past them is paid too, so
    /// that reign overpays by one place. That needs a match running over two hours past midnight.</para>
    /// </summary>
    public sealed class KingPayoutSweep : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan Grace = TimeSpan.FromHours(2);

        /// <summary>The places that earn a card back.</summary>
        public const int BackPlaces = 3;

        private readonly IFavorStore _store;
        private readonly IAccountStore _accounts;
        private readonly EconomyService _economy;
        private readonly IPrizeBacks _backs;
        private readonly FavorPrizeSettings _prizes;
        private readonly long _reignOneMs;
        private readonly ITimeSource _time;
        private readonly IReadOnlyList<int> _placeBackIds;
        private readonly IReadOnlyList<int> _poolIds;

        /// <param name="placeBackIds">1st, 2nd and 3rd's backs, or empty while their art does not exist.</param>
        /// <param name="poolIds">The backs a player who has held a place before chooses from.</param>
        public KingPayoutSweep(IFavorStore store, IAccountStore accounts, EconomyService economy, IPrizeBacks backs,
            FavorPrizeSettings prizes, long reignOneMs, ITimeSource time,
            IReadOnlyList<int> placeBackIds, IReadOnlyList<int> poolIds)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _economy = economy ?? throw new ArgumentNullException(nameof(economy));
            _backs = backs ?? throw new ArgumentNullException(nameof(backs));
            _prizes = prizes ?? throw new ArgumentNullException(nameof(prizes));
            _reignOneMs = reignOneMs;
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _placeBackIds = placeBackIds ?? throw new ArgumentNullException(nameof(placeBackIds));
            _poolIds = poolIds ?? throw new ArgumentNullException(nameof(poolIds));
            if (_placeBackIds.Count != 0 && _placeBackIds.Count != BackPlaces)
                throw new ArgumentException("Place backs are exactly " + BackPlaces + " ids, or none.", nameof(placeBackIds));
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
                    Console.Error.WriteLine("[king-sweep] pass failed, next in {0}: {1}: {2}",
                        Interval, ex.GetType().Name, ex.Message);
                }
                try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>One pass: the gold pass over every payable reign, oldest first, then the back pass.</summary>
        public async Task SweepAsync(CancellationToken ct = default)
        {
            // Every reign before the one Grace ago falls in has been closed at least Grace.
            int payableBefore = KingCalendar.ReignAt(_time.NowMs - (long)Grace.TotalMilliseconds, _reignOneMs);
            foreach (UnpaidReign reign in await _store.UnpaidReignsAsync(payableBefore, ct).ConfigureAwait(false))
                await PayGoldAsync(reign, ct).ConfigureAwait(false);

            if (_placeBackIds.Count == 0) return;
            await SettleBacksAsync(ct).ConfigureAwait(false);
        }

        private async Task PayGoldAsync(UnpaidReign reign, CancellationToken ct)
        {
            bool lowerIsBetter = CommissionCatalog.LowerIsBetter(KingSchedule.For(reign.Reign).Commission);
            // The top three earn backs even when fewer places earn gold.
            int places = Math.Max(_prizes.Gold.Count, BackPlaces);
            string eventId = "king:" + reign.Reign.ToString(CultureInfo.InvariantCulture);

            string? favoriteId = null, favoriteName = null;
            foreach (FavorRow row in await _store.TopAsync(reign.Reign, lowerIsBetter, places, ct).ConfigureAwait(false))
            {
                if (row.Place == 1 && reign.Struck)
                {
                    // Named on the record so the operator can see whom they struck; the Roll hides it.
                    favoriteId = row.PlayerId;
                    favoriteName = row.DisplayName;
                    continue;
                }
                Account? account = await _accounts.FindByPlayerIdAsync(row.PlayerId, ct).ConfigureAwait(false);
                if (account is null) continue;   // deleted: the place stays empty

                long gold = row.Place <= _prizes.Gold.Count ? _prizes.Gold[row.Place - 1] : 0;
                await GrantGoldAsync(account, gold, eventId, reign.Reign, ct).ConfigureAwait(false);
                await _store.RecordPrizeAsync(new FavorPrize(reign.Reign, row.PlayerId, row.Place, gold, null, false), ct)
                    .ConfigureAwait(false);
                if (row.Place == 1)
                {
                    favoriteId = row.PlayerId;
                    favoriteName = row.DisplayName;
                }
            }
            await _store.ClosePaidAsync(reign.Reign, favoriteId, favoriteName, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Taking a place for the first time earns its back; a place held in an earlier reign opens
        /// a choice from the pool, or pays gold when the player owns the whole pool. "Earlier" is
        /// read from the prize rows, not from what has been settled, so it is the same answer
        /// however late this pass runs: of two waiting rows at one place, the earlier reign's takes
        /// the back.
        /// </summary>
        private async Task SettleBacksAsync(CancellationToken ct)
        {
            foreach (FavorPrize prize in await _store.UnsettledBacksAsync(BackPlaces, ct).ConfigureAwait(false))
            {
                Account? account = await _accounts.FindByPlayerIdAsync(prize.PlayerId, ct).ConfigureAwait(false);
                if (account is null)
                {
                    await _store.SettleBackAsync(prize.Reign, prize.PlayerId, null, false, ct).ConfigureAwait(false);
                    continue;
                }

                string reignText = prize.Reign.ToString(CultureInfo.InvariantCulture);
                if (!await _store.HasHeldPlaceAsync(prize.PlayerId, prize.Place, prize.Reign, ct).ConfigureAwait(false))
                {
                    int back = _placeBackIds[prize.Place - 1];
                    await _backs.GrantAsync(account, back, "king:" + reignText, ct).ConfigureAwait(false);
                    await _store.SettleBackAsync(prize.Reign, prize.PlayerId, back, false, ct).ConfigureAwait(false);
                }
                else if (await PoolHasOneTheyLackAsync(account, ct).ConfigureAwait(false))
                {
                    await _store.SettleBackAsync(prize.Reign, prize.PlayerId, null, true, ct).ConfigureAwait(false);
                }
                else
                {
                    await GrantGoldAsync(account, _prizes.PoolEmptyGold, "king-pool:" + reignText, prize.Reign, ct)
                        .ConfigureAwait(false);
                    await _store.SettleBackAsync(prize.Reign, prize.PlayerId, null, false, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<bool> PoolHasOneTheyLackAsync(Account account, CancellationToken ct)
        {
            foreach (int id in _poolIds)
                if (!await _backs.OwnsAsync(account, id, ct).ConfigureAwait(false)) return true;
            return false;
        }

        /// <summary>
        /// Keyed on <paramref name="eventId"/>, so a repeat pays nothing. A conflict means the key
        /// was paid under other terms (the settings changed between a killed pass and its re-run):
        /// that is "already paid", or the reign would never close.
        /// </summary>
        private async Task GrantGoldAsync(Account account, long amount, string eventId, int reign, CancellationToken ct)
        {
            if (amount <= 0) return;
            try
            {
                await _economy.GrantGoldAsync(account, GoldRewardKind.KingsFavor, amount, eventId, ct).ConfigureAwait(false);
            }
            catch (GoldGrantConflictException)
            {
                Console.Error.WriteLine("[king-sweep] reign {0}: {1} for player_id={2} was already paid another amount; kept.",
                    reign, eventId, account.PlayerId);
            }
        }
    }
}
