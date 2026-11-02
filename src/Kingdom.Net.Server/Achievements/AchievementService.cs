using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// Turns one finished match into earned achievements. The whole decision lives here; the sink
    /// around it only gets the work off the match thread.
    ///
    /// <para><b>Find Game matches only, bot-filled ones included</b> (Sam, 2026-10-17). A friend
    /// match never counts, so two friends cannot trade achievements. A match the server filled with
    /// a bot has to count exactly like one against a person: an achievement that never arrived
    /// against a bot would give the bot away. The bot's own seat earns nothing.</para>
    /// </summary>
    public sealed class AchievementService
    {
        private static readonly IReadOnlyList<string> None = Array.Empty<string>();

        private readonly IAchievementStore _store;
        private readonly IAccountStore _accounts;
        private readonly Func<AccountId, bool>? _isBotAccount;

        public AchievementService(
            IAchievementStore store,
            IAccountStore accounts,
            Func<AccountId, bool>? isBotAccount = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _isBotAccount = isBotAccount;
        }

        /// <summary>
        /// Apply one seat's finished match. Returns the achievements it newly earned. Safe to call
        /// more than once with the same facts: the store keeps the first earning only.
        /// </summary>
        public async Task<IReadOnlyList<string>> ApplyAsync(AchievementMatchFacts facts, CancellationToken ct = default)
        {
            if (facts is null) throw new ArgumentNullException(nameof(facts));

            if (facts.Source != TelemetrySource.Ladder && facts.Source != TelemetrySource.BotFill) return None;
            AccountId accountId = AccountId.FromAuthenticatedAccount(facts.PlayerId);
            if (_isBotAccount?.Invoke(accountId) == true) return None;
            // Fail closed, as quests do: without the roster predicate a BotFill match cannot say
            // which seat is the bot.
            if (facts.Source == TelemetrySource.BotFill && _isBotAccount is null) return None;

            var earned = new List<string>();
            foreach (AchievementDefinition definition in AchievementCatalog.All)
                if (definition.EarnedBy(facts)) earned.Add(definition.Id);
            if (earned.Count == 0) return None;

            // A tokenless tooling seat (ServerOptions.AllowTokenlessMatches, never a player) has no account
            // to keep a Ledger on; GET /achievements could not show it one either.
            Account? account = await _accounts.FindByPlayerIdAsync(facts.PlayerId, ct).ConfigureAwait(false);
            if (account is null) return None;

            return await _store
                .EarnAsync(facts.PlayerId, earned, facts.MatchId, facts.EndedAtUtc, ct)
                .ConfigureAwait(false);
        }

        /// <summary>Everything the player has earned, oldest first — what <c>GET /achievements</c> renders.</summary>
        public Task<IReadOnlyList<EarnedAchievement>> EarnedAsync(string playerId, CancellationToken ct = default)
            => _store.ReadAsync(playerId, ct);
    }
}
