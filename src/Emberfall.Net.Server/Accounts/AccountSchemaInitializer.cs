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
    ///
    /// <para><b>Takes the DDL call as a delegate because two adapters need it and both run the
    /// identical statement.</b> <c>PostgresAccountStore.EnsureSchemaAsync</c> and
    /// <c>PostgresEntitlementLedger.EnsureSchemaAsync</c> each execute <see cref="AccountsSchema.Ddl"/>
    /// — <c>account</c> and <c>entitlement_grant</c> are one canonical <c>schema.sql</c> — so the
    /// composition root registers ONE of these whichever durable component is configured, rather than
    /// duplicating this retry loop per store.</para>
    /// </summary>
    internal sealed class AccountSchemaInitializer : BackgroundService
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        private readonly Func<CancellationToken, Task> _ensureSchema;

        public AccountSchemaInitializer(Func<CancellationToken, Task> ensureSchema)
            => _ensureSchema = ensureSchema ?? throw new ArgumentNullException(nameof(ensureSchema));

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _ensureSchema(ct); return; }
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
