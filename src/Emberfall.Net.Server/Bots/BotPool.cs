using System;
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Bots
{
    public interface IBotPool
    {
        BotHandle? TryAcquire();

        /// <summary>True if <paramref name="account"/> belongs to a roster bot — busy or idle, once
        /// it has ever been provisioned. Lets a caller (<c>MatchGate.StartMatch</c>, via
        /// <c>TelemetrySources.Classify</c>) mark a bot-fill match apart from an ordinary human-vs-human
        /// ladder pairing, without <c>Telemetry</c> depending on <c>Bots</c> — the pool answers the
        /// question, the telemetry classifier just takes a predicate.</summary>
        bool IsBotAccount(AccountId account);
    }

    /// <summary>An acquired bot, seated for exactly one match. <see cref="Release"/> must be called
    /// once the match ends (or once it's known the bot was never actually seated), or the bot is
    /// permanently stuck "busy" and the pool silently shrinks.</summary>
    public sealed class BotHandle
    {
        private readonly Action _release;

        internal BotHandle(AccountId account, string displayName, SeatDeck deck, BotSeatRunner seat, Action release)
        {
            Account = account;
            DisplayName = displayName;
            Deck = deck;
            Seat = seat;
            _release = release;
        }

        public AccountId Account { get; }
        public string DisplayName { get; }
        public SeatDeck Deck { get; }
        public BotSeatRunner Seat { get; }

        public void Release() => _release();
    }

    /// <summary>
    /// In-memory bot roster + busy-tracking. <see cref="TryAcquire"/> is synchronous and touches no
    /// I/O — every account is pre-resolved by <see cref="BotAccountProvisioner"/> via
    /// <see cref="MarkReady"/> before anything calls <see cref="TryAcquire"/> for real, which is what
    /// makes it safe to call from inside <c>MatchGate</c>'s own lock (design doc §Bot roster).
    /// Before provisioning completes, the roster is simply empty and every acquire returns null — the
    /// caller retries on the next matchmaking wake, never blocks.
    /// </summary>
    public sealed class BotPool : IBotPool, IBotAccountSink
    {
        private readonly ICardDatabase _db;
        private readonly object _lock = new object();
        private readonly Dictionary<string, ResolvedBotAccount> _ready = new Dictionary<string, ResolvedBotAccount>(StringComparer.Ordinal);
        private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every account ever provisioned, busy or idle. Never shrinks — the roster is fixed
        /// for the process lifetime, so "was this ever a bot" and "is this a bot" are the same
        /// question. Backs <see cref="IsBotAccount"/>.</summary>
        private readonly HashSet<AccountId> _botAccounts = new HashSet<AccountId>();

        public BotPool(ICardDatabase db) => _db = db;

        public void MarkReady(ResolvedBotAccount account)
        {
            lock (_lock)
            {
                _ready[account.Username] = account;
                _botAccounts.Add(account.Account);
            }
        }

        public bool IsBotAccount(AccountId account)
        {
            lock (_lock) return _botAccounts.Contains(account);
        }

        public BotHandle? TryAcquire()
        {
            lock (_lock)
            {
                foreach (var kvp in _ready)
                {
                    string username = kvp.Key;
                    if (_busy.Contains(username)) continue;
                    _busy.Add(username);
                    ResolvedBotAccount resolved = kvp.Value;
                    var seat = new BotSeatRunner(resolved.DisplayName, new CommandProcessor(_db), _db);
                    return new BotHandle(resolved.Account, resolved.DisplayName, resolved.Deck, seat, () =>
                    {
                        lock (_lock) _busy.Remove(username);
                    });
                }
                return null;
            }
        }
    }
}
