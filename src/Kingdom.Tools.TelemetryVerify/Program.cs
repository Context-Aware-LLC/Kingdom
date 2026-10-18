using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Kingdom.Tools.TelemetryVerify
{
    /// <summary>
    /// C-G1-8 live-durability proof. Ships a sample JSONL through the REAL <see cref="TelemetryDrain"/>
    /// into a live Supabase, then reads the tables back to prove (1) the rows land durably and (2) a
    /// second drain of the same lines adds nothing (ON CONFLICT idempotency, end-to-end).
    ///
    /// <code>
    ///   # KINGDOM_TELEMETRY_DB must be exported (the session-pooler URI from .env / Supabase).
    ///   dotnet run --project src/Kingdom.Tools.TelemetryVerify [-- path/to/telemetry.jsonl]
    /// </code>
    /// Exit 0 = PASS, 1 = a check failed, 2 = misconfiguration (no connstring / sample not found).
    /// </summary>
    internal static class Program
    {
        private static readonly string[] Tables = { "raw_event", "match", "match_seat", "card_seen" };

        private static async Task<int> Main(string[] args)
        {
            string? connRaw = Kingdom.Net.Server.KingdomEnv.Get("KINGDOM_TELEMETRY_DB");
            if (string.IsNullOrWhiteSpace(connRaw))
            {
                Console.Error.WriteLine("KINGDOM_TELEMETRY_DB is not set. Export the Supabase session-pooler URI first.");
                return 2;
            }

            string? sample = ResolveSample(args.Length > 0 ? args[0] : null);
            if (sample is null)
            {
                Console.Error.WriteLine("Sample JSONL not found. Pass a path, or run from the repo so " +
                                        "artifacts/ai-soak-smoke/telemetry.jsonl resolves.");
                return 2;
            }

            string[] lines = File.ReadAllLines(sample).Where(l => l.Trim().Length > 0).ToArray();
            string[] expectedUids = lines.Select(Sha256Hex).Distinct().ToArray();
            string connString = TelemetryDrain.NormalizeConnString(connRaw);
            var csb = new NpgsqlConnectionStringBuilder(connString);

            Console.WriteLine($"Sample : {sample}");
            Console.WriteLine($"Lines  : {lines.Length} ({expectedUids.Length} distinct)");
            Console.WriteLine($"Target : {csb.Host}:{csb.Port}/{csb.Database} (ssl={csb.SslMode})");
            Console.WriteLine(new string('-', 60));

            // Work off a temp copy so the offset checkpoint never lands in artifacts/ and each pass is fresh.
            string workDir = Path.Combine(Path.GetTempPath(), "kingdom-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            string workJsonl = Path.Combine(workDir, "telemetry.jsonl");
            File.Copy(sample, workJsonl);

            try
            {
                // Create the schema up front so the baseline count below has tables to read on a fresh DB.
                var drain = new TelemetryDrain(workJsonl, connRaw);
                await drain.EnsureSchemaAsync();

                await using var conn = new NpgsqlConnection(connString);
                await conn.OpenAsync();

                var before = await Counts(conn);
                long uidsBefore = await UidsPresent(conn, expectedUids);

                await drain.RunOnceAsync();
                var after1 = await Counts(conn);
                long uidsAfter1 = await UidsPresent(conn, expectedUids);

                // Second pass: reset the checkpoint so the identical lines are re-read. The DB must dedup.
                File.Delete(workJsonl + ".drain-offset");
                await drain.RunOnceAsync();
                var after2 = await Counts(conn);

                Report(before, after1, after2);
                Console.WriteLine(new string('-', 60));
                Console.WriteLine($"expected uids in raw_event : {uidsAfter1}/{expectedUids.Length} " +
                                  $"(was {uidsBefore} before)");
                Console.WriteLine($"drain shipped / skipped     : {drain.ShippedLines} / {drain.SkippedLines}");

                bool durable = uidsAfter1 == expectedUids.Length;
                bool noPoison = drain.SkippedLines == 0;
                bool idempotent = Tables.All(t => after2[t] == after1[t]);

                Console.WriteLine(new string('-', 60));
                Console.WriteLine($"[{(durable ? "PASS" : "FAIL")}] durability   — every sample line present in raw_event");
                Console.WriteLine($"[{(noPoison ? "PASS" : "FAIL")}] parse        — zero poison lines");
                Console.WriteLine($"[{(idempotent ? "PASS" : "FAIL")}] idempotency  — re-draining the same lines added no rows");

                bool ok = durable && noPoison && idempotent;
                Console.WriteLine(ok ? "\nALL CHECKS PASSED — telemetry is durable in Supabase." : "\nFAILED.");
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("CONNECTION / RUN FAILED:");
                for (Exception? e = ex; e is not null; e = e.InnerException)
                    Console.Error.WriteLine($"  {e.GetType().Name}: {e.Message}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Likely causes: password authentication failed (wrong DB password); " +
                    "connection timed out (outbound 5432 blocked, or the project is paused on the free tier); " +
                    "or an SSL/handshake error. The first line above is the actual cause.");
                return 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            }
        }

        private static void Report(
            System.Collections.Generic.Dictionary<string, long> before,
            System.Collections.Generic.Dictionary<string, long> after1,
            System.Collections.Generic.Dictionary<string, long> after2)
        {
            Console.WriteLine($"{"table",-12} {"before",8} {"pass1",8} {"pass2",8}");
            foreach (string t in Tables)
                Console.WriteLine($"{t,-12} {before[t],8} {after1[t],8} {after2[t],8}");
        }

        private static async Task<System.Collections.Generic.Dictionary<string, long>> Counts(NpgsqlConnection c)
        {
            var d = new System.Collections.Generic.Dictionary<string, long>();
            foreach (string t in Tables)
            {
                await using var cmd = new NpgsqlCommand($"select count(*) from {t}", c);
                d[t] = (long)(await cmd.ExecuteScalarAsync())!;
            }
            return d;
        }

        private static async Task<long> UidsPresent(NpgsqlConnection c, string[] uids)
        {
            await using var cmd = new NpgsqlCommand("select count(*) from raw_event where event_uid = any($1)", c);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = uids });
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        private static string Sha256Hex(string line) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));

        /// <summary>Explicit arg wins; otherwise walk up from the binary to the repo's sample soak file.</summary>
        private static string? ResolveSample(string? arg)
        {
            if (arg is not null)
            {
                string p = Path.GetFullPath(arg);
                return File.Exists(p) ? p : null;
            }

            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            {
                string candidate = Path.Combine(dir, "artifacts", "ai-soak-smoke", "telemetry.jsonl");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
