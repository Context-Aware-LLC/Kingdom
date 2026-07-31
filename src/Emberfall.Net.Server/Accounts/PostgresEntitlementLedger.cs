using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The durable entitlement ledger (EF-072), modelled on <see cref="TelemetryDrain"/>: the same
    /// Npgsql handling, the same Supabase-URI normalization
    /// (<see cref="TelemetryDrain.NormalizeConnString"/>), and the same idempotency posture — a UNIQUE
    /// natural key plus <c>ON CONFLICT DO NOTHING</c>, so a replayed receipt inserts once and the
    /// balance moves exactly once.
    ///
    /// <para><b>Correctness lives in the <c>external_txn_id</c> primary key</b>, not in application
    /// logic: even two servers racing the same receipt insert once between them. <see cref="GrantAsync"/>
    /// reports which side of that race it was on via <see cref="GrantOutcome"/>.</para>
    ///
    /// <para>Reuses the drain's connection-string parser rather than duplicating that hand-rolled,
    /// unit-tested logic, so a generated Supabase password with unescaped <c>:</c>/<c>@</c> is handled
    /// identically here.</para>
    /// </summary>
    public sealed class PostgresEntitlementLedger : IEntitlementLedger
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresEntitlementLedger(string rawConnString)
            => _connString = TelemetryDrain.NormalizeConnString(rawConnString);

        /// <summary>Apply the canonical DDL idempotently. Call once before use (mirrors the drain).</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(grant.ExternalTxnId))
                throw new ArgumentException(
                    "A grant must carry a non-empty external transaction id; it is the idempotency key.",
                    nameof(grant));

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into entitlement_grant (external_txn_id, player_id, item, qty, source, created_at) " +
                "values ($1,$2,$3,$4,$5,$6) on conflict (external_txn_id) do nothing", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, grant.ExternalTxnId));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, grant.PlayerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, grant.Item));
            cmd.Parameters.Add(P(NpgsqlDbType.Bigint, grant.Qty));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, grant.Source));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, grant.CreatedAt.UtcDateTime));

            // ON CONFLICT DO NOTHING reports 1 row on insert, 0 on a conflicting replay.
            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return new GrantResult(rows == 1 ? GrantOutcome.Applied : GrantOutcome.Duplicate);
        }

        public async Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select coalesce(sum(qty),0) from entitlement_grant where player_id=$1 and item=$2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, item));
            object? result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result);
        }

        public async Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select item, sum(qty) from entitlement_grant where player_id=$1 group by item", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));

            var acc = new Dictionary<string, long>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                acc[r.GetString(0)] = r.GetInt64(1);
            return acc;
        }

        public async Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select player_id, item, qty, source, external_txn_id, created_at from entitlement_grant " +
                "where player_id=$1 order by created_at, external_txn_id", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));

            var rows = new List<EntitlementGrant>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new EntitlementGrant(
                    r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                    new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc))));
            return rows;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
