using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// The durable achievement store, modelled on <see cref="Quests.PostgresQuestProgressStore"/>:
    /// the shared <c>schema.sql</c>, a natural primary key and <c>ON CONFLICT DO NOTHING</c>, so two
    /// servers evaluating the same finished match record it once between them.
    /// </summary>
    public sealed class PostgresAchievementStore : IAchievementStore
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresAchievementStore(string rawConnString)
            => _connString = TelemetryDrain.NormalizeConnString(rawConnString);

        /// <summary>Apply the canonical accounts DDL idempotently. Call once before use.</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<string>> EarnAsync(
            string playerId,
            IReadOnlyList<string> achievementIds,
            string matchId,
            DateTimeOffset earnedAt,
            CancellationToken ct = default)
        {
            var recorded = new List<string>();
            if (achievementIds.Count == 0) return recorded;

            var ids = new string[achievementIds.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = achievementIds[i];

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            // One statement for the whole set. RETURNING under ON CONFLICT DO NOTHING yields only the
            // rows this statement inserted, which is exactly "newly earned".
            await using var cmd = new NpgsqlCommand(
                "insert into achievement_earned (player_id, achievement_id, match_id, earned_at) " +
                "select $1, id, $3, $4 from unnest($2::text[]) as id " +
                "on conflict (player_id, achievement_id) do nothing " +
                "returning achievement_id", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Array | NpgsqlDbType.Text, ids));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, matchId));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, earnedAt.UtcDateTime));

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) recorded.Add(reader.GetString(0));
            return recorded;
        }

        public async Task<IReadOnlyList<EarnedAchievement>> ReadAsync(string playerId, CancellationToken ct = default)
        {
            var earned = new List<EarnedAchievement>();

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select achievement_id, match_id, earned_at from achievement_earned " +
                "where player_id = $1 order by earned_at, achievement_id", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                earned.Add(new EarnedAchievement(
                    reader.GetString(0),
                    reader.GetString(1),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc))));
            }
            return earned;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
