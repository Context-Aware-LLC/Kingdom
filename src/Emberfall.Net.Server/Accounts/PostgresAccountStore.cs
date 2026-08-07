using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The durable account store (EF-072), modelled on <see cref="TelemetryDrain"/>. Get-or-create is
    /// race-safe <i>by construction</i>: <see cref="AccountIdentity.DerivePlayerId"/> is deterministic,
    /// so the player id is computed before the insert, and
    /// <c>ON CONFLICT (credential_kind, credential_hash) DO NOTHING</c> followed by a read-back means
    /// concurrent first-sights converge on the one row (both computed the same id anyway).
    /// </summary>
    public sealed class PostgresAccountStore : IAccountStore
    {
        private readonly string _connString;
        private readonly ITimeSource _time;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        /// <param name="time">Stamps <c>created_at</c> on first sight (server clock; never the core's).</param>
        public PostgresAccountStore(string rawConnString, ITimeSource time)
        {
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
            _time = time;
        }

        /// <summary>Apply the canonical DDL idempotently. Call once before use (mirrors the drain).</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<Account> ResolveOrCreateAsync(string credentialKind, string credentialHash,
            string? email = null, CancellationToken ct = default)
        {
            InMemoryAccountStore.Require(credentialKind, credentialHash);
            string playerId = AccountIdentity.DerivePlayerId(credentialKind, credentialHash);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using (var ins = new NpgsqlCommand(
                "insert into account (player_id, credential_kind, credential_hash, email, created_at) " +
                "values ($1,$2,$3,$4,$5) on conflict (credential_kind, credential_hash) do nothing", conn))
            {
                ins.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                ins.Parameters.Add(P(NpgsqlDbType.Text, credentialKind));
                ins.Parameters.Add(P(NpgsqlDbType.Text, credentialHash));
                ins.Parameters.Add(P(NpgsqlDbType.Text, (object?)email ?? DBNull.Value));
                ins.Parameters.Add(P(NpgsqlDbType.TimestampTz, createdAt.UtcDateTime));
                await ins.ExecuteNonQueryAsync(ct);
            }

            // Read the authoritative row — ours if we won the insert, the pre-existing one otherwise.
            await using var sel = new NpgsqlCommand(
                "select player_id, credential_kind, credential_hash, email, created_at from account " +
                "where credential_kind=$1 and credential_hash=$2", conn);
            sel.Parameters.Add(P(NpgsqlDbType.Text, credentialKind));
            sel.Parameters.Add(P(NpgsqlDbType.Text, credentialHash));
            await using var r = await sel.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new InvalidOperationException("account row missing immediately after upsert — this should be impossible.");
            return ReadAccount(r);
        }

        public async Task<Account?> FindByPlayerIdAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select player_id, credential_kind, credential_hash, email, created_at from account where player_id=$1", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return ReadAccount(r);
        }

        private static Account ReadAccount(NpgsqlDataReader r)
            => new Account(r.GetString(0), r.GetString(1), r.GetString(2),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)),
                r.IsDBNull(3) ? null : r.GetString(3));

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
