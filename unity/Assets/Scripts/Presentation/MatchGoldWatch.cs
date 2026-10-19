using System;
using System.Threading.Tasks;
using Kingdom.Net.Client.Shop;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Finds out how much Gold one match paid, for the proclamation (whimsy pass #2). The same two
    /// reads <see cref="MatchQuestWatch"/> makes, against the wallet: once when the match is wired,
    /// once when it ends, and the difference is the answer.
    ///
    /// <para><b>Why the wallet and not the match-end message.</b> The server pays on a queue behind
    /// the match (<c>MatchRewardWorker</c>), deliberately off the match thread, and whether this is
    /// the day's first win is a ledger question the match thread cannot answer without a database
    /// round trip. The wallet's difference is what actually landed: match Gold, first-win Gold and
    /// any quest this match completed, all of it.</para>
    ///
    /// <para><b>The second read retries</b>, for the reason the quest watch does: a read the moment
    /// the match ends can still answer the old balance. A match that paid nothing (a friend match)
    /// spends the whole budget, which fits inside <see cref="MatchEndSequence.StingerSeconds"/>.</para>
    ///
    /// <para>Every failure answers zero, which the proclamation shows as no Gold line at all. It is
    /// decoration and must never hold the screen up or put an error on it.</para>
    /// </summary>
    public sealed class MatchGoldWatch
    {
        private const int TimeoutSeconds = 10;
        private const int Attempts = 4;
        private const int RetryDelayMs = 1000;

        private readonly HttpShopGateway _gateway = new HttpShopGateway(
            CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds),
            AuthGateway.TokenForConnectAsync);

        private Task<long?> _before;
        private bool _cancelled;

        /// <summary>Take the baseline. Call once, when the match is wired.</summary>
        public void Begin() => _before = FetchAsync();

        /// <summary>Stop retrying — the scene that wanted the answer is gone.</summary>
        public void Cancel() => _cancelled = true;

        /// <summary>The Gold this match paid, or zero. Never throws.</summary>
        public async Task<long> EarnedAsync()
        {
            if (_before == null) return 0;
            long? before = await _before;
            if (before == null) return 0;

            for (int attempt = 0; attempt < Attempts && !_cancelled; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryDelayMs);
                if (_cancelled) break;
                long? after = await FetchAsync();
                if (after != null && after.Value > before.Value) return after.Value - before.Value;
            }
            return 0;
        }

        private async Task<long?> FetchAsync()
        {
            try
            {
                if (!AuthGateway.IsSignedIn) return null;
                ShopSnapshot shop = await _gateway.GetShopAsync();
                return shop.Wallet.Gold;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Gold] match-end wallet read threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "the proclamation shows no Gold line.");
                return null;
            }
        }
    }
}
