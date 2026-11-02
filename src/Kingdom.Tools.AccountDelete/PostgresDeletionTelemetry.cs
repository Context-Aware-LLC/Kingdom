using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// Every telemetry-DB query the deletion makes (the runbook's telemetry table). Match data stays
    /// once it no longer identifies the player: the typed seat and card rows keep the tombstone in
    /// place of the account id. Everything that binds a device or session to them goes.
    ///
    /// <para>The account id in telemetry is the store's <c>player_id</c>, the id MatchGate seats a
    /// player under (<c>ClientTelemetryEndpoint</c> resolves the same one), never the raw <c>sub</c>.</para>
    /// </summary>
    internal sealed class PostgresDeletionTelemetry : IDeletionTelemetry
    {
        // A raw line carrying the player's account id, snake_case: hello, client_milestone, queue,
        // queue_refused, first_action.
        private const string RawAccount = "line->>'account_id' = @p";

        // The camelCase id inside the drained match lines. Containment rather than unnesting: a line
        // whose `players` is not an array simply does not match, where jsonb_array_elements would fail.
        private const string RawPlayers =
            "((type in ('match_complete', 'match_void') and line->'players' @> jsonb_build_array(jsonb_build_object('accountId', @p::text)))" +
            " or (type = 'card_seen' and line->>'accountId' = @p))";

        // Rows that name a device and no account (gate_served, crash, a milestone sent before sign-in),
        // for the devices in @d.
        private const string RawDeviceOnly = "line->>'device' = any(@d) and line->>'account_id' is null";

        // The devices in @d that some OTHER account is bound to. Those keep their device-only rows: they
        // describe that person as much as this one.
        private const string SharedDevices =
            "select distinct line->>'device' from raw_event " +
            "where line->>'device' = any(@d) and line->>'account_id' is not null and line->>'account_id' <> @p";

        /// <summary>The tables this class reaches, and the one it leaves alone, for the test that holds
        /// it to <c>Telemetry/schema.sql</c>. <c>match</c> carries no account id.</summary>
        internal static readonly IReadOnlyCollection<string> TableNames = new[] { "card_seen", "match_seat", "raw_event" };
        internal static readonly IReadOnlyCollection<string> Untouched = new[] { "match" };

        private readonly string _conn;

        public PostgresDeletionTelemetry(string connString) => _conn = connString;

        public async Task<IReadOnlyList<string>> BoundDevicesAsync(string playerId, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = PostgresDeletionAccounts.Cmd(conn,
                $"select distinct line->>'device' from raw_event where {RawAccount} and line->>'device' is not null " +
                "order by 1", ("p", playerId));
            return await ReadStringsAsync(cmd, ct);
        }

        public async Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            string[] all = devices.Distinct(StringComparer.Ordinal).ToArray();
            IReadOnlyList<string> shared = await SharedAsync(conn, null, playerId, all, ct);
            string[] own = all.Except(shared, StringComparer.Ordinal).ToArray();

            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [CountKeys.MatchSeat] = await CountAsync(conn, "from match_seat where account_id = @p", playerId, own, ct),
                [CountKeys.CardSeen] = await CountAsync(conn, "from card_seen where account_id = @p", playerId, own, ct),
                [CountKeys.RawAccount] = await CountAsync(conn, $"from raw_event where {RawAccount}", playerId, own, ct),
                [CountKeys.RawPlayers] = await CountAsync(conn, $"from raw_event where {RawPlayers}", playerId, own, ct),
                [CountKeys.RawDeviceOnly] = await CountAsync(conn, $"from raw_event where {RawDeviceOnly}", playerId, own, ct),
                [CountKeys.Devices] = all.Length,
                [CountKeys.DevicesShared] = shared.Count,
            };
            return counts;
        }

        public async Task ExecuteAsync(string playerId, string tombstoneId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // The plan's devices plus any bound since, judged before the account rows that bind them go.
            var all = new HashSet<string>(devices, StringComparer.Ordinal);
            await using (var bound = PostgresDeletionAccounts.Cmd(conn,
                $"select distinct line->>'device' from raw_event where {RawAccount} and line->>'device' is not null",
                ("p", playerId)))
            {
                bound.Transaction = tx;
                foreach (string d in await ReadStringsAsync(bound, ct)) all.Add(d);
            }
            string[] allArr = all.ToArray();
            IReadOnlyList<string> shared = await SharedAsync(conn, tx, playerId, allArr, ct);
            string[] own = allArr.Except(shared, StringComparer.Ordinal).ToArray();

            string[] statements =
            {
                "update match_seat set account_id = @t where account_id = @p",
                "update card_seen set account_id = @t where account_id = @p",
                $"delete from raw_event where {RawDeviceOnly}",
                $"delete from raw_event where {RawAccount}",
                $"delete from raw_event where {RawPlayers}",
            };
            foreach (string sql in statements)
            {
                await using var cmd = PostgresDeletionAccounts.Cmd(conn, sql, ("p", playerId), ("t", tombstoneId), ("d", own));
                cmd.Transaction = tx;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        private static async Task<IReadOnlyList<string>> SharedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
            string playerId, string[] devices, CancellationToken ct)
        {
            if (devices.Length == 0) return Array.Empty<string>();
            await using var cmd = PostgresDeletionAccounts.Cmd(conn, SharedDevices, ("p", playerId), ("d", devices));
            cmd.Transaction = tx;
            return await ReadStringsAsync(cmd, ct);
        }

        private static async Task<long> CountAsync(NpgsqlConnection conn, string from, string playerId, string[] devices,
            CancellationToken ct)
        {
            await using var cmd = PostgresDeletionAccounts.Cmd(conn, "select count(*) " + from, ("p", playerId), ("d", devices));
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        private static async Task<IReadOnlyList<string>> ReadStringsAsync(NpgsqlCommand cmd, CancellationToken ct)
        {
            var list = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
            return list;
        }

        private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
        {
            var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync(ct);
            return conn;
        }
    }
}
