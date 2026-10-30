using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Tools.StoreReconcile
{
    /// <summary>
    /// EF-080c reconciliation report — store transactions vs the entitlement ledger.
    ///
    /// <code>
    ///   # KINGDOM_ACCOUNTS_DB: the session-pooler URI from .env / Supabase.
    ///   # KINGDOM_PLAY_SERVICE_ACCOUNT: the Play service account key (inline JSON or a path).
    ///   dotnet run --project src/Kingdom.Tools.StoreReconcile
    /// </code>
    ///
    /// Reads <c>entitlement_grant</c> rows whose <c>external_txn_id</c> starts with <c>store:</c>
    /// or <c>refund:</c>, asks Play about each granted order and for its 30-day voided list
    /// (<see cref="PlayStoreFeed"/>), and answers:
    /// <list type="number">
    /// <item>Granted by us, an order Play does not know → <b>INVESTIGATE</b></item>
    /// <item>Refunded at the store, not clawed back here → <b>OWED CLAWBACK</b></item>
    /// <item>Paid, never granted, refunded by Play for want of an acknowledgement → listed, not an issue</item>
    /// </list>
    /// OWED GOODS stays in the report and is always zero from this feed; see <see cref="PlayStoreFeed"/>.
    ///
    /// Read-only. It reports; it does not fix. A missed clawback is fixed by the notification path
    /// (<c>/store/play/notifications</c>), which Pub/Sub redelivers for seven days.
    /// Exit 0 = no issues; 1 = issues found; 2 = misconfiguration.
    /// </summary>
    internal static class Program
    {
        private const string EnvVar = "KINGDOM_ACCOUNTS_DB";

        private static async Task<int> Main(string[] args)
        {
            string? connRaw = Kingdom.Net.Server.KingdomEnv.Get(EnvVar);
            if (string.IsNullOrWhiteSpace(connRaw))
            {
                Console.Error.WriteLine(
                    $"{EnvVar} is not set. Export the Supabase session-pooler URI first.");
                return 2;
            }

            PlayStoreReceiptValidator? play = PlayStoreReceiptValidator.FromEnvironment();
            if (play is null)
            {
                Console.Error.WriteLine(
                    $"{PlayStoreReceiptValidator.ServiceAccountEnvVar} is not set. Point it at the Play service account key.");
                return 2;
            }

            string connString = TelemetryDrain.NormalizeConnString(connRaw);
            var csb = new NpgsqlConnectionStringBuilder(connString);

            Console.WriteLine($"Store     : Google Play, {play.PackageName}");
            Console.WriteLine($"Target    : {csb.Host}:{csb.Port}/{csb.Database} (ssl={csb.SslMode})");
            Console.WriteLine(new string('-', 60));

            IReadOnlyList<LedgerStoreGrant> ledgerGrants;
            try
            {
                ledgerGrants = await LoadLedgerGrantsAsync(connString);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("DB QUERY FAILED:");
                for (Exception? e = ex; e is not null; e = e.InnerException)
                    Console.Error.WriteLine($"  {e.GetType().Name}: {e.Message}");
                return 2;
            }

            Console.WriteLine($"Ledger    : {ledgerGrants.Count} store/refund rows in entitlement_grant");

            PlayStoreFeedResult feed;
            try
            {
                feed = await PlayStoreFeed.BuildAsync(play, ledgerGrants, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            catch (StoreUnavailableException ex)
            {
                Console.Error.WriteLine($"PLAY QUERY FAILED: {ex.Message}");
                return 2;
            }

            Console.WriteLine($"Txns      : {feed.Transactions.Count} (from Play)");
            Console.WriteLine(new string('-', 60));

            ReconciliationReport report = StoreReconciler.Run(feed.Transactions, ledgerGrants);

            PrintCategory(
                "OWED GOODS (charged by store, no grant in ledger)",
                report.OwedGoods.Count,
                () =>
                {
                    foreach (OwedGoodsFinding f in report.OwedGoods)
                        Console.WriteLine($"  store_txn_id={f.StoreTxnId}  player={f.PlayerId}");
                });

            PrintCategory(
                "INVESTIGATE (ledger grant, no matching store charge)",
                report.Investigate.Count,
                () =>
                {
                    foreach (InvestigateFinding f in report.Investigate)
                        Console.WriteLine(
                            $"  store_txn_id={f.BaseTxnId}  player={f.PlayerId}  ledger_rows={f.LedgerRowCount}");
                });

            PrintCategory(
                "OWED CLAWBACK (store refunded, not reversed in ledger)",
                report.OwedClawback.Count,
                () =>
                {
                    foreach (OwedClawbackFinding f in report.OwedClawback)
                        Console.WriteLine(
                            $"  refund_id={f.StoreRefundId}  original_txn={f.OriginalTxnId}  player={f.PlayerId}");
                });

            Console.WriteLine();
            Console.WriteLine(
                $"[INFO] PAID, NEVER GRANTED, AUTO-REFUNDED BY PLAY (last 30 days): {feed.AutoRefundedUnacknowledged.Count}");
            foreach (PlayVoidedPurchase v in feed.AutoRefundedUnacknowledged)
                Console.WriteLine(
                    $"  order_id={v.OrderId}  voided_at={DateTimeOffset.FromUnixTimeMilliseconds(v.VoidedTimeMillis):u}");

            Console.WriteLine(new string('-', 60));
            if (report.HasIssues)
            {
                Console.WriteLine("ISSUES FOUND — review findings above.");
                return 1;
            }

            Console.WriteLine("CLEAN — no reconciliation issues.");
            return 0;
        }

        private static void PrintCategory(string heading, int count, Action printRows)
        {
            Console.WriteLine();
            Console.WriteLine($"[{(count > 0 ? "ISSUES" : "OK")}] {heading}: {count}");
            if (count > 0) printRows();
        }

        private static async Task<IReadOnlyList<LedgerStoreGrant>> LoadLedgerGrantsAsync(string connString)
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();

            // Pull every row whose external_txn_id was written by a store purchase or clawback.
            // Both prefixes are indexed via the primary key so this is a fast PK-range scan.
            const string Sql =
                "select external_txn_id, player_id, item, qty, source " +
                "from entitlement_grant " +
                "where external_txn_id like 'store:%' or external_txn_id like 'refund:%' " +
                "order by external_txn_id";

            await using var cmd = new NpgsqlCommand(Sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<LedgerStoreGrant>();
            while (await reader.ReadAsync())
            {
                rows.Add(new LedgerStoreGrant(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4)));
            }
            return rows.AsReadOnly();
        }
    }
}
