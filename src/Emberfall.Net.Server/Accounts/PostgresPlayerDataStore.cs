using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The durable player-data store — characters and decks, modelled on
    /// <see cref="PostgresAccountStore"/>. Same retry posture, same reasons: this deployment runs
    /// with <c>auto_stop_machines = "suspend"</c>, so the pool's first query after an idle resume can
    /// land on a socket the far end already closed.
    /// </summary>
    public sealed class PostgresPlayerDataStore : IPlayerDataStore
    {
        private readonly string _connString;

        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        public PostgresPlayerDataStore(string rawConnString)
        {
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
        }

        /// <summary>
        /// Open a pooled connection, run <paramref name="work"/>, and retry ONCE from scratch if the
        /// attempt died on a broken connection. See <see cref="PostgresAccountStore"/>'s copy of this
        /// method for the full "why" (Fly suspend/resume killing a pooled socket).
        ///
        /// <para>PRECONDITION: every caller must be idempotent, because a retry re-runs the whole
        /// unit. Every operation here is — both saves are upserts, both deletes are filtered
        /// deletes, and <see cref="DeleteCharacterAsync"/> wraps its two statements in a transaction
        /// so a retry after a half-applied attempt finds nothing left to do. Do not add a
        /// non-idempotent operation without re-reading this comment.</para>
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
                        "[playerdata] transient DB fault on a pooled connection ({0}) — retrying once with a fresh one.",
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

        /// <summary>Apply the canonical DDL idempotently. Mirrors the account store's method of the
        /// same name — the character and deck tables ship in the same embedded resource.
        ///
        /// <para><b>Nothing in production calls this.</b> <c>AccountSchemaInitializer</c> in
        /// <c>ServerApp.cs</c> runs the identical <see cref="AccountsSchema.Ddl"/> once at startup and
        /// covers these tables already; the only live caller is the (Postgres-gated, otherwise
        /// skipped) contract fixture, which needs the schema before the server has booted. It exists
        /// so a test can stand this store up alone — do not read its presence as a second
        /// migration path.</para>
        /// </summary>
        public Task EnsureSchemaAsync(CancellationToken ct = default)
            => WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(AccountsSchema.Ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }, ct);

        public Task<PlayerRoster> ReadAsync(string playerId, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                // One query where there were two (ADR-0009). The character table it also read is
                // gone, and the pairing it supplied now arrives inside `body` — so the join that
                // used to reconstruct a deck's legal pool from a second table is not simplified,
                // it is absent.
                var decks = new List<DeckList>();
                await using (var cmd = new NpgsqlCommand(
                    "select body from deck where player_id=$1", conn))
                {
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                        decks.Add(CollectionJson.ReadDecks(r.GetString(0))[0]);
                }

                return new PlayerRoster(decks);
            }, ct);

        /// <summary>Upsert, guarded by <c>where deck.player_id = excluded.player_id</c>. When that
        /// guard fails the statement matches zero rows and reports <c>INSERT 0 0</c> — the write did
        /// not happen, so this returns false rather than letting a refusal read as a save. The
        /// refusal is about ATTRIBUTION, never about the deck being legal.
        ///
        /// <para><b>The pairing rides inside <c>body</c></b> (ADR-0009), which is replaced wholesale
        /// on every save — so unlike the old <c>character</c> table, immutability cannot be enforced
        /// by leaving a column out of the <c>do update set</c>. <see cref="DeckWriteEndpoint"/> owns
        /// that check, because it is the only layer that sees the incoming and stored pairings
        /// together.</para></summary>
        public Task<bool> SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default)
        {
            string body = CollectionJson.WriteDecks(new[] { deck });
            var now = DateTimeOffset.UtcNow;
            return WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into deck (deck_id, player_id, body, created_at, updated_at) " +
                    "values ($1,$2,$3,$4,$4) " +
                    "on conflict (deck_id) do update set body = excluded.body, " +
                    "  updated_at = excluded.updated_at " +
                    "where deck.player_id = excluded.player_id", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, deck.Id));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, body));
                cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, now.UtcDateTime));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
            }, ct);
        }

        public Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default)
            => WithConnectionAsync(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "delete from deck where deck_id=$1 and player_id=$2", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, deckId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
            }, ct);

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
