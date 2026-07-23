using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// The off-box drain (C-G1-8). Ships the <see cref="JsonlTelemetrySink"/> buffer into the durable
    /// Postgres store (hosted Supabase; decision 2026-07-22). This is what makes the telemetry actually
    /// durable: the JSONL on the Fly volume is only a crash-safe BUFFER — a Fly volume is single-machine,
    /// single-region, and un-replicated, so it cannot be the store. This process is the bridge.
    ///
    /// <para><b>Effectively-once by construction.</b> The drain reads the append-only JSONL from a byte
    /// offset it checkpoints to disk, and every insert is <c>ON CONFLICT DO NOTHING</c> against a natural
    /// key. So the checkpoint is an OPTIMISATION, not the correctness guarantee: if it lags the committed
    /// rows (crash between commit and checkpoint write), the re-read is harmless — the keys dedup it. A
    /// line is only stepped over once its chunk has committed, so nothing is skipped either.</para>
    ///
    /// <para><b>Loss-free when the DB is down.</b> An unreachable Supabase fails the tick, records
    /// <see cref="LastError"/>, and does NOT advance the offset. The JSONL buffer just grows until the DB
    /// is back — which is the entire reason the buffer exists. No telemetry is dropped for an outage.</para>
    ///
    /// <para><b>Disabled unless a connection string is configured.</b> With no <c>EMBERFALL_TELEMETRY_DB</c>
    /// this type is never constructed (see <c>ServerConfig</c>); the sink still writes the local buffer.
    /// So a box with no DB configured is an honest, visible "buffered but not shipped", not a silent drop.</para>
    ///
    /// <para><b>A poison line is skipped, not fatal.</b> A line that will not parse is counted
    /// (<see cref="SkippedLines"/>) and stepped over so it cannot wedge the drain forever. The sink emits
    /// canonical JSON, so this should never fire — but "one bad row halts all telemetry" is exactly the
    /// silent-n=0 failure this condition exists to prevent, one layer down.</para>
    /// </summary>
    public sealed class TelemetryDrain : BackgroundService
    {
        /// <summary>Embedded canonical DDL — applied verbatim so there is no second copy to drift.</summary>
        private const string SchemaResource = "Emberfall.Net.Server.Telemetry.schema.sql";

        /// <summary>Cap on bytes pulled per tick, so a large backlog drains over several ticks rather
        /// than one enormous transaction that could time out the pooler.</summary>
        private const int MaxBytesPerTick = 1 << 20; // 1 MiB

        /// <summary>Lines per transaction. Bounds each commit; the offset advances per committed chunk.</summary>
        private const int ChunkLines = 250;

        private readonly string _jsonlPath;
        private readonly string _checkpointPath;
        private readonly string _connString;
        private readonly TimeSpan _interval;

        private long _offset;

        private readonly object _counterLock = new object();
        private long _shippedLines;
        private long _skippedLines;
        private long _pendingBytes;
        private DateTimeOffset? _lastDrainUtc;
        private Exception? _lastError;
        private bool _schemaReady;

        /// <param name="jsonlPath">The buffer the sink appends to (<see cref="JsonlTelemetrySink.Path"/>).</param>
        /// <param name="rawConnString">A libpq URI (<c>postgresql://…</c>) or an Npgsql key-value string.</param>
        /// <param name="pollSeconds">How often to look for new lines. Match-teardown cadence, not per-action.</param>
        public TelemetryDrain(string jsonlPath, string rawConnString, int pollSeconds = 5)
        {
            _jsonlPath = jsonlPath;
            _checkpointPath = jsonlPath + ".drain-offset";
            _connString = NormalizeConnString(rawConnString);
            _interval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        }

        // ── Observability (the sink's "failures are counted, not silent" contract, for the drain) ──
        public long ShippedLines { get { lock (_counterLock) return _shippedLines; } }
        public long SkippedLines { get { lock (_counterLock) return _skippedLines; } }
        public long PendingBytes { get { lock (_counterLock) return _pendingBytes; } }
        public long CommittedOffset => Interlocked.Read(ref _offset);
        public DateTimeOffset? LastDrainUtc { get { lock (_counterLock) return _lastDrainUtc; } }
        public Exception? LastError { get { lock (_counterLock) return _lastError; } }
        public bool SchemaReady { get { lock (_counterLock) return _schemaReady; } }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _offset = ReadCheckpoint();

            // Ensure the schema, retrying forever until the DB is reachable — the buffer holds meanwhile.
            while (!ct.IsCancellationRequested && !_schemaReady)
            {
                try { await EnsureSchemaAsync(ct); lock (_counterLock) _schemaReady = true; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Record(ex); await DelaySafe(ct); }
            }

            while (!ct.IsCancellationRequested)
            {
                try { await DrainOnceAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Record(ex); }
                await DelaySafe(ct);
            }

            // Best-effort final flush. Not required for correctness — anything unshipped is still in the
            // buffer and resumes on next startup — but it shortens the window on a graceful stop.
            try { await DrainOnceAsync(CancellationToken.None); } catch (Exception ex) { Record(ex); }
        }

        /// <summary>
        /// One synchronous pass — ensure the schema, then drain whatever is currently buffered — for the
        /// verify tool and integration checks. The running server never calls this; it loops in
        /// <see cref="ExecuteAsync"/>. Exposed so the live-Supabase proof runs the EXACT production path
        /// (same schema apply, same parse, same inserts) rather than a re-implementation that could drift.
        /// </summary>
        internal async Task RunOnceAsync(CancellationToken ct = default)
        {
            _offset = ReadCheckpoint();
            await EnsureSchemaAsync(ct);
            lock (_counterLock) _schemaReady = true;
            await DrainOnceAsync(ct);
        }

        internal async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            string ddl = ReadSchemaResource();
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task DrainOnceAsync(CancellationToken ct)
        {
            if (!File.Exists(_jsonlPath)) return;

            byte[] window;
            long fileLen;
            using (var fs = new FileStream(_jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileLen = fs.Length;
                long avail = fileLen - _offset;
                if (avail <= 0) { SetPending(0); return; }

                int toRead = (int)Math.Min(avail, MaxBytesPerTick);
                fs.Seek(_offset, SeekOrigin.Begin);
                window = new byte[toRead];
                int read = 0;
                while (read < toRead)
                {
                    int n = await fs.ReadAsync(window.AsMemory(read, toRead - read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read != toRead) Array.Resize(ref window, read);
                SetPending(fileLen - _offset - read);
            }

            // Only whole lines. A trailing partial (the sink mid-append) is left for the next tick.
            int lastNl = Array.LastIndexOf(window, (byte)'\n');
            if (lastNl < 0) return;
            int end = lastNl + 1;

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            var chunk = new List<NpgsqlBatchCommand>(ChunkLines * 2);
            long chunkBytes = 0;
            int chunkLines = 0, chunkShipped = 0, chunkSkipped = 0;

            int pos = 0;
            while (pos < end)
            {
                int nl = Array.IndexOf(window, (byte)'\n', pos, end - pos);
                int lineLen = nl - pos;                 // bytes, excluding the newline
                int totalLen = lineLen + 1;             // including the newline

                if (lineLen > 0)
                {
                    string lineText = Encoding.UTF8.GetString(window, pos, lineLen);
                    string uid = Sha256Hex(window, pos, lineLen);
                    if (TryBuildLine(lineText, uid, chunk)) chunkShipped++;
                    else chunkSkipped++;
                }

                chunkBytes += totalLen;
                chunkLines++;
                pos = nl + 1;

                if (chunkLines >= ChunkLines || pos >= end)
                {
                    await CommitChunkAsync(conn, chunk, ct);
                    _offset += chunkBytes;                // only after the chunk is durably committed
                    WriteCheckpoint(_offset);
                    lock (_counterLock)
                    {
                        _shippedLines += chunkShipped;
                        _skippedLines += chunkSkipped;
                        _lastDrainUtc = DateTimeOffset.UtcNow;
                    }
                    chunk.Clear();
                    chunkBytes = 0; chunkLines = 0; chunkShipped = 0; chunkSkipped = 0;
                }
            }
        }

        private static async Task CommitChunkAsync(NpgsqlConnection conn, List<NpgsqlBatchCommand> cmds, CancellationToken ct)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var batch = new NpgsqlBatch(conn) { Transaction = tx };
            foreach (var c in cmds) batch.BatchCommands.Add(c);
            if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        // ── Line → SQL. Kept parse-only-in-.NET so a malformed line fails HERE (caller skips it) and can
        //    never poison the transaction. Every insert is idempotent on its natural key. ──────────────
        internal static bool TryBuildLine(string lineText, string uid, List<NpgsqlBatchCommand> into)
        {
            try
            {
                using var doc = JsonDocument.Parse(lineText);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString()!;

                into.Add(Cmd(
                    "insert into raw_event (event_uid, type, line) values ($1,$2,$3) on conflict (event_uid) do nothing",
                    P(NpgsqlDbType.Text, uid), P(NpgsqlDbType.Text, type), P(NpgsqlDbType.Jsonb, lineText)));

                switch (type)
                {
                    case "match_complete": BuildMatchComplete(root, into); return true;
                    case "match_void": BuildMatchVoid(root, into); return true;
                    case "card_seen": BuildCardSeen(root, into); return true;
                    default: return true; // unknown type still lands in raw_event; nothing typed to write
                }
            }
            catch
            {
                return false; // poison line — caller counts it and steps over it
            }
        }

        private static void BuildMatchComplete(JsonElement r, List<NpgsqlBatchCommand> into)
        {
            string mid = Str(r, "matchId");
            into.Add(Cmd(
                "insert into match (match_id, env, source, ended_at, status, end_reason, winner_seat, turn_count) " +
                "values ($1,$2,$3,$4,'complete',$5,$6,$7) on conflict (match_id) do nothing",
                P(NpgsqlDbType.Text, mid), P(NpgsqlDbType.Text, Str(r, "env")), P(NpgsqlDbType.Text, Str(r, "source")),
                P(NpgsqlDbType.TimestampTz, Ts(r, "endedAtUtc")), P(NpgsqlDbType.Text, Str(r, "endReason")),
                P(NpgsqlDbType.Text, Str(r, "winnerSeat")), P(NpgsqlDbType.Integer, r.GetProperty("turnCount").GetInt32())));

            var players = r.GetProperty("players");
            BuildSeat(mid, "p0", players[0], into);
            BuildSeat(mid, "p1", players[1], into);
        }

        private static void BuildSeat(string mid, string seat, JsonElement s, List<NpgsqlBatchCommand> into)
        {
            var kept = new List<string>();
            foreach (var e in s.GetProperty("mulliganKept").EnumerateArray()) kept.Add(e.GetString()!);
            into.Add(Cmd(
                "insert into match_seat (match_id, seat, account_id, class_id, deck_id, deck_hash, result, mulligan_kept) " +
                "values ($1,$2,$3,$4,$5,$6,$7,$8) on conflict (match_id, seat) do nothing",
                P(NpgsqlDbType.Text, mid), P(NpgsqlDbType.Text, seat), P(NpgsqlDbType.Text, Str(s, "accountId")),
                P(NpgsqlDbType.Text, Str(s, "classId")), P(NpgsqlDbType.Text, Str(s, "deckId")),
                P(NpgsqlDbType.Text, Str(s, "deckHash")), P(NpgsqlDbType.Text, Str(s, "result")),
                P(NpgsqlDbType.Array | NpgsqlDbType.Text, kept.ToArray())));
        }

        private static void BuildMatchVoid(JsonElement r, List<NpgsqlBatchCommand> into)
        {
            string mid = Str(r, "matchId");
            into.Add(Cmd(
                "insert into match (match_id, env, source, ended_at, status, end_reason, winner_seat, turn_count) " +
                "values ($1,$2,$3,$4,'void',null,null,$5) on conflict (match_id) do nothing",
                P(NpgsqlDbType.Text, mid), P(NpgsqlDbType.Text, Str(r, "env")), P(NpgsqlDbType.Text, Str(r, "source")),
                P(NpgsqlDbType.TimestampTz, Ts(r, "endedAtUtc")), P(NpgsqlDbType.Integer, r.GetProperty("turnCount").GetInt32())));

            // A void is accounts-only: two account ids in seat order, no per-seat outcome.
            var players = r.GetProperty("players");
            BuildVoidSeat(mid, "p0", players[0].GetString()!, into);
            BuildVoidSeat(mid, "p1", players[1].GetString()!, into);
        }

        private static void BuildVoidSeat(string mid, string seat, string account, List<NpgsqlBatchCommand> into)
        {
            into.Add(Cmd(
                "insert into match_seat (match_id, seat, account_id) values ($1,$2,$3) " +
                "on conflict (match_id, seat) do nothing",
                P(NpgsqlDbType.Text, mid), P(NpgsqlDbType.Text, seat), P(NpgsqlDbType.Text, account)));
        }

        private static void BuildCardSeen(JsonElement r, List<NpgsqlBatchCommand> into)
        {
            var fpt = r.GetProperty("firstPlayedTurn");
            object firstPlayed = fpt.ValueKind == JsonValueKind.Null ? DBNull.Value : fpt.GetInt32();
            into.Add(Cmd(
                "insert into card_seen (match_id, env, source, ended_at, account_id, class_id, card_def_id, " +
                "in_deck, drawn_this_match, match_won, kept_in_mulligan, first_played_turn) " +
                "values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) " +
                "on conflict (match_id, account_id, card_def_id) do nothing",
                P(NpgsqlDbType.Text, Str(r, "matchId")), P(NpgsqlDbType.Text, Str(r, "env")),
                P(NpgsqlDbType.Text, Str(r, "source")), P(NpgsqlDbType.TimestampTz, Ts(r, "endedAtUtc")),
                P(NpgsqlDbType.Text, Str(r, "accountId")), P(NpgsqlDbType.Text, Str(r, "classId")),
                P(NpgsqlDbType.Text, Str(r, "cardDefId")), P(NpgsqlDbType.Boolean, r.GetProperty("inDeck").GetBoolean()),
                P(NpgsqlDbType.Boolean, r.GetProperty("drawnThisMatch").GetBoolean()),
                P(NpgsqlDbType.Boolean, r.GetProperty("matchWon").GetBoolean()),
                P(NpgsqlDbType.Boolean, r.GetProperty("keptInMulligan").GetBoolean()),
                P(NpgsqlDbType.Integer, firstPlayed)));
        }

        // ── small helpers ────────────────────────────────────────────────────────────────────────────
        private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

        private static DateTime Ts(JsonElement e, string name) =>
            DateTimeOffset.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;

        private static NpgsqlParameter P(NpgsqlDbType type, object? value) =>
            new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };

        private static NpgsqlBatchCommand Cmd(string sql, params NpgsqlParameter[] ps)
        {
            var c = new NpgsqlBatchCommand(sql);
            foreach (var p in ps) c.Parameters.Add(p);
            return c;
        }

        private static string Sha256Hex(byte[] buf, int offset, int count)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buf.AsSpan(offset, count), hash);
            return Convert.ToHexStringLower(hash);
        }

        private static string ReadSchemaResource()
        {
            var asm = typeof(TelemetryDrain).Assembly;
            using var s = asm.GetManifestResourceStream(SchemaResource)
                ?? throw new InvalidOperationException($"Embedded schema '{SchemaResource}' not found.");
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Supabase hands out a libpq URI (<c>postgresql://user:pass@host:port/db</c>); Npgsql wants a
        /// key-value string. Convert here so the operator pastes exactly what the dashboard shows. SSL is
        /// forced on — Supabase requires it, and <see cref="SslMode.Require"/> encrypts without demanding
        /// a CA chain we would otherwise have to ship.
        ///
        /// <para><b>Parsed by hand, not <see cref="Uri"/>.</b> Generated DB passwords routinely contain
        /// <c>:</c> and <c>@</c>, which <see cref="Uri"/> rejects with "Invalid port specified" even
        /// though libpq accepts them. So we split structurally — last <c>@</c> ends the userinfo, first
        /// <c>:</c> ends the username — which tolerates an unescaped password. (A literal <c>/</c> in a
        /// password is still genuinely ambiguous in a URI and must be percent-encoded; everything else
        /// works raw.) Components are percent-decoded, so a properly-encoded password also round-trips.</para>
        /// </summary>
        internal static string NormalizeConnString(string raw)
        {
            raw = raw.Trim();
            int scheme = raw.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0 ||
                (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
                 !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
                return raw; // already an Npgsql key-value string

            string rest = raw.Substring(scheme + 3);

            // Split authority (userinfo@host:port) from the path/query at the first '/'.
            int slash = rest.IndexOf('/');
            string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
            string pathQuery = slash >= 0 ? rest.Substring(slash + 1) : string.Empty;

            // Last '@' ends the userinfo — the host part never contains one, an unescaped password may.
            int at = authority.LastIndexOf('@');
            string userInfo = at >= 0 ? authority.Substring(0, at) : string.Empty;
            string hostPort = at >= 0 ? authority.Substring(at + 1) : authority;

            // First ':' ends the username; the password is the remainder and may itself contain ':'.
            int uColon = userInfo.IndexOf(':');
            string user = uColon >= 0 ? userInfo.Substring(0, uColon) : userInfo;
            string pass = uColon >= 0 ? userInfo.Substring(uColon + 1) : string.Empty;

            // Last ':' splits host from a numeric port (plain domain host; no IPv6 literal expected here).
            string host = hostPort;
            int port = 5432;
            int hColon = hostPort.LastIndexOf(':');
            if (hColon >= 0 && int.TryParse(hostPort.Substring(hColon + 1), out int p))
            {
                host = hostPort.Substring(0, hColon);
                port = p;
            }

            int q = pathQuery.IndexOf('?');
            string db = (q >= 0 ? pathQuery.Substring(0, q) : pathQuery).Trim('/');

            var b = new NpgsqlConnectionStringBuilder
            {
                Host = Uri.UnescapeDataString(host),
                Port = port,
                Username = Uri.UnescapeDataString(user),
                Password = pass.Length == 0 ? null : Uri.UnescapeDataString(pass),
                Database = db.Length == 0 ? "postgres" : Uri.UnescapeDataString(db),
                SslMode = SslMode.Require,
            };
            return b.ConnectionString;
        }

        private long ReadCheckpoint()
        {
            try
            {
                if (File.Exists(_checkpointPath) &&
                    long.TryParse(File.ReadAllText(_checkpointPath).Trim(), out long v) && v >= 0)
                    return v;
            }
            catch (Exception ex) { Record(ex); }
            return 0;
        }

        private void WriteCheckpoint(long offset)
        {
            try
            {
                string tmp = _checkpointPath + ".tmp";
                File.WriteAllText(tmp, offset.ToString(CultureInfo.InvariantCulture));
                File.Move(tmp, _checkpointPath, overwrite: true);
            }
            catch (Exception ex) { Record(ex); }
        }

        private void SetPending(long bytes) { lock (_counterLock) _pendingBytes = bytes < 0 ? 0 : bytes; }
        private void Record(Exception ex) { lock (_counterLock) _lastError = ex; }

        private async Task DelaySafe(CancellationToken ct)
        {
            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { }
        }
    }
}
