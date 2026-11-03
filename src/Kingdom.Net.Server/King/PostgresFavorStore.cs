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
                "select display_name, score, player_id from favor_score where reign = $1 " +
                "order by score " + (lowerIsBetter ? "asc" : "desc") + ", reached_at asc, player_id collate \"C\" asc " +
                "limit $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, Math.Max(0, limit)));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            int place = 0;
            while (await r.ReadAsync(ct)) rows.Add(new FavorRow(++place, r.GetString(0), r.GetInt32(1), r.GetString(2)));
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

        public async Task<IReadOnlyList<int>> ReignsPlayedAsync(string playerId, CancellationToken ct = default)
        {
            // One row per (reign, player) is the table's key, so no DISTINCT is needed.
            var reigns = new List<int>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select reign from favor_score where player_id = $1 order by reign", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) reigns.Add(r.GetInt32(0));
            return reigns;
        }

        public async Task<IReadOnlyList<UnpaidReign>> UnpaidReignsAsync(int beforeReign, CancellationToken ct = default)
        {
            // generate_series(1, 0) is empty, so a sweep before Reign 1 closes lists nothing.
            var reigns = new List<UnpaidReign>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select g.reign, coalesce(k.struck, false) from generate_series(1, $1 - 1) as g(reign) " +
                "left join king_reign k on k.reign = g.reign " +
                "where k.paid_at is null order by g.reign", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, beforeReign));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) reigns.Add(new UnpaidReign(r.GetInt32(0), r.GetBoolean(1)));
            return reigns;
        }

        public async Task<bool> HasHeldPlaceAsync(string playerId, int place, int beforeReign, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select exists (select 1 from favor_prize where player_id = $1 and place = $2 and reign < $3)", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, place));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, beforeReign));
            return await cmd.ExecuteScalarAsync(ct) is true;
        }

        public async Task RecordPrizeAsync(FavorPrize prize, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into favor_prize (reign, player_id, place, gold, back_id, choice_open) " +
                "values ($1, $2, $3, $4, $5, $6) on conflict (reign, player_id) do nothing", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, prize.Reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, prize.PlayerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, prize.Place));
            cmd.Parameters.Add(P(NpgsqlDbType.Bigint, prize.Gold));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, (object?)prize.BackId ?? DBNull.Value));
            cmd.Parameters.Add(P(NpgsqlDbType.Boolean, prize.ChoiceOpen));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task ClosePaidAsync(int reign, string? favoriteId, string? favoriteName, CancellationToken ct = default)
        {
            // struck is not in the update list, so a strike written before the close survives it;
            // the WHERE keeps a paid reign's record from being written twice.
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into king_reign (reign, favorite_id, favorite_name, paid_at) values ($1, $2, $3, now()) " +
                "on conflict (reign) do update set favorite_id = excluded.favorite_id, " +
                "favorite_name = excluded.favorite_name, paid_at = excluded.paid_at " +
                "where king_reign.paid_at is null", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, (object?)favoriteId ?? DBNull.Value));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, (object?)favoriteName ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<FavorPrize?> PrizeAsync(int reign, string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select reign, player_id, place, gold, back_id, choice_open from favor_prize " +
                "where reign = $1 and player_id = $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? ReadPrize(r) : null;
        }

        public async Task<IReadOnlyList<FavorPrize>> UnsettledBacksAsync(int throughPlace, CancellationToken ct = default)
        {
            var rows = new List<FavorPrize>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select p.reign, p.player_id, p.place, p.gold, p.back_id, p.choice_open " +
                "from favor_prize p join king_reign k on k.reign = p.reign " +
                "where p.back_settled_at is null and p.place <= $1 and k.paid_at is not null " +
                "and not (k.struck and p.place = 1) " +
                "order by p.reign, p.place", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, throughPlace));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadPrize(r));
            return rows;
        }

        public async Task SettleBackAsync(int reign, string playerId, int? backId, bool choiceOpen, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "update favor_prize set back_id = $3, choice_open = $4, back_settled_at = now() " +
                "where reign = $1 and player_id = $2 and back_settled_at is null", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, (object?)backId ?? DBNull.Value));
            cmd.Parameters.Add(P(NpgsqlDbType.Boolean, choiceOpen));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<FavorPrize>> OpenChoicesAsync(string playerId, CancellationToken ct = default)
        {
            var rows = new List<FavorPrize>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select reign, player_id, place, gold, back_id, choice_open from favor_prize " +
                "where player_id = $1 and choice_open order by reign", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadPrize(r));
            return rows;
        }

        public async Task<bool> CloseChoiceAsync(int reign, string playerId, int? backId, CancellationToken ct = default)
        {
            // The WHERE is the race guard: a second close waits on the first's row lock, re-reads
            // choice_open as false, and updates nothing.
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "update favor_prize set choice_open = false, back_id = $3, choice_grant_pending = ($3 is not null) " +
                "where reign = $1 and player_id = $2 and choice_open", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, (object?)backId ?? DBNull.Value));
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }

        public async Task<IReadOnlyList<FavorPrize>> UndeliveredChoicesAsync(string? playerId, CancellationToken ct = default)
        {
            var rows = new List<FavorPrize>();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select reign, player_id, place, gold, back_id, choice_open from favor_prize " +
                "where choice_grant_pending and ($1::text is null or player_id = $1) order by reign, place, player_id collate \"C\"", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, (object?)playerId ?? DBNull.Value));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadPrize(r));
            return rows;
        }

        public async Task MarkChoiceDeliveredAsync(int reign, string playerId, CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "update favor_prize set choice_grant_pending = false where reign = $1 and player_id = $2", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, reign));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>Reads the six columns reign, player_id, place, gold, back_id, choice_open, in that order.</summary>
        private static FavorPrize ReadPrize(NpgsqlDataReader r)
            => new FavorPrize(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), r.GetBoolean(5));

        private static NpgsqlParameter P(NpgsqlDbType type, object value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
