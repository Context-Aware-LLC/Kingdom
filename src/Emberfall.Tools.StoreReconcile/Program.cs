using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Tools.StoreReconcile
{
    /// <summary>
    /// EF-080c reconciliation report — store transactions vs the entitlement ledger.
    ///
    /// <code>
    ///   # EMBERFALL_ACCOUNTS_DB must be exported (the session-pooler URI from .env / Supabase).
    ///   dotnet run --project src/Emberfall.Tools.StoreReconcile [-- path/to/store_transactions.jsonl]
    /// </code>
    ///
    /// Reads <c>entitlement_grant</c> rows whose <c>external_txn_id</c> starts with <c>store:</c>
    /// or <c>refund:</c>, then joins against the stub feed to answer three questions:
    /// <list type="number">
    /// <item>Charged at the store, no grant in our ledger → <b>OWED GOODS</b></item>
    /// <item>Granted by us, no matching store charge → <b>INVESTIGATE</b></item>
    /// <item>Refunded at the store, not clawed back here → <b>OWED CLAWBACK</b></item>
    /// </list>
    ///
    /// Read-only. It reports; it does not fix.
    /// Exit 0 = no issues; 1 = issues found; 2 = misconfiguration.
    /// </summary>
    internal static class Program
    {
        private const string EnvVar = "EMBERFALL_ACCOUNTS_DB";

        private static async Task<int> Main(string[] args)
        {
            string? connRaw = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(connRaw))
            {
                Console.Error.WriteLine(
                    $"{EnvVar} is not set. Export the Supabase session-pooler URI first.");
                return 2;
            }

            string? feedPath = ResolveStubFeed(args.Length > 0 ? args[0] : null);
            if (feedPath is null)
            {
                Console.Error.WriteLine(
                    "Store-transaction stub feed not found. Pass a path, or run from the repo so " +
                    "src/Emberfall.Tools.StoreReconcile/sample/store_transactions.jsonl resolves.");
                return 2;
            }

            IReadOnlyList<StoreTransaction> storeTxns;
            try { storeTxns = StoreTransactionFeed.LoadJsonl(feedPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load stub feed: {ex.Message}");
                return 2;
            }

            string connString = TelemetryDrain.NormalizeConnString(connRaw);
            var csb = new NpgsqlConnectionStringBuilder(connString);

            Console.WriteLine($"Feed      : {feedPath}");
            Console.WriteLine($"Txns      : {storeTxns.Count} (store feed)");
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
            Console.WriteLine(new string('-', 60));

            ReconciliationReport report = StoreReconciler.Run(storeTxns, ledgerGrants);

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

        /// <summary>Explicit arg wins; otherwise walk up from the binary to the repo sample.</summary>
        private static string? ResolveStubFeed(string? arg)
        {
            if (arg is not null)
            {
                string p = Path.GetFullPath(arg);
                return File.Exists(p) ? p : null;
            }

            // Walk up from the binary towards the repo root, looking for the checked-in sample.
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            {
                string candidate = Path.Combine(
                    dir, "src", "Emberfall.Tools.StoreReconcile", "sample", "store_transactions.jsonl");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
