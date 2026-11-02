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
    /// The durable <see cref="ICompletedMatchStore"/>, on the accounts database beside
    /// <c>quest_progress</c>: same shared <c>schema.sql</c>, same natural key plus
    /// <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    public sealed class PostgresCompletedMatchStore : ICompletedMatchStore
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresCompletedMatchStore(string rawConnString)
            => _connString = TelemetryDrain.NormalizeConnString(rawConnString);

        public async Task RecordAsync(string playerId, string matchId, DateTimeOffset endedAtUtc, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "insert into completed_match (player_id, match_id, ended_at) values ($1,$2,$3) " +
                "on conflict (player_id, match_id) do nothing", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, matchId));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, endedAtUtc.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<string>> FirstMatchIdsAsync(string playerId, int limit, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "select match_id from completed_match where player_id=$1 " +
                "order by ended_at, match_id limit $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, limit));
            var ids = new List<string>(limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));
            return ids;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
