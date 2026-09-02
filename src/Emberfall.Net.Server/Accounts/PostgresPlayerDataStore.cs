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
        /// unit. Every operation here is — both saves are upserts and the delete is a plain filtered
        /// delete — so this store satisfies it. Do not add a non-idempotent operation without
        /// re-reading this comment.</para>
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

        /// <summary>Apply the canonical DDL idempotently. Call once before use (mirrors the account
        /// store) — the character and deck tables ship in the same embedded resource.</summary>
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
                var characters = new List<Character>();
                await using (var cmd = new NpgsqlCommand(
                    "select character_id, name, primary_class, secondary_class, default_deck_id " +
                    "from character where player_id=$1", conn))
                {
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        characters.Add(new Character(
                            r.GetString(0), r.GetString(1),
                            Enum.Parse<CardClass>(r.GetString(2)),
                            Enum.Parse<CardClass>(r.GetString(3)),
                            r.GetString(4)));
                    }
                }

                var decks = new List<DeckList>();
                await using (var cmd = new NpgsqlCommand(
                    "select body from deck where player_id=$1", conn))
                {
                    cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                        decks.Add(CollectionJson.ReadDecks(r.GetString(0))[0]);
                }

                return new PlayerRoster(characters, decks);
            }, ct);

        public Task SaveCharacterAsync(string playerId, Character character, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            return WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into character (character_id, player_id, name, primary_class, secondary_class, default_deck_id, created_at, updated_at) " +
                    "values ($1,$2,$3,$4,$5,$6,$7,$7) " +
                    "on conflict (character_id) do update set name = excluded.name, " +
                    "  default_deck_id = excluded.default_deck_id, updated_at = excluded.updated_at " +
                    "where character.player_id = excluded.player_id", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, character.Id));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, character.Name));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, character.PrimaryClass.ToString()));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, character.SecondaryClass.ToString()));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, character.DefaultDeckId));
                cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, now.UtcDateTime));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }, ct);
        }

        public Task SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default)
        {
            string body = CollectionJson.WriteDecks(new[] { deck });
            var now = DateTimeOffset.UtcNow;
            return WithConnectionAsync<object?>(async conn =>
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into deck (deck_id, player_id, character_id, body, created_at, updated_at) " +
                    "values ($1,$2,$3,$4,$5,$5) " +
                    "on conflict (deck_id) do update set body = excluded.body, " +
                    "  character_id = excluded.character_id, updated_at = excluded.updated_at " +
                    "where deck.player_id = excluded.player_id", conn);
                cmd.Parameters.Add(P(NpgsqlDbType.Text, deck.Id));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, deck.CharacterId));
                cmd.Parameters.Add(P(NpgsqlDbType.Text, body));
                cmd.Parameters.Add(P(NpgsqlDbType.TimestampTz, now.UtcDateTime));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
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
