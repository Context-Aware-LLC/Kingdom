using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// Every accounts-DB query the deletion makes. The runbook's accounts table is this class: no table
    /// has a foreign key or a cascade, by design, so every table is named here by hand
    /// (<c>Accounts/schema.sql</c> says a new player-keyed table must be added here in the same commit).
    ///
    /// <para>Run as <c>emberfall_app</c>, the role the server writes as. The Supabase SQL editor's
    /// <c>postgres</c> role gets 42501 on every write to these tables.</para>
    /// </summary>
    internal sealed class PostgresDeletionAccounts : IDeletionAccounts
    {
        /// <summary>The ledger rows the runbook keeps: purchases and refunds, the financial archive and
        /// the replay guard in one.</summary>
        internal const string KeptGrant = "(external_txn_id like 'store:%' or external_txn_id like 'refund:%')";

        private readonly string _conn;

        public PostgresDeletionAccounts(string connString) => _conn = connString;

        public async Task<AccountFacts?> FindAccountAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn,
                "select credential_kind, credential_hash, email, kind from account where player_id = @p",
                ("p", playerId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new AccountFacts(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
        }

        public async Task<long> CountOtherAccountsWithEmailAsync(string email, string playerId, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn,
                "select count(*) from account where lower(trim(email)) = @e and player_id <> @p",
                ("e", email), ("p", playerId));
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        /// <summary>Each count, and the statement that acts on exactly those rows. One list, so the
        /// dry run and the deletion cannot disagree about which rows a table loses.</summary>
        private static readonly (string Key, string Count, string? Act)[] Tables =
        {
            (CountKeys.Account, "from account where player_id = @p", "delete from account where player_id = @p"),
            (CountKeys.GrantDelete, $"from entitlement_grant where player_id = @p and not {KeptGrant}",
                $"delete from entitlement_grant where player_id = @p and not {KeptGrant}"),
            (CountKeys.GrantKept, $"from entitlement_grant where player_id = @p and {KeptGrant}", null),
            (CountKeys.Deck, "from deck where player_id = @p", "delete from deck where player_id = @p"),
            (CountKeys.QuestProgress, "from quest_progress where player_id = @p", "delete from quest_progress where player_id = @p"),
            (CountKeys.CompletedMatch, "from completed_match where player_id = @p", "delete from completed_match where player_id = @p"),
            (CountKeys.AchievementEarned, "from achievement_earned where player_id = @p", "delete from achievement_earned where player_id = @p"),
            (CountKeys.FavorScore, "from favor_score where player_id = @p", "delete from favor_score where player_id = @p"),
            (CountKeys.FavorPrize, "from favor_prize where player_id = @p", "delete from favor_prize where player_id = @p"),
            // The reign is history other people read, so it stays with no Favorite named.
            (CountKeys.KingReign, "from king_reign where favorite_id = @p",
                "update king_reign set favorite_id = null, favorite_name = null where favorite_id = @p"),
            (CountKeys.LadderPlayer, "from ladder_player where player_id = @p", "delete from ladder_player where player_id = @p"),
            // The match row is the opponent's history too, so it stays with the tombstone in our seat.
            (CountKeys.LadderMatch, "from ladder_match where p0_player_id = @p or p1_player_id = @p",
                "update ladder_match set p0_player_id = @t where p0_player_id = @p; " +
                "update ladder_match set p1_player_id = @t where p1_player_id = @p"),
            (CountKeys.FriendCode, "from friend_code where player_id = @p", "delete from friend_code where player_id = @p"),
            (CountKeys.Friendship, "from friendship where player_lo = @p or player_hi = @p",
                "delete from friendship where player_lo = @p or player_hi = @p"),
            (CountKeys.FriendBlock, "from friend_block where blocker = @p or blocked = @p",
                "delete from friend_block where blocker = @p or blocked = @p"),
            (CountKeys.PlayGamesLink, "from play_games_link where player_id = @p", "delete from play_games_link where player_id = @p"),
        };

        /// <summary>The tables this class reaches, for the test that holds it to <c>Accounts/schema.sql</c>.</summary>
        internal static IReadOnlyCollection<string> TableNames =>
            new SortedSet<string>(Array.ConvertAll(Tables, t => t.Count.Split(' ')[1]), StringComparer.Ordinal);

        public async Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var t in Tables)
            {
                await using var cmd = Cmd(conn, "select count(*) " + t.Count, ("p", playerId));
                counts[t.Key] = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            return counts;
        }

        public async Task<bool> ExecuteAsync(string playerId, string tombstoneId, string planSha256, DateTimeOffset at,
            CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // The stamp first: it takes the record's row lock, so a second run racing this one waits
            // here and then finds the step done.
            await using (var stamp = Cmd(conn,
                "update account_deletion set accounts_db_at = @at where plan_sha256 = @h and accounts_db_at is null",
                ("at", at), ("h", planSha256)))
            {
                stamp.Transaction = tx;
                if (await stamp.ExecuteNonQueryAsync(ct) != 1)
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }
            }

            foreach (var t in Tables)
            {
                if (t.Act is null) continue;
                await using var cmd = Cmd(conn, t.Act, ("p", playerId), ("t", tombstoneId));
                cmd.Transaction = tx;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        }

        public async Task<bool> RecordTableExistsAsync(CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn, "select to_regclass('account_deletion') is not null");
            return (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<DeletionRecord?> GetRecordAsync(string planSha256, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn,
                "select plan_sha256, request_message_id, request_received_at, tombstone_id, started_at, " +
                "auth_delete_started_at, auth_deleted_at, accounts_db_at, telemetry_db_at, prereg_at, " +
                "completed_at, archive_expires_on from account_deletion where plan_sha256 = @h",
                ("h", planSha256));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new DeletionRecord(
                r.GetString(0), r.GetString(1), Ts(r, 2)!.Value, r.GetString(3), Ts(r, 4)!.Value,
                Ts(r, 5), Ts(r, 6), Ts(r, 7), Ts(r, 8), Ts(r, 9), Ts(r, 10),
                r.IsDBNull(11) ? null : DateOnly.FromDateTime(r.GetDateTime(11)));
        }

        public async Task<bool> TryInsertRecordAsync(DeletionRecord record, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn,
                "insert into account_deletion (plan_sha256, request_message_id, request_received_at, tombstone_id, started_at) " +
                "values (@h, @m, @rx, @t, @s) on conflict do nothing",
                ("h", record.PlanSha256), ("m", record.RequestMessageId), ("rx", record.RequestReceivedAt),
                ("t", record.TombstoneId), ("s", record.StartedAt));
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }

        public async Task StampAsync(string planSha256, DeletionStep step, DateTimeOffset at, DateOnly? archiveExpiresOn = null,
            CancellationToken ct = default)
        {
            string column = step switch
            {
                DeletionStep.AuthDeleteStarted => "auth_delete_started_at",
                DeletionStep.AuthDeleted => "auth_deleted_at",
                DeletionStep.AccountsDb => "accounts_db_at",
                DeletionStep.TelemetryDb => "telemetry_db_at",
                DeletionStep.Prereg => "prereg_at",
                DeletionStep.Completed => "completed_at",
                _ => throw new ArgumentOutOfRangeException(nameof(step)),
            };
            string sql = $"update account_deletion set {column} = coalesce({column}, @at)" +
                (step == DeletionStep.Completed ? ", archive_expires_on = coalesce(archive_expires_on, @x)" : "") +
                " where plan_sha256 = @h";
            await using var conn = await OpenAsync(ct);
            await using var cmd = Cmd(conn, sql, ("at", at), ("h", planSha256));
            if (step == DeletionStep.Completed)
                cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Date) { Value = archiveExpiresOn!.Value });
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("The deletion record for this plan is missing.");
        }

        private static DateTimeOffset? Ts(NpgsqlDataReader r, int i) =>
            r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));

        private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
        {
            var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync(ct);
            return conn;
        }

        internal static NpgsqlCommand Cmd(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
        {
            var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in ps)
            {
                if (value is DateTimeOffset dto)
                    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = dto.UtcDateTime });
                else if (value is string[] arr)
                    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = arr });
                else
                    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });
            }
            return cmd;
        }
    }
}
