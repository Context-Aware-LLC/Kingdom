using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Ranking
{
    public sealed class PostgresRatingStore : IRatingStore
    {
        private readonly string _connString;

        public PostgresRatingStore(string rawConnString, int defaultRating = 1_000)
        {
            if (defaultRating <= 0) throw new ArgumentOutOfRangeException(nameof(defaultRating));
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
            DefaultRating = defaultRating;
        }

        public int DefaultRating { get; }

        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                return null;
            }, ct);

        public Task<int> GetRatingAsync(AccountId account, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "select rating from ladder_player where player_id=$1", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, account.Value));
                object? value = await cmd.ExecuteScalarAsync(ct);
                return value is null or DBNull ? DefaultRating : Convert.ToInt32(value);
            }, ct);

        public Task<PlayerStanding?> GetStandingAsync(AccountId account, CancellationToken ct = default)
            => WithConnectionAsync<PlayerStanding?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "select rating, games_played from ladder_player where player_id=$1", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, account.Value));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;

                // ApplyAsync inserts the row and counts the match in one transaction, so a row
                // with no games is not reachable from the game. Answer it as unrated anyway: a
                // hand-inserted row should not read as a standing somebody played for.
                long games = reader.GetInt64(1);
                return games > 0 ? new PlayerStanding(reader.GetInt32(0), games) : null;
            }, ct);

        public Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
            int limit,
            CancellationToken ct = default)
        {
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

            return WithConnectionAsync<IReadOnlyList<LeaderboardEntry>>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "select row_number() over (order by rating desc, wins desc, games_played, player_id)::int, " +
                    "display_name, rating, games_played, wins, losses, draws " +
                    "from ladder_player " +
                    "order by rating desc, wins desc, games_played, player_id limit $1", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Integer, limit));

                var rows = new List<LeaderboardEntry>(limit);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new LeaderboardEntry(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)));
                }
                return rows;
            }, ct);
        }

        public Task<RatingChange?> ApplyAsync(RankedMatchResult result, CancellationToken ct = default)
        {
            if (!result.IsEligible) return Task.FromResult<RatingChange?>(null);

            return WithConnectionAsync(async conn =>
            {
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

                // Claim the match first. A concurrent or retried application blocks here until the
                // winner commits, then sees the unique conflict and exits without touching players.
                await using (var claim = new NpgsqlCommand(
                    "insert into ladder_match " +
                    "(match_id, ended_at, p0_player_id, p1_player_id, outcome) " +
                    "values ($1,$2,$3,$4,$5) on conflict (match_id) do nothing returning match_id", conn, tx))
                {
                    claim.Parameters.Add(P(NpgsqlDbType.Text, result.Match.Value));
                    claim.Parameters.Add(P(NpgsqlDbType.TimestampTz, result.EndedAtUtc.UtcDateTime));
                    claim.Parameters.Add(P(NpgsqlDbType.Text, result.P0.Value));
                    claim.Parameters.Add(P(NpgsqlDbType.Text, result.P1.Value));
                    claim.Parameters.Add(P(NpgsqlDbType.Text, OutcomeToDb(result.Outcome)));
                    object? claimed = await claim.ExecuteScalarAsync(ct);
                    if (claimed is null)
                    {
                        await tx.RollbackAsync(ct);
                        return null;
                    }
                }

                await EnsurePlayerAsync(conn, tx, result.P0, result.P0DisplayName, result.EndedAtUtc, ct);
                await EnsurePlayerAsync(conn, tx, result.P1, result.P1DisplayName, result.EndedAtUtc, ct);

                // Both rows are locked in the same lexical order in every match, eliminating the
                // p0/p1 inversion that otherwise deadlocks two simultaneous results.
                var standings = new Dictionary<string, (int rating, long games)>(StringComparer.Ordinal);
                await using (var read = new NpgsqlCommand(
                    "select player_id, rating, games_played from ladder_player " +
                    "where player_id in ($1,$2) order by player_id for update", conn, tx))
                {
                    read.Parameters.Add(P(NpgsqlDbType.Text, result.P0.Value));
                    read.Parameters.Add(P(NpgsqlDbType.Text, result.P1.Value));
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        standings.Add(reader.GetString(0), (reader.GetInt32(1), reader.GetInt64(2)));
                }

                var p0 = standings[result.P0.Value];
                var p1 = standings[result.P1.Value];
                (int p0Delta, int p1Delta) = EloRating.Calculate(
                    p0.rating, p0.games, p1.rating, p1.games, result.Outcome);

                await UpdatePlayerAsync(conn, tx, result.P0, result.P0DisplayName,
                    p0.rating + p0Delta, ResultFor(0, result.Outcome), result.EndedAtUtc, ct);
                await UpdatePlayerAsync(conn, tx, result.P1, result.P1DisplayName,
                    p1.rating + p1Delta, ResultFor(1, result.Outcome), result.EndedAtUtc, ct);

                await using (var audit = new NpgsqlCommand(
                    "update ladder_match set p0_rating_before=$1, p0_rating_after=$2, " +
                    "p1_rating_before=$3, p1_rating_after=$4 where match_id=$5", conn, tx))
                {
                    audit.Parameters.Add(P(NpgsqlDbType.Integer, p0.rating));
                    audit.Parameters.Add(P(NpgsqlDbType.Integer, p0.rating + p0Delta));
                    audit.Parameters.Add(P(NpgsqlDbType.Integer, p1.rating));
                    audit.Parameters.Add(P(NpgsqlDbType.Integer, p1.rating + p1Delta));
                    audit.Parameters.Add(P(NpgsqlDbType.Text, result.Match.Value));
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return new RatingChange(
                    result.Match.Value,
                    p0.rating,
                    p0.rating + p0Delta,
                    p1.rating,
                    p1.rating + p1Delta);
            }, ct);
        }

        private async Task EnsurePlayerAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            AccountId account,
            string displayName,
            DateTimeOffset at,
            CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "insert into ladder_player " +
                "(player_id, display_name, rating, games_played, wins, losses, draws, updated_at) " +
                "values ($1,$2,$3,0,0,0,0,$4) on conflict (player_id) do nothing", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, account.Value));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, InMemoryRatingStore.NormalizeName(displayName)));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, DefaultRating));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, at.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task UpdatePlayerAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            AccountId account,
            string displayName,
            int rating,
            SeatResult result,
            DateTimeOffset at,
            CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "update ladder_player set display_name=$1, rating=$2, games_played=games_played+1, " +
                "wins=wins+$3, losses=losses+$4, draws=draws+$5, updated_at=$6 where player_id=$7", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, InMemoryRatingStore.NormalizeName(displayName)));
            cmd.Parameters.Add(P(NpgsqlDbType.Integer, rating));
            cmd.Parameters.Add(P(NpgsqlDbType.Bigint, result == SeatResult.Win ? 1L : 0L));
            cmd.Parameters.Add(P(NpgsqlDbType.Bigint, result == SeatResult.Loss ? 1L : 0L));
            cmd.Parameters.Add(P(NpgsqlDbType.Bigint, result == SeatResult.Draw ? 1L : 0L));
            cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, at.UtcDateTime));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, account.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task<T> WithConnectionAsync<T>(
            Func<NpgsqlConnection, Task<T>> work,
            CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connString);
                    await conn.OpenAsync(ct);
                    return await work(conn);
                }
                catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsBrokenConnection(ex))
                {
                    Console.Error.WriteLine(
                        "[ranking] transient DB fault ({0}) — retrying once with a fresh connection.",
                        ex.GetBaseException().Message);
                }
            }
        }

        private static bool IsBrokenConnection(Exception ex)
        {
            if (ex is NpgsqlException { IsTransient: true }) return true;
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is System.Net.Sockets.SocketException or System.IO.IOException) return true;
            return false;
        }

        private static SeatResult ResultFor(int seat, MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Player0Win => seat == 0 ? SeatResult.Win : SeatResult.Loss,
            MatchOutcome.Player1Win => seat == 1 ? SeatResult.Win : SeatResult.Loss,
            _ => SeatResult.Draw,
        };

        private static string OutcomeToDb(MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Player0Win => "p0_win",
            MatchOutcome.Player1Win => "p1_win",
            _ => "draw",
        };

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
