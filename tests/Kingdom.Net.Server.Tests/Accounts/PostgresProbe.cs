using System;
using System.Threading.Tasks;
using Kingdom.Net.Server.Telemetry;
using Npgsql;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// Gates the Postgres contract fixtures on a configured database. The live round-trip (schema apply
    /// + insert + read back) cannot run in CI without credentials — exactly the posture the telemetry
    /// drain's live check takes (see <c>TelemetryDrainTests</c>) — so with no <c>EMBERFALL_LEDGER_DB</c>
    /// the whole fixture is <b>Ignored</b>, not failed. Point that variable at a THROWAWAY Postgres URI
    /// to run the identical suite against it — which is how "both implementations pass the same suite" is
    /// satisfied.
    ///
    /// <para><b>Deliberately its own variable, with no fallback to <c>EMBERFALL_TELEMETRY_DB</c>.</b>
    /// This suite applies DDL and TRUNCATES between tests, so it must only ever touch a database the
    /// operator has explicitly dedicated to it. Silently reaching the telemetry store (which is a live
    /// hosted Supabase) from a unit-test run is exactly the accident we refuse to leave armed.</para>
    /// </summary>
    internal static class PostgresProbe
    {
        public static string? ConnString =>
            Environment.GetEnvironmentVariable("EMBERFALL_LEDGER_DB");

        /// <summary>The normalized connection string, or <see cref="Assert.Ignore(string)"/> if unset.</summary>
        public static string RequireOrIgnore()
        {
            string? c = ConnString;
            if (string.IsNullOrWhiteSpace(c))
                Assert.Ignore(
                    "No EMBERFALL_LEDGER_DB configured — Postgres contract suite skipped. " +
                    "Set it to a Postgres URI (postgresql://…) to run the identical suite against a real DB.");
            return TelemetryDrain.NormalizeConnString(c!);
        }

        /// <summary>Empty the tables so each test starts from a clean ledger (both fixtures share the DB).</summary>
        public static async Task TruncateAsync(string connString)
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "truncate table entitlement_grant, account, deck, friend_code, friendship, friend_block, play_games_link", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
