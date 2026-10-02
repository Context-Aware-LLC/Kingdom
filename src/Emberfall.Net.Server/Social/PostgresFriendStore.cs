using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Social
{
    /// <summary>
    /// The durable friends store (#409), modelled on <see cref="PostgresAccountStore"/>: same
    /// connection posture, same retry, same embedded <c>schema.sql</c>.
    ///
    /// <para><b>Every write that reads before it writes takes a per-player advisory lock first</b>,
    /// both players' in id order. The caps are counts, and a count followed by an insert is a race
    /// two concurrent requests both win; the lock is what makes the cap a cap. Ordering the two
    /// locks is what keeps A→B and B→A from deadlocking.</para>
    /// </summary>
    public sealed class PostgresFriendStore : IFriendStore
    {
        /// <summary>Namespaces this store's advisory locks away from any other user of them.</summary>
        private const int LockSpace = 409;

        /// <summary>A collision needs two random codes out of 32^9 to match; this bounds a loop
        /// that should never run twice.</summary>
        private const int MintAttempts = 8;

        private readonly string _connString;
        private readonly ITimeSource _time;
        private readonly Func<string> _mint;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresFriendStore(string rawConnString, ITimeSource time)
            : this(rawConnString, time, FriendCodeMint.Next) { }

        /// <param name="mint">The code source — injectable so a test can force a collision.</param>
        internal PostgresFriendStore(string rawConnString, ITimeSource time, Func<string> mint)
        {
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
            _time = time;
            _mint = mint;
        }

        /// <summary>
        /// Open a pooled connection, run <paramref name="work"/>, and retry ONCE from scratch if the
        /// attempt died on a broken connection. See <see cref="PostgresAccountStore"/>'s copy of this
        /// method for the full "why" (Fly suspend/resume killing a pooled socket).
        ///
        /// <para>PRECONDITION: every caller must be idempotent, because a retry re-runs the whole
        /// unit. Every operation here is: a request that already stands answers AlreadyPending, an
        /// accept that already landed answers Accepted, and the rest are filtered deletes and
        /// <c>ON CONFLICT DO NOTHING</c> inserts.</para>
        /// </summary>
        private async Task<T> WithConnectionAsync<T>(
            Func<NpgsqlConnection, Task<T>> work, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connString);
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    return await work(conn).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsBrokenConnection(ex))
                {
                    Console.Error.WriteLine(
                        "[friends] transient DB fault on a pooled connection ({0}) — retrying once with a fresh one.",
                        ex.GetBaseException().Message);
                }
            }
        }

        /// <summary>A dead pooled socket rather than a query the database rejected.</summary>
        private static bool IsBrokenConnection(Exception ex)
        {
            if (ex is NpgsqlException { IsTransient: true }) return true;
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is System.Net.Sockets.SocketException or System.IO.IOException) return true;
            return false;
        }

        /// <summary>Apply the canonical DDL idempotently. Nothing in production calls this —
        /// <c>AccountSchemaInitializer</c> runs the identical <see cref="AccountsSchema.Ddl"/> at
        /// startup; it exists so the contract fixture can stand this store up alone.</summary>
        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }, ct);

        public Task<FriendIdentity> EnsureIdentityAsync(string playerId, CancellationToken ct = default)
            => WithConnectionAsync(conn => EnsureIdentityAsync(conn, playerId, ct), ct);

        private async Task<FriendIdentity> EnsureIdentityAsync(
            NpgsqlConnection conn, string playerId, CancellationToken ct)
        {
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs).UtcDateTime;
            for (int attempt = 0; attempt < MintAttempts; attempt++)
            {
                // `on conflict do nothing` with no target swallows BOTH conflicts: this player
                // already has a code (the read-back finds it), or another player holds the one
                // just minted (the read-back finds nothing, and the loop mints again).
                await using (var ins = new NpgsqlCommand(
                    "insert into friend_code (player_id, code, created_at) values ($1,$2,$3) " +
                    "on conflict do nothing", conn))
                {
                    ins.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, _mint()));
                    ins.Parameters.Add(P(NpgsqlDbType.TimestampTz, createdAt));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using var sel = new NpgsqlCommand(
                    "select code, display_name from friend_code where player_id=$1", conn);
                sel.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                await using var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                    return new FriendIdentity(playerId, r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
            }

            throw new InvalidOperationException(
                $"could not mint a unique friend code in {MintAttempts} attempts — the code source is not random.");
        }

        public Task<string?> FindPlayerIdByCodeAsync(string code, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "select player_id from friend_code where code=$1", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, code));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }, ct);

        public Task SetDisplayNameAsync(string playerId, string displayName, CancellationToken ct = default)
            => WithConnectionAsync<object?>(async conn =>
            {
                await EnsureIdentityAsync(conn, playerId, ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "update friend_code set display_name=$1 where player_id=$2", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, displayName));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }, ct);

        public Task<FriendList> ReadAsync(string playerId, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                FriendIdentity self = await EnsureIdentityAsync(conn, playerId, ct).ConfigureAwait(false);
                var friends = new List<FriendIdentity>();
                var incoming = new List<FriendIdentity>();
                var outgoing = new List<FriendIdentity>();
                var blocked = new List<FriendIdentity>();

                await using (var cmd = new NpgsqlCommand(
                    "select f.status, f.requested_by, c.player_id, c.code, c.display_name " +
                    "from friendship f join friend_code c on c.player_id = " +
                    "  case when f.player_lo=$1 then f.player_hi else f.player_lo end " +
                    "where f.player_lo=$1 or f.player_hi=$1 " +
                    "order by f.created_at, c.code collate \"C\"", conn))
                {
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var who = new FriendIdentity(
                            r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
                        if (r.GetString(0) == "accepted") friends.Add(who);
                        else if (r.GetString(1) == playerId) outgoing.Add(who);
                        else incoming.Add(who);
                    }
                }

                await using (var cmd = new NpgsqlCommand(
                    "select c.player_id, c.code, c.display_name " +
                    "from friend_block b join friend_code c on c.player_id = b.blocked " +
                    "where b.blocker=$1 order by b.created_at, c.code collate \"C\"", conn))
                {
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                        blocked.Add(new FriendIdentity(
                            r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
                }

                return new FriendList(self, friends, incoming, outgoing, blocked);
            }, ct);

        public Task<FriendRequestOutcome> RequestAsync(string fromPlayerId, string toPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            if (fromPlayerId == toPlayerId) return Task.FromResult(FriendRequestOutcome.Refused);
            var (lo, hi) = InMemoryFriendStore.Key(fromPlayerId, toPlayerId);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs).UtcDateTime;

            return WithConnectionAsync(async conn =>
            {
                await EnsureIdentityAsync(conn, fromPlayerId, ct).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await LockPairAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);

                if (!await HasIdentityAsync(conn, tx, toPlayerId, ct).ConfigureAwait(false) ||
                    await EitherBlockedAsync(conn, tx, fromPlayerId, toPlayerId, ct).ConfigureAwait(false))
                    return FriendRequestOutcome.Refused;

                var existing = await FindAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);
                if (existing is { } row)
                {
                    if (row.Accepted) return FriendRequestOutcome.AlreadyFriends;
                    if (row.RequestedBy == fromPlayerId) return FriendRequestOutcome.AlreadyPending;

                    // They asked first, so this is the answer to their request.
                    if ((await CountAsync(conn, tx, fromPlayerId, ct).ConfigureAwait(false)).Friends >= limits.MaxFriends)
                        return FriendRequestOutcome.AtCapacity;
                    if ((await CountAsync(conn, tx, toPlayerId, ct).ConfigureAwait(false)).Friends >= limits.MaxFriends)
                        return FriendRequestOutcome.Refused;
                    await MarkAcceptedAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return FriendRequestOutcome.Accepted;
                }

                var mine = await CountAsync(conn, tx, fromPlayerId, ct).ConfigureAwait(false);
                if (mine.Friends >= limits.MaxFriends || mine.Outgoing >= limits.MaxPendingOutgoing)
                    return FriendRequestOutcome.AtCapacity;
                if ((await CountAsync(conn, tx, toPlayerId, ct).ConfigureAwait(false)).Incoming >= limits.MaxPendingIncoming)
                    return FriendRequestOutcome.Refused;

                await using (var ins = new NpgsqlCommand(
                    "insert into friendship (player_lo, player_hi, status, requested_by, created_at) " +
                    "values ($1,$2,'pending',$3,$4)", conn, tx))
                {
                    ins.Parameters.Add(P(NpgsqlDbType.Text, lo));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, hi));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, fromPlayerId));
                    ins.Parameters.Add(P(NpgsqlDbType.TimestampTz, now));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return FriendRequestOutcome.Sent;
            }, ct);
        }

        public Task<FriendAcceptOutcome> AcceptAsync(string playerId, string requesterPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            if (playerId == requesterPlayerId) return Task.FromResult(FriendAcceptOutcome.NotFound);
            var (lo, hi) = InMemoryFriendStore.Key(playerId, requesterPlayerId);

            return WithConnectionAsync(async conn =>
            {
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await LockPairAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);

                var existing = await FindAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);
                if (existing is not { } row) return FriendAcceptOutcome.NotFound;
                if (row.Accepted) return FriendAcceptOutcome.Accepted;
                if (row.RequestedBy != requesterPlayerId) return FriendAcceptOutcome.NotFound;

                if ((await CountAsync(conn, tx, playerId, ct).ConfigureAwait(false)).Friends >= limits.MaxFriends ||
                    (await CountAsync(conn, tx, requesterPlayerId, ct).ConfigureAwait(false)).Friends >= limits.MaxFriends)
                    return FriendAcceptOutcome.AtCapacity;

                await MarkAcceptedAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return FriendAcceptOutcome.Accepted;
            }, ct);
        }

        public Task<bool> DeclineAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
            => DropAsync(playerId, otherPlayerId, "pending", ct);

        public Task<bool> RemoveAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
            => DropAsync(playerId, otherPlayerId, "accepted", ct);

        private Task<bool> DropAsync(string playerId, string otherPlayerId, string status, CancellationToken ct)
        {
            var (lo, hi) = InMemoryFriendStore.Key(playerId, otherPlayerId);
            return WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "delete from friendship where player_lo=$1 and player_hi=$2 and status=$3", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, lo));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, hi));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, status));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
            }, ct);
        }

        public Task<FriendBlockOutcome> BlockAsync(string playerId, string otherPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            if (playerId == otherPlayerId) return Task.FromResult(FriendBlockOutcome.Blocked);
            var (lo, hi) = InMemoryFriendStore.Key(playerId, otherPlayerId);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs).UtcDateTime;

            return WithConnectionAsync(async conn =>
            {
                await EnsureIdentityAsync(conn, playerId, ct).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await LockPairAsync(conn, tx, lo, hi, ct).ConfigureAwait(false);

                if (!await HasIdentityAsync(conn, tx, otherPlayerId, ct).ConfigureAwait(false))
                    return FriendBlockOutcome.Blocked;

                await using (var count = new NpgsqlCommand(
                    "select count(*) filter (where blocked <> $2), count(*) filter (where blocked = $2) " +
                    "from friend_block where blocker=$1", conn, tx))
                {
                    count.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    count.Parameters.Add(P(NpgsqlDbType.Text, otherPlayerId));
                    await using var r = await count.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await r.ReadAsync(ct).ConfigureAwait(false);
                    if (r.GetInt64(1) == 0 && r.GetInt64(0) >= limits.MaxBlocked)
                        return FriendBlockOutcome.AtCapacity;
                }

                await using (var ins = new NpgsqlCommand(
                    "insert into friend_block (blocker, blocked, created_at) values ($1,$2,$3) " +
                    "on conflict do nothing", conn, tx))
                {
                    ins.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, otherPlayerId));
                    ins.Parameters.Add(P(NpgsqlDbType.TimestampTz, now));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var del = new NpgsqlCommand(
                    "delete from friendship where player_lo=$1 and player_hi=$2", conn, tx))
                {
                    del.Parameters.Add(P(NpgsqlDbType.Text, lo));
                    del.Parameters.Add(P(NpgsqlDbType.Text, hi));
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return FriendBlockOutcome.Blocked;
            }, ct);
        }

        public Task<bool> UnblockAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "delete from friend_block where blocker=$1 and blocked=$2", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, otherPlayerId));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
            }, ct);

        /// <summary>Held to the end of the transaction. <paramref name="lo"/> before
        /// <paramref name="hi"/>, always — that order is the deadlock guard.</summary>
        private static async Task LockPairAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string lo, string hi, CancellationToken ct)
        {
            foreach (string playerId in new[] { lo, hi })
            {
                await using var cmd = new NpgsqlCommand(
                    "select pg_advisory_xact_lock($1, hashtext($2))", conn, tx);
                cmd.Parameters.Add(P(NpgsqlDbType.Integer, LockSpace));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task<bool> HasIdentityAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string playerId, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "select 1 from friend_code where player_id=$1", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }

        private static async Task<bool> EitherBlockedAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string a, string b, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "select 1 from friend_block where (blocker=$1 and blocked=$2) or (blocker=$2 and blocked=$1) limit 1",
                conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, a));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, b));
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }

        private static async Task<(bool Accepted, string RequestedBy)?> FindAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string lo, string hi, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "select status, requested_by from friendship where player_lo=$1 and player_hi=$2", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, lo));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, hi));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
            return (r.GetString(0) == "accepted", r.GetString(1));
        }

        private static async Task<(long Friends, long Outgoing, long Incoming)> CountAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string playerId, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "select count(*) filter (where status='accepted'), " +
                "  count(*) filter (where status='pending' and requested_by=$1), " +
                "  count(*) filter (where status='pending' and requested_by<>$1) " +
                "from friendship where player_lo=$1 or player_hi=$1", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }

        private static async Task MarkAcceptedAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, string lo, string hi, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "update friendship set status='accepted' where player_lo=$1 and player_hi=$2", conn, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Text, lo));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, hi));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
