using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Net.Server.Social
{
    /// <summary>
    /// Which account holds which Play Games id (#423). Only ever written with an id Google gave the
    /// server for that player (<see cref="IPlayGamesApi"/>). One Google account maps to one account
    /// here, so a second account linking the same id takes it over: whoever last proved they hold the
    /// Google account is the one their friends find.
    /// </summary>
    public interface IPlayGamesLinkStore
    {
        Task LinkAsync(string playerId, string playGamesId, CancellationToken ct = default);

        /// <summary>The accounts holding any of <paramref name="playGamesIds"/>, keyed by Play Games id.
        /// Ids nobody holds are absent.</summary>
        Task<IReadOnlyDictionary<string, string>> FindAsync(IReadOnlyCollection<string> playGamesIds,
            CancellationToken ct = default);
    }

    public sealed class InMemoryPlayGamesLinkStore : IPlayGamesLinkStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _byPlayer = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _byPlayGames = new Dictionary<string, string>(StringComparer.Ordinal);

        public Task LinkAsync(string playerId, string playGamesId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_byPlayGames.TryGetValue(playGamesId, out string? holder)) _byPlayer.Remove(holder);
                if (_byPlayer.TryGetValue(playerId, out string? previous)) _byPlayGames.Remove(previous);
                _byPlayer[playerId] = playGamesId;
                _byPlayGames[playGamesId] = playerId;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> FindAsync(IReadOnlyCollection<string> playGamesIds,
            CancellationToken ct = default)
        {
            lock (_lock)
            {
                IReadOnlyDictionary<string, string> found = playGamesIds.Distinct(StringComparer.Ordinal)
                    .Where(_byPlayGames.ContainsKey)
                    .ToDictionary(id => id, id => _byPlayGames[id], StringComparer.Ordinal);
                return Task.FromResult(found);
            }
        }
    }

    /// <summary>The durable link store, on the accounts database beside the friends graph.</summary>
    public sealed class PostgresPlayGamesLinkStore : IPlayGamesLinkStore
    {
        private readonly string _connString;
        private readonly ITimeSource _time;

        public PostgresPlayGamesLinkStore(string rawConnString, ITimeSource time)
        {
            _connString = TelemetryDrain.NormalizeConnString(rawConnString);
            _time = time;
        }

        /// <summary>Idempotent, so one retry after a dead pooled socket is safe (the friends store's rule).</summary>
        public async Task LinkAsync(string playerId, string playGamesId, CancellationToken ct = default)
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs).UtcDateTime;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connString);
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                    await using (var drop = new NpgsqlCommand(
                        "delete from play_games_link where play_games_id=$1 and player_id<>$2", conn, tx))
                    {
                        drop.Parameters.Add(P(NpgsqlDbType.Text, playGamesId));
                        drop.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                        await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    await using (var upsert = new NpgsqlCommand(
                        "insert into play_games_link (player_id, play_games_id, linked_at) values ($1,$2,$3) " +
                        "on conflict (player_id) do update set play_games_id=excluded.play_games_id, linked_at=excluded.linked_at",
                        conn, tx))
                    {
                        upsert.Parameters.Add(P(NpgsqlDbType.Text, playerId));
                        upsert.Parameters.Add(P(NpgsqlDbType.Text, playGamesId));
                        upsert.Parameters.Add(P(NpgsqlDbType.TimestampTz, now));
                        await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return;
                }
                catch (NpgsqlException ex) when (attempt == 0 && ex.IsTransient && !ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine("[play-games] transient DB fault ({0}) — retrying once.", ex.Message);
                }
            }
        }

        public async Task<IReadOnlyDictionary<string, string>> FindAsync(IReadOnlyCollection<string> playGamesIds,
            CancellationToken ct = default)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (playGamesIds.Count == 0) return found;

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "select play_games_id, player_id from play_games_link where play_games_id = any($1)", conn);
            cmd.Parameters.Add(P(NpgsqlDbType.Array | NpgsqlDbType.Text, playGamesIds.ToArray()));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                found[r.GetString(0)] = r.GetString(1);
            return found;
        }

        private static NpgsqlParameter P(NpgsqlDbType type, object? value)
            => new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }
}
