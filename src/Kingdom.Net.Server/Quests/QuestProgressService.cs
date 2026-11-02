using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.Quests
{
    /// <summary>
    /// Turns one finished match into quest progress and, when a quest completes, into Gold
    /// (dev-docs/specs/daily-quests.md §6). This is the whole quest decision; the sink and worker
    /// around it exist only to get the work off the match thread.
    ///
    /// <para><b>It credits the quests the player held on the day the MATCH ENDED, not today.</b> A
    /// match that finishes at 23:59:58 UTC and is evaluated four seconds later still belongs to the
    /// day it was played — anything else silently drops a player's last match of the day, which is
    /// the one they were most likely playing to finish a quest.</para>
    /// </summary>
    public sealed class QuestProgressService
    {
        private readonly IQuestProgressStore _progress;
        private readonly IAccountStore _accounts;
        private readonly GoldFaucet _faucet;
        private readonly Func<AccountId, bool>? _isBotAccount;

        public QuestProgressService(
            IQuestProgressStore progress,
            IAccountStore accounts,
            GoldFaucet faucet,
            Func<AccountId, bool>? isBotAccount = null)
        {
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _faucet = faucet ?? throw new ArgumentNullException(nameof(faucet));
            _isBotAccount = isBotAccount;
        }

        /// <summary>
        /// Apply one seat's finished match. Safe to call more than once with the same facts — the
        /// progress key and the faucet's transaction id both collapse a replay.
        /// </summary>
        public async Task ApplyAsync(QuestMatchFacts facts, CancellationToken ct = default)
        {
            if (facts is null) throw new ArgumentNullException(nameof(facts));

            // Bot fill must be progression-identical for the PLAYER, without creating a second
            // economy for roster accounts. Fail closed when a BotFill event arrives without the
            // roster predicate that can distinguish the seats. AI soak is never player progress.
            if (facts.Source == TelemetrySource.AiSoak) return;
            AccountId accountId = AccountId.FromAuthenticatedAccount(facts.PlayerId);
            if (_isBotAccount?.Invoke(accountId) == true) return;
            if (facts.Source == TelemetrySource.BotFill && _isBotAccount is null) return;

            Account? account = await _accounts
                .FindByPlayerIdAsync(facts.PlayerId, ct)
                .ConfigureAwait(false);
            // A tokenless tooling seat (ServerOptions.AllowTokenlessMatches, never a player) has no account
            // row and is not paid. Roster bots are filtered above even though they do have rows:
            // only the human half of bot fill owns progression. Both decisions live here because
            // "who can hold a quest" is this service's question and the sink should not have to
            // know the answer.
            if (account is null) return;

            DateOnly day = DateOnly.FromDateTime(facts.EndedAtUtc.UtcDateTime);

            foreach ((DateOnly questDay, QuestDefinition quest) in QuestCatalog.Active(facts.PlayerId, day))
            {
                int credit = quest.Credit(facts);
                if (credit <= 0) continue;

                QuestCreditResult result = await _progress.CreditAsync(
                    new QuestCredit(facts.PlayerId, quest.Id, questDay, facts.MatchId, credit, facts.EndedAtUtc),
                    ct).ConfigureAwait(false);

                // Pay on the TRANSITION into completion, never on merely being complete. The faucet
                // keys its transaction id on the current UTC day, so a stored quest completed
                // yesterday and credited again today would mint a fresh key and pay twice. A replay
                // wrote nothing and so is not a transition; a match that pushes the total from below
                // the goal to at or above it is the only thing that pays.
                if (!result.Applied) continue;
                if (result.Total < quest.Goal || result.Total - credit >= quest.Goal) continue;

                // Granted on completion, not claimed (spec §8). No "paid" flag is stored anywhere —
                // the ledger is the flag.
                await _faucet
                    .GrantDailyQuestAsync(account, QuestTransactionId(quest.Id, questDay), ct)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// What the faucet's per-quest idempotency key is built from. The faucet already scopes its
        /// transaction id by the CURRENT UTC day, and a stored quest can complete on a later day
        /// than the one it was assigned for — so the assigned day has to be inside the quest id, or
        /// two different quest-days could collapse onto one payment.
        /// </summary>
        internal static string QuestTransactionId(string questId, DateOnly day)
            => questId + "@" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The player's active quests with their progress — what <c>GET /quests</c> renders.
        /// </summary>
        public async Task<IReadOnlyList<QuestView>> ViewAsync(
            string playerId,
            DateOnly today,
            CancellationToken ct = default)
        {
            IReadOnlyList<(DateOnly Day, QuestDefinition Quest)> active =
                QuestCatalog.Active(playerId, today);

            var keys = new List<(string, DateOnly)>(active.Count);
            foreach ((DateOnly day, QuestDefinition quest) in active) keys.Add((quest.Id, day));

            IReadOnlyDictionary<(string QuestId, DateOnly Day), int> progress =
                await _progress.ProgressAsync(playerId, keys, ct).ConfigureAwait(false);

            var views = new List<QuestView>(active.Count);
            foreach ((DateOnly day, QuestDefinition quest) in active)
            {
                int done = progress.TryGetValue((quest.Id, day), out int n) ? n : 0;
                views.Add(new QuestView(
                    quest.Id,
                    quest.Title,
                    quest.Quip,
                    quest.Goal,
                    // Clamped: a card quest can overshoot its goal in the match that completes it,
                    // and a progress bar reading 19/15 looks like a bug to the person holding it.
                    Math.Min(done, quest.Goal),
                    day,
                    done >= quest.Goal));
            }
            return views;
        }
    }

    /// <summary>One row of <c>GET /quests</c>. Flat and scalar-only — see the endpoint for why.</summary>
    public readonly record struct QuestView(
        string Id,
        string Title,
        string Quip,
        int Goal,
        int Progress,
        DateOnly Day,
        bool Complete);
}
