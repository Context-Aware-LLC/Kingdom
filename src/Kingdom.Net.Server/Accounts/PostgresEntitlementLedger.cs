using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Net.Server.Accounts
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

        public async Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Serialize both retries of one transaction id and distinct spends by one player. The
            // two keys are acquired in a stable order so concurrent purchases cannot deadlock.
            await AdvisoryLockAsync(conn, tx, "batch:" + batch.TransactionId, ct);
            await AdvisoryLockAsync(conn, tx, "player:" + batch.PlayerId, ct);

            var expected = new List<EntitlementGrant>(batch.Deltas.Count);
            for (int i = 0; i < batch.Deltas.Count; i++) expected.Add(batch.GrantAt(i));

            var existing = new List<EntitlementGrant>();
            var placeholders = new List<string>(expected.Count);
            for (int i = 0; i < expected.Count; i++) placeholders.Add("$" + (i + 1));
            await using (var find = new NpgsqlCommand(
                "select player_id, item, qty, source, external_txn_id, created_at " +
                "from entitlement_grant where external_txn_id in (" + string.Join(",", placeholders) + ") " +
                "order by external_txn_id", conn, tx))
            {
                for (int i = 0; i < expected.Count; i++)
                    find.Parameters.Add(P(NpgsqlDbType.Text, expected[i].ExternalTxnId));
                await using var reader = await find.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    existing.Add(new EntitlementGrant(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                        reader.GetString(4),
                        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
            }

            if (existing.Count > 0)
            {
                EntitlementBatchOutcome replayOutcome = SameRows(expected, existing)
                    ? EntitlementBatchOutcome.Duplicate
                    : EntitlementBatchOutcome.Conflict;
                await tx.RollbackAsync(ct);
                return new EntitlementBatchResult(replayOutcome);
            }

            if (batch.Requirement != null)
            {
                await using var balance = new NpgsqlCommand(
                    "select coalesce(sum(qty),0) from entitlement_grant where player_id=$1 and item=$2",
                    conn, tx);
                balance.Parameters.Add(P(NpgsqlDbType.Text, batch.PlayerId));
                balance.Parameters.Add(P(NpgsqlDbType.Text, batch.Requirement.Item));
                long current = Saturate(Convert.ToDecimal(await balance.ExecuteScalarAsync(ct)));
                if (current < batch.Requirement.Minimum)
                {
                    await tx.RollbackAsync(ct);
                    return new EntitlementBatchResult(EntitlementBatchOutcome.InsufficientBalance);
                }
            }

            for (int i = 0; i < expected.Count; i++)
            {
                EntitlementGrant grant = expected[i];
                await using var insert = new NpgsqlCommand(
                    "insert into entitlement_grant (external_txn_id, player_id, item, qty, source, created_at) " +
                    "values ($1,$2,$3,$4,$5,$6)", conn, tx);
                insert.Parameters.Add(P(NpgsqlDbType.Text, grant.ExternalTxnId));
                insert.Parameters.Add(P(NpgsqlDbType.Text, grant.PlayerId));
                insert.Parameters.Add(P(NpgsqlDbType.Text, grant.Item));
                insert.Parameters.Add(P(NpgsqlDbType.Bigint, grant.Qty));
                insert.Parameters.Add(P(NpgsqlDbType.Text, grant.Source));
                insert.Parameters.Add(P(NpgsqlDbType.TimestampTz, grant.CreatedAt.UtcDateTime));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return new EntitlementBatchResult(EntitlementBatchOutcome.Applied);
        }

        public async Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(
            string playerId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player id is required.", nameof(playerId));

            var conn = new NpgsqlConnection(_connString);
            try
            {
                await conn.OpenAsync(ct);
                string key = "player_mutation:" + playerId;
                await using var cmd = new NpgsqlCommand(
                    "select pg_advisory_lock(hashtextextended($1,0))", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, key));
                await cmd.ExecuteNonQueryAsync(ct);
                return new PostgresPlayerMutationLock(conn, key);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
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
            return Saturate(Convert.ToDecimal(result));
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
                acc[r.GetString(0)] = Saturate(r.GetFieldValue<decimal>(1));
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

        /// <summary>
        /// Clamp a <c>sum(qty)</c> into <see cref="long"/> instead of throwing.
        ///
        /// <para><b>Postgres answers <c>sum</c> of a <c>bigint</c> column as unbounded
        /// <c>numeric</c></b>, so the projection has a range the column does not, and
        /// <c>NpgsqlDataReader.GetInt64</c> throws <see cref="OverflowException"/> the moment one
        /// player's total leaves it. That throw travelled all the way out as a 500 on
        /// <c>GET /collection</c> and took the player's entire collection with it, permanently —
        /// every subsequent read hit the same rows (2026-09-04, live; the cause was a 64-bit pack
        /// seed written as a qty, fixed at its write site in
        /// <see cref="BoosterPackGrantService.SeedSourcePrefix"/>).</para>
        ///
        /// <para>Saturating is the right answer even though no legitimate balance can reach it: a
        /// balance that far out is already a ledger bug, and the choice is between one nonsensical
        /// number on one item and losing every other item the player owns. Same posture
        /// <see cref="LedgerOwnedCounts"/> takes on a row it cannot parse.</para>
        /// </summary>
        private static long Saturate(decimal sum)
        {
            if (sum > long.MaxValue) return long.MaxValue;
            if (sum < long.MinValue) return long.MinValue;
            return (long)sum;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };

        private static async Task AdvisoryLockAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            string key,
            CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "select pg_advisory_xact_lock(hashtextextended($1,0))", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, key));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static bool SameRows(
            IReadOnlyList<EntitlementGrant> expected,
            IReadOnlyList<EntitlementGrant> actual)
        {
            if (expected.Count != actual.Count) return false;
            var byId = new Dictionary<string, EntitlementGrant>(StringComparer.Ordinal);
            for (int i = 0; i < actual.Count; i++) byId[actual[i].ExternalTxnId] = actual[i];
            for (int i = 0; i < expected.Count; i++)
            {
                EntitlementGrant wanted = expected[i];
                if (!byId.TryGetValue(wanted.ExternalTxnId, out EntitlementGrant? found)) return false;
                if (found.PlayerId != wanted.PlayerId || found.Item != wanted.Item ||
                    found.Qty != wanted.Qty || found.Source != wanted.Source)
                    return false;
            }
            return true;
        }

        private sealed class PostgresPlayerMutationLock : IAsyncDisposable
        {
            private NpgsqlConnection? _conn;
            private readonly string _key;

            public PostgresPlayerMutationLock(NpgsqlConnection conn, string key)
            {
                _conn = conn;
                _key = key;
            }

            public async ValueTask DisposeAsync()
            {
                NpgsqlConnection? conn = Interlocked.Exchange(ref _conn, null);
                if (conn == null) return;

                try
                {
                    await using var cmd = new NpgsqlCommand(
                        "select pg_advisory_unlock(hashtextextended($1,0))", conn);
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, _key));
                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    await conn.DisposeAsync();
                }
            }
        }
    }
}
