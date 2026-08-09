using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// Applies the accounts + entitlement DDL once at startup, retrying forever until the DB is
    /// reachable (mirrors <see cref="Emberfall.Net.Server.Telemetry.TelemetryDrain"/>'s own
    /// schema-readiness loop). Registered only when <c>EMBERFALL_ACCOUNTS_DB</c> is configured — the
    /// in-memory store needs no schema, and nothing durable is lost by retrying: a socket that
    /// resolves an account before the schema lands simply fails that one connect (NOR-254).
    /// </summary>
    internal sealed class AccountSchemaInitializer : BackgroundService
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        private readonly PostgresAccountStore _store;

        public AccountSchemaInitializer(PostgresAccountStore store) => _store = store;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _store.EnsureSchemaAsync(ct); return; }
                catch (OperationCanceledException) { return; }
                catch (Exception) { await DelaySafe(ct); }
            }
        }

        private static async Task DelaySafe(CancellationToken ct)
        {
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
