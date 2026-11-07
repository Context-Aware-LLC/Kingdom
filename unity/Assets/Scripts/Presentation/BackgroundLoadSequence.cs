#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kingdom.Presentation
{
    /// <summary>Local fallback first, optional remote upgrade, shared requests and bounded retries.</summary>
    public sealed class BackgroundLoadSequence<T> where T : class
    {
        private sealed class Entry
        {
            public Task<T?> Pending = null!;
            public double RetryAfter;
        }

        private readonly Dictionary<string, Entry> _loads = new();
        private readonly Func<BackgroundTier, string, Task<T?>> _load;
        private readonly Func<double> _time;

        // The adapter returns null on an unavailable asset. Successful loads live for the session;
        // a failed request can be tried on a later screen visit without hammering an offline host.
        public BackgroundLoadSequence(Func<BackgroundTier, string, Task<T?>> load, Func<double> time)
        {
            _load = load;
            _time = time;
        }

        public async Task LoadAsync(string key, BackgroundTier tier, Action<T> ready)
        {
            string preferred = BackgroundVariantPolicy.Address(tier, key);
            if (_loads.TryGetValue(preferred, out var cached)
                && cached.Pending.IsCompletedSuccessfully && cached.Pending.Result != null)
            {
                ready(cached.Pending.Result);
                return;
            }

            T? local = await GetAsync(BackgroundTier.Mobile, key);
            if (local != null) ready(local);
            if (tier == BackgroundTier.Mobile) return;

            T? larger = await GetAsync(BackgroundTier.DesktopTablet, key);
            if (larger != null) ready(larger);
        }

        private Task<T?> GetAsync(BackgroundTier tier, string key)
        {
            string address = BackgroundVariantPolicy.Address(tier, key);
            if (_loads.TryGetValue(address, out var entry))
            {
                if (!entry.Pending.IsCompleted || entry.Pending.Result != null || _time() < entry.RetryAfter)
                    return entry.Pending;
            }

            entry = new Entry();
            _loads[address] = entry;
            entry.Pending = FetchAsync(entry, tier, key);
            return entry.Pending;
        }

        private async Task<T?> FetchAsync(Entry entry, BackgroundTier tier, string key)
        {
            T? value = await _load(tier, key);
            if (value == null) entry.RetryAfter = _time() + 60;
            return value;
        }
    }
}
