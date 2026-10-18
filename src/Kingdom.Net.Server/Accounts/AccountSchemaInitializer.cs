using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// Applies the accounts + entitlement DDL once at startup, retrying forever until the DB is
    /// reachable (mirrors <see cref="Kingdom.Net.Server.Telemetry.TelemetryDrain"/>'s own
    /// schema-readiness loop). Registered only when <c>KINGDOM_ACCOUNTS_DB</c> is configured — the
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

        /// <summary>
        /// <b>It says so, on both outcomes.</b> This loop used to be entirely silent: DDL that never
        /// applied left the server UP and healthy, every account-backed route answering 503 forever,
        /// and nothing anywhere saying why. That is the most expensive shape a failure can take —
        /// indistinguishable from "the feature was never deployed" — and it cost a deploy
        /// verification on 2026-09-02, when there was no way to confirm from outside that the
        /// <c>character</c> and <c>deck</c> tables had been created.
        ///
        /// <para>One line on success naming the tables, one per failed attempt with the reason.
        /// <c>Console.Error</c> rather than a logger, because that is what <c>TelemetryDrain</c> and
        /// the Postgres stores already use and Fly captures both streams.</para>
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _ensureSchema(ct);
                    Console.Error.WriteLine(
                        // The tables schema.sql actually creates. It listed `character`, which the
                        // 2026-09-04 migration dropped, and omitted the two ladder tables — a
                        // deploy-verification line that names the wrong tables is worse than none,
                        // since its whole job is confirming from outside that they exist.
                        "[accounts-schema] applied: account, entitlement_grant, ladder_player, " +
                        "ladder_match, deck, quest_progress." +
                        (attempt > 0 ? $" (after {attempt} failed attempt(s))" : ""));
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    attempt++;
                    Console.Error.WriteLine(
                        "[accounts-schema] attempt {0} failed ({1}) — retrying in {2}s. " +
                        "Until this succeeds every account-backed route answers 503.",
                        attempt, ex.GetBaseException().Message, RetryDelay.TotalSeconds);
                    await DelaySafe(ct);
                }
            }
        }

        private static async Task DelaySafe(CancellationToken ct)
        {
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
