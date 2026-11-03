using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// The durable Favor store, on the shared accounts <c>schema.sql</c>. The better-score rule lives in
    /// one upsert, so two servers recording for the same player between them keep the better one.
    /// </summary>
    public sealed class PostgresFavorStore : IFavorStore
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresFavorStore(string rawConnString)
            => _connString = TelemetryDrain.NormalizeConnString(rawConnString);

        /// <summary>Apply the canonical accounts DDL idempotently. Call once before use.</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<int?> RecordAsync(FavorScore score, bool lowerIsBetter, CancellationToken ct = default)
        {
            // DO UPDATE ... WHERE is strict, so an equal score touches nothing and reached_at stays.
            // The prior best is read in the same statement, from the snapshot before the upsert.
            string beats = lowerIsBetter ? "excluded.score < favor_score.score" : "excluded.score > favor_score.score";
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "with prior as (select score from favor_score where reign = $1 and player_id = $2), " +
                "up as (insert into favor_score (reign, player_id, display_name, score, reached_at) " +
                "values ($1, $2, $3, $4, $5) " +
                "on conflict (reign, player_id) do update set display_name = excluded.display_name, " +
                "score = excluded.score, reached_at = excluded.reached_at " +
                "where " + beats + " returning 1) " +
                "select (select score from prior)", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, score.Reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, score.PlayerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, score.DisplayName));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, score.Score));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, score.ReachedAt.UtcDateTime));

            object? prior = await cmd.ExecuteScalarAsync(ct);
            return prior is int best ? best : (int?)null;
        }

        public async Task<int?> BestAsync(int reign, string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select score from favor_score where reign = $1 and player_id = $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            object? v = await cmd.ExecuteScalarAsync(ct);
            return v is int best ? best : (int?)null;
        }

        public async Task<IReadOnlyList<FavorRow>> TopAsync(int reign, bool lowerIsBetter, int limit, CancellationToken ct = default)
        {
            var rows = new List<FavorRow>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select display_name, score from favor_score where reign = $1 " +
                "order by score " + (lowerIsBetter ? "asc" : "desc") + ", reached_at asc, player_id collate \"C\" asc " +
                "limit $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, Math.Max(0, limit)));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            int place = 0;
            while (await r.ReadAsync(ct)) rows.Add(new FavorRow(++place, r.GetString(0), r.GetInt32(1)));
            return rows;
        }

        public async Task<int?> PlaceAsync(int reign, string playerId, bool lowerIsBetter, CancellationToken ct = default)
        {
            // Rank by the same total order TopAsync sorts by, so the two cannot disagree.
            string ahead = lowerIsBetter ? "o.score < me.score" : "o.score > me.score";
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select 1 + (select count(*) from favor_score o where o.reign = me.reign and (" + ahead +
                " or (o.score = me.score and (o.reached_at < me.reached_at or (o.reached_at = me.reached_at " +
                "and o.player_id collate \"C\" < me.player_id collate \"C\"))))) " +
                "from favor_score me where me.reign = $1 and me.player_id = $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            object? v = await cmd.ExecuteScalarAsync(ct);
            return v is long place ? (int)place : (int?)null;
        }

        public async Task<IReadOnlyList<RollRow>> RollAsync(int limit, CancellationToken ct = default)
        {
            var rows = new List<RollRow>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select reign, case when struck then null else favorite_name end " +
                "from king_reign order by reign desc limit $1", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, Math.Max(0, limit)));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new RollRow(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1)));
            return rows;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
