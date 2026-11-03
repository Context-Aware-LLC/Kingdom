using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// Banks one seat's commission score into the King's Favor at match end, and pays the gold for
    /// qualifying in a reign: attempted on every qualifying score, paid once, because the grant is
    /// keyed on the reign. Called from <see cref="AchievementSink"/>'s worker, off the match thread,
    /// once per attributed seat.
    ///
    /// <para><b>Ladder and bot-filled matches only</b>, as with achievements: a friend match never
    /// scores, and a bot-filled one scores exactly like a human one or the board gives the bot away.
    /// <b>Superuser accounts never appear</b>, because they own every card; bots are Superuser
    /// accounts, so that is also what keeps a bot's own seat off the board.</para>
    /// </summary>
    public sealed class FavorService
    {
        private readonly IFavorStore _store;
        private readonly IAccountStore _accounts;
        private readonly EconomyService? _economy;
        private readonly FavorPrizeSettings? _prizes;

        /// <param name="economy">Null on a box with no ledger: scores bank and no gold is paid.</param>
        /// <param name="prizes">Null when <c>KINGDOM_FAVOR_PRIZES</c> is unset or invalid: the same.</param>
        public FavorService(IFavorStore store, IAccountStore accounts, EconomyService? economy, FavorPrizeSettings? prizes)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _economy = economy;
            _prizes = prizes;
        }

        /// <summary>
        /// Bank <paramref name="facts"/>' score under <paramref name="king"/>'s reign, which is the
        /// reign the match started in, never the one it ended in. <paramref name="displayName"/> is
        /// the name the seat played under.
        /// </summary>
        public async Task ApplyAsync(AchievementMatchFacts facts, KingSeating king, string displayName, CancellationToken ct)
        {
            if (facts is null) throw new ArgumentNullException(nameof(facts));
            if (king is null) throw new ArgumentNullException(nameof(king));

            if (facts.Source != TelemetrySource.Ladder && facts.Source != TelemetrySource.BotFill) return;
            if (king.Reign < 1) return;
            Commission? commission = CommissionCatalog.Find(king.CommissionId);
            if (commission is null)
            {
                Console.Error.WriteLine("[king] reign {0} asks for unknown commission '{1}'; nothing banked.",
                    king.Reign, king.CommissionId);
                return;
            }

            // "A game you won" includes a win by concede or timeout: the result is the test, not the reason.
            if (commission.NeedsWin && facts.Result != SeatResult.Win) return;
            int score = commission.Score(facts);
            if (!commission.Qualifies(score)) return;

            // A tokenless tooling seat has no account row and so no place on a board.
            Account? account = await _accounts.FindByPlayerIdAsync(facts.PlayerId, ct).ConfigureAwait(false);
            if (account is null || account.Kind == AccountKind.Superuser) return;

            await _store.RecordAsync(
                new FavorScore(king.Reign, facts.PlayerId, displayName, score, facts.EndedAtUtc),
                commission.LowerIsBetter, ct).ConfigureAwait(false);
            await PayQualifyingGoldAsync(account, king.Reign, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Qualifying in a reign earns gold once. It is attempted on EVERY qualifying score, not only
        /// the first, and the <c>king-qualify:{reign}</c> key makes every repeat free: a grant lost
        /// to a crash or a ledger failure after the score banked is paid by the player's next one.
        ///
        /// <para>A conflict means the key is already paid under other terms, typically
        /// <c>QualifyGold</c> changed mid-reign. That is "already paid", not a failure: the score
        /// banked and the player got their gold, so it must not reach the worker as one.</para>
        /// </summary>
        private async Task PayQualifyingGoldAsync(Account account, int reign, CancellationToken ct)
        {
            if (_economy is null || _prizes is null || _prizes.QualifyGold <= 0) return;
            try
            {
                await _economy.GrantGoldAsync(
                    account, GoldRewardKind.KingsFavor, _prizes.QualifyGold,
                    "king-qualify:" + reign.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }
            catch (GoldGrantConflictException)
            {
                // Already paid; see the summary.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The score is already banked, so this must not read as "not banked" in the worker.
                Console.Error.WriteLine(
                    "[king] reign {0} qualifying gold for player_id={1} was not paid; their next qualifying " +
                    "score retries it: {2}", reign, account.PlayerId, ex.Message);
            }
        }
    }
}
