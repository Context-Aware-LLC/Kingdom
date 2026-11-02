using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Kingdom.Policy;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Decks;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>Which policy an acquired bot plays. <see cref="Default"/> is ordinary bot fill:
    /// the configured bot model, or Greedy. The other two are the first-match policy's
    /// (<c>Matchmaking.FirstMatchPolicy</c>, launch item 5).</summary>
    public enum BotOpponent
    {
        Default,
        /// <summary>A newcomer's first match: <c>greedy-ai.v3</c>.</summary>
        Greedy,
        /// <summary>A newcomer's second match: the pinned second-match model (H1), or Greedy when
        /// none is configured. Never the moving champion.</summary>
        SecondMatch,
    }

    public interface IBotPool
    {
        BotHandle? TryAcquire(BotOpponent opponent = BotOpponent.Default);

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
        private readonly Func<SeatDeck, ISeatPolicy> _policyFactory;
        private readonly Func<SeatDeck, ISeatPolicy> _greedyFactory;
        private readonly Func<SeatDeck, ISeatPolicy> _secondMatchFactory;
        private readonly object _lock = new object();
        private readonly Dictionary<string, ResolvedBotAccount> _ready = new Dictionary<string, ResolvedBotAccount>(StringComparer.Ordinal);
        private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every account ever provisioned, busy or idle. Never shrinks — the roster is fixed
        /// for the process lifetime, so "was this ever a bot" and "is this a bot" are the same
        /// question. Backs <see cref="IsBotAccount"/>.</summary>
        private readonly HashSet<AccountId> _botAccounts = new HashSet<AccountId>();

        /// <param name="policyFactory">Ordinary bot fill's policy; null is Greedy.</param>
        /// <param name="secondMatchFactory">A newcomer's second match (<see cref="BotOpponent.SecondMatch"/>);
        /// null is Greedy.</param>
        public BotPool(ICardDatabase db, Func<SeatDeck, ISeatPolicy>? policyFactory = null,
            Func<SeatDeck, ISeatPolicy>? secondMatchFactory = null)
        {
            _db = db;
            _greedyFactory = _ => new GreedySeatPolicy(new CommandProcessor(_db), _db);
            _policyFactory = policyFactory ?? _greedyFactory;
            _secondMatchFactory = secondMatchFactory ?? _greedyFactory;
        }

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

        /// <summary>
        /// Seats one idle bot, chosen UNIFORMLY at random among the idle ones, playing the policy
        /// <paramref name="opponent"/> names. The roster entry (name and deck) is the same draw
        /// whichever policy plays it.
        ///
        /// <para><b>Why not simply the first idle entry.</b> That is what this did, and with a
        /// two-name roster it read as harmless. It is not a tie-break — <see cref="_ready"/> is keyed
        /// by username and enumerates in insertion order in practice, so "first idle" means the bot
        /// the roster file happens to list first wins every acquire where it is free. A player queueing
        /// against bot-fill met the same opponent, with the same deck, every time; every other roster
        /// entry only ever appeared while that one was mid-match. Growing the roster does nothing at
        /// all under that rule, which is exactly how it stayed invisible.</para>
        ///
        /// <para>The CSPRNG rather than <c>System.Random</c>: <c>Kingdom.Tools.DeterminismGate</c>
        /// check 7 bans the latter server-side. Nothing here reaches hashed state — which bot is
        /// seated is a matchmaking decision, made before the match exists, exactly like
        /// <c>MatchGate.MintSeed</c>'s own jitter.</para>
        /// </summary>
        public BotHandle? TryAcquire(BotOpponent opponent = BotOpponent.Default)
        {
            Func<SeatDeck, ISeatPolicy> factory = opponent switch
            {
                BotOpponent.Greedy => _greedyFactory,
                BotOpponent.SecondMatch => _secondMatchFactory,
                _ => _policyFactory,
            };
            lock (_lock)
            {
                var idle = new List<string>(_ready.Count);
                foreach (var kvp in _ready)
                    if (!_busy.Contains(kvp.Key)) idle.Add(kvp.Key);
                if (idle.Count == 0) return null;

                // Sorted before the draw: _ready's enumeration order is a Dictionary implementation
                // detail, and an unsorted list would make the draw depend on it. Ordinal, because
                // usernames are keys.
                idle.Sort(StringComparer.Ordinal);

                string username = idle[RandomNumberGenerator.GetInt32(idle.Count)];
                _busy.Add(username);
                ResolvedBotAccount resolved = _ready[username];
                var seat = new BotSeatRunner(
                    resolved.Username,
                    resolved.DisplayName,
                    factory(resolved.Deck));
                return new BotHandle(resolved.Account, resolved.DisplayName, resolved.Deck, seat, () =>
                {
                    lock (_lock) _busy.Remove(username);
                });
            }
        }
    }
}
