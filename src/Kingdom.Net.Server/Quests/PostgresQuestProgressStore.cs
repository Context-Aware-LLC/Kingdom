using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Net.Server.Quests
{
    /// <summary>
    /// The durable quest progress store, modelled on <see cref="PostgresEntitlementLedger"/> — the
    /// same connection-string normalization, the same shared <c>schema.sql</c>, and the same
    /// idempotency posture: a natural primary key plus <c>ON CONFLICT DO NOTHING</c>, so two servers
    /// racing the same finished match insert once between them.
    ///
    /// <para>It shares the accounts database because quest progress is keyed on the same
    /// <c>player_id</c> the ledger is, and the payout it leads to is a row in that ledger.</para>
    /// </summary>
    public sealed class PostgresQuestProgressStore : IQuestProgressStore
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresQuestProgressStore(string rawConnString)
            => _connString = TelemetryDrain.NormalizeConnString(rawConnString);

        /// <summary>Apply the canonical accounts DDL idempotently. Call once before use.</summary>
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<QuestCreditResult> CreditAsync(QuestCredit credit, CancellationToken ct = default)
        {
            if (credit.Credit <= 0)
                return new QuestCreditResult(false, await ProgressOfAsync(credit, ct).ConfigureAwait(false));

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

            // TWO STATEMENTS, NOT A DATA-MODIFYING CTE. The obvious single statement — insert in a
            // CTE, sum in the outer select — silently returns a total that EXCLUDES the row just
            // inserted: Postgres runs every part of a statement against the same snapshot, so the
            // CTE's write is not visible to the select beside it. The quest would then complete one
            // match late, every time. They share a transaction so the sum still cannot see a
            // half-applied concurrent credit.
            await using var insert = new NpgsqlCommand(
                "insert into quest_progress " +
                "  (player_id, quest_id, quest_day, match_id, credit, credited_at) " +
                "values ($1,$2,$3,$4,$5,$6) " +
                "on conflict (player_id, quest_id, quest_day, match_id) do nothing", conn, tx);
            insert.Parameters.Add(P(NpgsqlDbType.Text, credit.PlayerId));
            insert.Parameters.Add(P(NpgsqlDbType.Text, credit.QuestId));
            insert.Parameters.Add(P(NpgsqlDbType.Date, credit.Day.ToDateTime(TimeOnly.MinValue)));
            insert.Parameters.Add(P(NpgsqlDbType.Text, credit.MatchId));
            insert.Parameters.Add(P(NpgsqlDbType.Integer, credit.Credit));
            insert.Parameters.Add(P(NpgsqlDbType.TimestampTz, credit.CreditedAt.UtcDateTime));

            // ON CONFLICT DO NOTHING reports 1 row on insert, 0 on a replay.
            bool applied = await insert.ExecuteNonQueryAsync(ct) == 1;

            await using var sum = new NpgsqlCommand(
                "select coalesce(sum(credit), 0)::int from quest_progress " +
                "where player_id = $1 and quest_id = $2 and quest_day = $3", conn, tx);
            sum.Parameters.Add(P(NpgsqlDbType.Text, credit.PlayerId));
            sum.Parameters.Add(P(NpgsqlDbType.Text, credit.QuestId));
            sum.Parameters.Add(P(NpgsqlDbType.Date, credit.Day.ToDateTime(TimeOnly.MinValue)));

            object? total = await sum.ExecuteScalarAsync(ct);
            await tx.CommitAsync(ct);
            return new QuestCreditResult(applied, total is int n ? n : 0);
        }

        public async Task<IReadOnlyDictionary<(string QuestId, DateOnly Day), int>> ProgressAsync(
            string playerId,
            IReadOnlyList<(string QuestId, DateOnly Day)> quests,
            CancellationToken ct = default)
        {
            var acc = new Dictionary<(string, DateOnly), int>();
            if (quests.Count == 0) return acc;

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            // One statement for the whole active set rather than a query per quest: three round
            // trips on a page load that already blocks the menu is three too many.
            DateOnly oldest = quests[0].Day;
            foreach ((string _, DateOnly day) in quests) if (day < oldest) oldest = day;

            await using var cmd = new NpgsqlCommand(
                "select quest_id, quest_day, coalesce(sum(credit), 0)::int from quest_progress " +
                "where player_id = $1 and quest_day >= $2 group by quest_id, quest_day", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Date, oldest.ToDateTime(TimeOnly.MinValue)));

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                acc[(reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)))] = reader.GetInt32(2);

            foreach ((string questId, DateOnly day) in quests)
                if (!acc.ContainsKey((questId, day))) acc[(questId, day)] = 0;

            return acc;
        }

        private async Task<int> ProgressOfAsync(QuestCredit credit, CancellationToken ct)
        {
            IReadOnlyDictionary<(string, DateOnly), int> rows = await ProgressAsync(
                credit.PlayerId,
                new[] { (credit.QuestId, credit.Day) },
                ct).ConfigureAwait(false);
            return rows.TryGetValue((credit.QuestId, credit.Day), out int n) ? n : 0;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value };
    }
}
