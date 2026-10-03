using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The durable account store (EF-072), modelled on <see cref="TelemetryDrain"/>. Get-or-create is
    /// race-safe <i>by construction</i>: <see cref="AccountIdentity.DerivePlayerId"/> is deterministic,
    /// so the player id is computed before the insert, and
    /// <c>ON CONFLICT (credential_kind, credential_hash) DO NOTHING</c> followed by a read-back means
    /// concurrent first-sights converge on the one row (both computed the same id anyway).
    /// </summary>
    public sealed class PostgresAccountStore : IAccountStore
    {
        private readonly string _connString;
        private readonly ITimeSource _time;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        /// <param name="time">Stamps <c>created_at</c> on first sight (server clock; never the core's).</param>
        public PostgresAccountStore(string rawConnString, ITimeSource time)
        {
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
            _time = time;
        }

        /// <summary>
        /// Open a pooled connection, run <paramref name="work"/>, and retry ONCE from scratch if the
        /// attempt died on a broken connection.
        ///
        /// <para>WHY THIS EXISTS. Npgsql pools connectors, and this server runs with
        /// <c>auto_stop_machines = "suspend"</c> (fly.toml): Fly snapshots RAM when the lobby goes
        /// idle and restores it on the next request. The pool comes back believing its sockets are
        /// live — but the far end closed them minutes ago, so the first query after a resume is
        /// issued down a dead socket and fails with <c>Connection reset by peer</c>. Npgsql discards
        /// the broken connector as it fails, so one retry gets a genuinely fresh one. Idle pools
        /// going stale is normal for any pooled client; this deployment merely guarantees it.</para>
        ///
        /// <para>Cost of not having it, 2026-08-18: this killed the FIRST match join after every
        /// idle window. The exception escaped <c>MatchGate.HandleFreshAsync</c>, took the WebSocket
        /// down with it, and reached the player as <c>DeadReason.Forfeited</c> — i.e. as a
        /// matchmaking failure, which is where the debugging went. The database never appeared in
        /// the client's account of it.</para>
        ///
        /// <para>PRECONDITION: every caller must be idempotent, because a retry re-runs the whole
        /// unit. Every operation here is — the upsert is <c>ON CONFLICT DO NOTHING</c>, the kind,
        /// onboarded and last-seen updates are scalar assignments of a value fixed before the
        /// first attempt, and the rest are reads.</para>
        /// </summary>
        private async Task<T> WithConnectionAsync<T>(
            Func<NpgsqlConnection, Task<T>> work, CancellationToken ct)
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
                        "[accounts] transient DB fault on a pooled connection ({0}) — retrying once with a fresh one.",
                        ex.GetBaseException().Message);
                }
            }
        }

        /// <summary>A dead pooled socket rather than a query the database rejected. Npgsql's own
        /// <c>IsTransient</c> covers it plus server-side retryables (deadlock, serialization
        /// failure); the cause walk catches the resume case explicitly, which arrives as
        /// NpgsqlException → IOException → SocketException(104).</summary>
        private static bool IsBrokenConnection(Exception ex)
        {
            if (ex is NpgsqlException { IsTransient: true }) return true;
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is System.Net.Sockets.SocketException or System.IO.IOException) return true;
            return false;
        }

        /// <summary>Apply the canonical DDL idempotently. Call once before use (mirrors the drain).</summary>
        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                return null;
            }, ct);

        public Task<Account> ResolveOrCreateAsync(string credentialKind, string credentialHash,
            string? email = null, CancellationToken ct = default)
        {
            InMemoryAccountStore.Require(credentialKind, credentialHash);
            string playerId = AccountIdentity.DerivePlayerId(credentialKind, credentialHash);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

            return WithConnectionAsync(async conn =>
            {
                await using (var ins = new NpgsqlCommand(
                    "insert into account (player_id, credential_kind, credential_hash, email, created_at) " +
                    "values ($1,$2,$3,$4,$5) on conflict (credential_kind, credential_hash) do nothing", conn))
                {
                    ins.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, credentialKind));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, credentialHash));
                    ins.Parameters.Add(P(NpgsqlDbType.Text, (object?)email ?? DBNull.Value));
                    ins.Parameters.Add(P(NpgsqlDbType.TimestampTz, createdAt.UtcDateTime));
                    await ins.ExecuteNonQueryAsync(ct);
                }

                // Read the authoritative row — ours if we won the insert, the pre-existing one otherwise.
                await using var sel = new NpgsqlCommand(
                    "select " + AccountColumns + " from account " +
                    "where credential_kind=$1 and credential_hash=$2", conn);
                sel.Parameters.Add(P(NpgsqlDbType.Text, credentialKind));
                sel.Parameters.Add(P(NpgsqlDbType.Text, credentialHash));
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException("account row missing immediately after upsert — this should be impossible.");
                return ReadAccount(r);
            }, ct);
        }

        public Task<Account?> FindByPlayerIdAsync(string playerId, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "select " + AccountColumns + " from account where player_id=$1", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return (Account?)null;
                return ReadAccount(r);
            }, ct);

        public async Task<Account?> SetKindAsync(string playerId, AccountKind kind, CancellationToken ct = default)
        {
            bool found = await WithConnectionAsync(async conn =>
            {
                await using var upd = new NpgsqlCommand(
                    "update account set kind=$1 where player_id=$2", conn);
                upd.Parameters.Add(P(NpgsqlDbType.Text, KindToDb(kind)));
                upd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                return await upd.ExecuteNonQueryAsync(ct) != 0;
            }, ct);

            if (!found) return null;
            return await FindByPlayerIdAsync(playerId, ct);
        }

        public async Task<Account?> MarkOnboardedAsync(string playerId, CancellationToken ct = default)
        {
            // No `and onboarded = false` guard: the update is already one-way (nothing sets it back)
            // and idempotent, and adding the guard would make a second call report "no such account"
            // for a player who is simply already onboarded — the caller cannot tell those apart.
            bool found = await WithConnectionAsync(async conn =>
            {
                await using var upd = new NpgsqlCommand(
                    "update account set onboarded=true where player_id=$1", conn);
                upd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                return await upd.ExecuteNonQueryAsync(ct) != 0;
            }, ct);

            if (!found) return null;
            return await FindByPlayerIdAsync(playerId, ct);
        }

        public Task<Account?> MarkSeenAsync(string playerId, CancellationToken ct = default)
        {
            // Read outside the retry, so a retried attempt writes the same instant.
            var seenAt = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

            return WithConnectionAsync(async conn =>
            {
                await using var upd = new NpgsqlCommand(
                    "update account set last_seen=$1 where player_id=$2 returning " + AccountColumns, conn);
                upd.Parameters.Add(P(NpgsqlDbType.TimestampTz, seenAt.UtcDateTime));
                upd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                await using var r = await upd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return (Account?)null;
                return ReadAccount(r);
            }, ct);
        }

        /// <summary>The select list <see cref="ReadAccount"/> reads by ordinal. One copy, so a new
        /// column cannot be added to one query and missed in another.</summary>
        private const string AccountColumns =
            "player_id, credential_kind, credential_hash, email, created_at, kind, onboarded, last_seen";

        private static Account ReadAccount(NpgsqlDataReader r)
            => new Account(r.GetString(0), r.GetString(1), r.GetString(2),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)),
                r.IsDBNull(3) ? null : r.GetString(3),
                KindFromDb(r.IsDBNull(5) ? "standard" : r.GetString(5)),
                !r.IsDBNull(6) && r.GetBoolean(6),
                r.IsDBNull(7) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)));

        private static string KindToDb(AccountKind kind) => kind switch
        {
            AccountKind.Superuser => "superuser",
            _ => "standard",
        };

        private static AccountKind KindFromDb(string kind) => kind switch
        {
            "superuser" => AccountKind.Superuser,
            _ => AccountKind.Standard,
        };

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
