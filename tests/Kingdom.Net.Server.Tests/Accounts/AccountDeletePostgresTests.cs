using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Tools.AccountDelete;
using Npgsql;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The deletion tool's SQL against a real Postgres: both canonical schemas, two players who know
    /// each other, and a full plan → execute run. Ignored without <c>KINGDOM_LEDGER_DB</c>, the
    /// THROWAWAY database the contract suites use (<see cref="PostgresProbe"/>). Each test works in its
    /// own fresh Postgres schema and drops it afterwards, so it never touches the public tables.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public sealed class AccountDeletePostgresTests
    {
        private const string Sub = "4f1c2b7e-9d3a-4c55-8e21-6b0f9a7d3c11";
        private const string Email = "player@example.com";
        private const string Friend = "ffffffffffffffffffffffffffffffff";
        private const string DeviceOwn = "1111111111111111";
        private const string DeviceShared = "2222222222222222";

        private static readonly string Pid = AccountIdentity.DerivePlayerId("supabase", CredentialHash.Of(Sub));

        private string _admin = "";
        private string _conn = "";
        private string _schema = "";

        [SetUp]
        public async Task SetUp()
        {
            _admin = PostgresProbe.RequireOrIgnore();
            _schema = "acctdel_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            await ExecAsync(_admin, $"create schema {_schema}");
            _conn = new NpgsqlConnectionStringBuilder(_admin) { SearchPath = _schema }.ConnectionString;
            await ExecAsync(_conn, AccountsSchema.Ddl);
            await ExecAsync(_conn, AccountDeleteTests.Resource("Kingdom.Net.Server.Telemetry.schema.sql"));
            await SeedAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            if (_schema.Length > 0 && _admin.Length > 0) await ExecAsync(_admin, $"drop schema if exists {_schema} cascade");
        }

        private async Task SeedAsync()
        {
            const string at = "'2026-10-01T00:00:00Z'";
            await ExecAsync(_conn, $@"
insert into account (player_id, credential_kind, credential_hash, created_at, email) values
  ('{Pid}', 'supabase', '{CredentialHash.Of(Sub)}', {at}, 'Player@Example.com'),
  ('{Friend}', 'supabase', 'friendhash', {at}, 'friend@example.com');
insert into entitlement_grant values
  ('signup:{Pid}', '{Pid}', 'gold', 100, 'signup', {at}),
  ('store:GPA.1', '{Pid}', 'gems', 500, 'play_billing', {at}),
  ('refund:GPA.1', '{Pid}', 'gems', -500, 'refund', {at}),
  ('signup:{Friend}', '{Friend}', 'gold', 100, 'signup', {at});
insert into deck values ('d1', '{Pid}', '{{}}', {at}, {at}), ('d2', '{Friend}', '{{}}', {at}, {at});
insert into quest_progress values ('{Pid}', 'q1', '2026-10-01', 'm1', 1, {at});
insert into completed_match values ('{Pid}', 'm1', {at}), ('{Friend}', 'm1', {at});
insert into achievement_earned values ('{Pid}', 'a1', 'm1', {at});
insert into favor_score values (1, '{Pid}', 'Me', 5, {at}), (1, '{Friend}', 'Pal', 4, {at});
insert into favor_prize values (1, '{Pid}', 1, 100, 3, false), (1, '{Friend}', 2, 50, null, false);
insert into king_reign (reign, favorite_id, favorite_name) values (1, '{Pid}', 'Me'), (2, '{Friend}', 'Pal');
insert into ladder_player (player_id, display_name, rating, updated_at) values ('{Pid}', 'Me', 1000, {at}), ('{Friend}', 'Pal', 1000, {at});
insert into ladder_match (match_id, ended_at, p0_player_id, p1_player_id, outcome) values
  ('m1', {at}, '{Pid}', '{Friend}', 'p0_win'), ('m2', {at}, '{Friend}', '{Pid}', 'draw');
insert into friend_code values ('{Pid}', 'CODE000001', 'Me', {at}), ('{Friend}', 'CODE000002', 'Pal', {at});
insert into friendship values ('{Pid}', '{Friend}', 'accepted', '{Pid}', {at});
insert into friend_block values ('{Friend}', '{Pid}', {at});
insert into play_games_link values ('{Pid}', 'g123', {at});

insert into match values ('m1', 'fly', 'ladder', {at}, 'complete', 'win', 'p0', 9);
insert into match_seat (match_id, seat, account_id) values ('m1', 'p0', '{Pid}'), ('m1', 'p1', '{Friend}');
insert into card_seen values
  ('m1', 'fly', 'ladder', {at}, '{Pid}', 'c', '7', true, true, true, true, 2),
  ('m1', 'fly', 'ladder', {at}, '{Friend}', 'c', '7', true, true, false, true, 3);
insert into raw_event (event_uid, type, line) values
  ('e1', 'hello', '{{""type"":""hello"",""account_id"":""{Pid}"",""device"":""{DeviceOwn}""}}'),
  ('e2', 'hello', '{{""type"":""hello"",""account_id"":""{Pid}"",""device"":""{DeviceShared}""}}'),
  ('e3', 'hello', '{{""type"":""hello"",""account_id"":""{Friend}"",""device"":""{DeviceShared}""}}'),
  ('e4', 'gate_served', '{{""type"":""gate_served"",""device"":""{DeviceOwn}""}}'),
  ('e5', 'crash', '{{""type"":""crash"",""device"":""{DeviceOwn}""}}'),
  ('e6', 'gate_served', '{{""type"":""gate_served"",""device"":""{DeviceShared}""}}'),
  ('e7', 'queue', '{{""type"":""queue"",""account_id"":""{Pid}""}}'),
  ('e8', 'match_complete', '{{""type"":""match_complete"",""players"":[{{""accountId"":""{Pid}""}},{{""accountId"":""{Friend}""}}]}}'),
  ('e9', 'card_seen', '{{""type"":""card_seen"",""accountId"":""{Pid}""}}'),
  ('e10', 'card_seen', '{{""type"":""card_seen"",""accountId"":""{Friend}""}}'),
  ('e11', 'match_void', '{{""type"":""match_void"",""players"":""not an array""}}');
");
        }

        private (Planner, Executor, FakeAuthDirectory, FakePrereg, Func<DateTimeOffset>, Action<DateTimeOffset>) Tool()
        {
            var log = new List<string>();
            var auth = new FakeAuthDirectory(log);
            auth.Users.Add((Sub, Email, new[] { "google" }));
            var prereg = new FakePrereg(log);
            var accounts = new PostgresDeletionAccounts(_conn);
            var telemetry = new PostgresDeletionTelemetry(_conn);
            DateTimeOffset now = new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero);
            return (new Planner(auth, accounts, telemetry, prereg),
                new Executor(auth, accounts, telemetry, prereg, () => now),
                auth, prereg, () => now, t => now = t);
        }

        [Test]
        public async Task The_dry_run_counts_exactly_what_the_runbook_names_and_changes_nothing()
        {
            var (planner, _, _, _, now, _) = Tool();
            string before = await SnapshotAsync();

            var (plan, refusal) = await planner.PlanAsync(Email, "abc123", now(), now());

            Assert.That(refusal, Is.EqualTo(Refusal.None));
            Assert.That(await SnapshotAsync(), Is.EqualTo(before));
            Assert.That(plan!.Devices, Is.EqualTo(new[] { DeviceOwn, DeviceShared }));
            Assert.That(plan.Counts, Is.EqualTo(new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [CountKeys.AuthUsers] = 1, [CountKeys.AuthIdentities] = 1,
                [CountKeys.Account] = 1, [CountKeys.GrantDelete] = 1, [CountKeys.GrantKept] = 2, [CountKeys.Deck] = 1,
                [CountKeys.QuestProgress] = 1, [CountKeys.CompletedMatch] = 1, [CountKeys.AchievementEarned] = 1,
                [CountKeys.FavorScore] = 1, [CountKeys.FavorPrize] = 1, [CountKeys.KingReign] = 1,
                [CountKeys.LadderPlayer] = 1, [CountKeys.LadderMatch] = 2, [CountKeys.FriendCode] = 1,
                [CountKeys.Friendship] = 1, [CountKeys.FriendBlock] = 1, [CountKeys.PlayGamesLink] = 1,
                [CountKeys.MatchSeat] = 1, [CountKeys.CardSeen] = 1, [CountKeys.RawAccount] = 3,
                [CountKeys.RawPlayers] = 2, [CountKeys.RawDeviceOnly] = 2,
                [CountKeys.Devices] = 2, [CountKeys.DevicesShared] = 1,
                [CountKeys.Prereg] = 1,
            }));
        }

        [Test]
        public async Task A_full_run_removes_the_player_and_leaves_everyone_else_alone()
        {
            var (planner, executor, auth, prereg, now, setNow) = Tool();
            var (plan, _) = await planner.PlanAsync(Email, "abc123", now(), now());
            byte[] bytes = plan!.ToBytes();
            string hash = DeletionPlan.Sha256Hex(bytes);
            string t = plan.TombstoneId;

            ExecuteResult first = await executor.RunAsync(bytes, hash);
            Assert.That(first.Status, Is.EqualTo(ExecuteStatus.Waiting));
            Assert.That(await ScalarAsync($"select count(*) from account where player_id = '{Pid}'"), Is.EqualTo(1),
                "the databases wait out the token");
            setNow(first.ResumeAfter!.Value);
            ExecuteResult done = await executor.RunAsync(bytes, hash);
            Assert.That(done.Status, Is.EqualTo(ExecuteStatus.Completed), done.Message);
            Assert.That(auth.Users, Is.Empty);
            Assert.That(prereg.Rows, Is.Zero);

            // Nothing in the accounts DB names the player except the kept ledger rows.
            foreach (string table in PostgresDeletionAccounts.TableNames)
            {
                long n = await ScalarAsync($"select count(*) from {table} t where row_to_json(t)::text like '%{Pid}%'");
                long expected = table == "entitlement_grant" ? 2 : 0;
                Assert.That(n, Is.EqualTo(expected), table);
            }
            Assert.That(await ScalarAsync($"select count(*) from entitlement_grant where player_id = '{Pid}' and external_txn_id like 'signup:%'"), Is.Zero);

            // The opponent's history stays, with the tombstone in our seat.
            Assert.That(await ScalarAsync($"select count(*) from ladder_match where p0_player_id = '{t}' or p1_player_id = '{t}'"), Is.EqualTo(2));
            Assert.That(await ScalarAsync($"select count(*) from match_seat where account_id = '{t}'"), Is.EqualTo(1));
            Assert.That(await ScalarAsync($"select count(*) from card_seen where account_id = '{t}'"), Is.EqualTo(1));

            // The reign stays, with no favorite named; the friend's reign is untouched.
            Assert.That(await ScalarAsync("select count(*) from king_reign where reign = 1 and favorite_id is null and favorite_name is null"), Is.EqualTo(1));
            Assert.That(await ScalarAsync($"select count(*) from king_reign where reign = 2 and favorite_id = '{Friend}'"), Is.EqualTo(1));

            // The friend keeps everything that is only theirs.
            foreach (string table in new[] { "account", "deck", "completed_match", "ladder_player", "friend_code", "favor_score", "favor_prize" })
                Assert.That(await ScalarAsync($"select count(*) from {table} where player_id = '{Friend}'"), Is.EqualTo(1), table);

            // Telemetry: the player's lines and their own device's lines are gone; the shared device's
            // anonymous line and the friend's lines stay.
            string[] left = (await StringsAsync("select event_uid from raw_event order by event_uid")).ToArray();
            Assert.That(left, Is.EquivalentTo(new[] { "e3", "e6", "e10", "e11" }));
            Assert.That(await ScalarAsync($"select count(*) from raw_event where line::text like '%{Pid}%'"), Is.Zero);

            // The record holds neither the email nor the player id.
            string record = (await StringsAsync("select row_to_json(d)::text from account_deletion d")).Single();
            Assert.That(record, Does.Not.Contain(Pid).And.Not.Contain("example.com").And.Not.Contain(Sub));
            Assert.That(record, Does.Contain("\"archive_expires_on\":\"2034-04-15\"").And.Contain(t));
            Assert.That(await ScalarAsync("select count(*) from account_deletion where completed_at is not null " +
                "and auth_deleted_at is not null and accounts_db_at is not null and telemetry_db_at is not null and prereg_at is not null"),
                Is.EqualTo(1));
        }

        [Test]
        public async Task A_new_row_after_the_plan_stops_execute_before_anything_is_deleted()
        {
            var (planner, executor, auth, _, now, _) = Tool();
            var (plan, _) = await planner.PlanAsync(Email, "abc123", now(), now());
            byte[] bytes = plan!.ToBytes();
            await ExecAsync(_conn, $"insert into deck values ('d3', '{Pid}', '{{}}', now(), now())");
            string before = await SnapshotAsync();

            ExecuteResult r = await executor.RunAsync(bytes, DeletionPlan.Sha256Hex(bytes));

            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(r.Differences, Is.EqualTo(new[] { $"{CountKeys.Deck}: plan 1, now 2" }));
            Assert.That(await SnapshotAsync(), Is.EqualTo(before));
            Assert.That(auth.Users, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task The_accounts_step_runs_once_even_when_called_twice()
        {
            var accounts = new PostgresDeletionAccounts(_conn);
            var rec = new DeletionRecord("h", "abc123", DateTimeOffset.UtcNow, "deleted-x", DateTimeOffset.UtcNow);
            Assert.That(await accounts.TryInsertRecordAsync(rec), Is.True);
            Assert.That(await accounts.TryInsertRecordAsync(rec), Is.False);

            Assert.That(await accounts.ExecuteAsync(Pid, "deleted-x", "h", DateTimeOffset.UtcNow), Is.True);
            await ExecAsync(_conn, $"insert into deck values ('d9', '{Pid}', '{{}}', now(), now())");
            Assert.That(await accounts.ExecuteAsync(Pid, "deleted-x", "h", DateTimeOffset.UtcNow), Is.False);
            Assert.That(await ScalarAsync("select count(*) from deck where deck_id = 'd9'"), Is.EqualTo(1));
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>Every row of every table, as text, so "changed nothing" is checked, not assumed.</summary>
        private async Task<string> SnapshotAsync()
        {
            var tables = await StringsAsync(
                $"select table_name from information_schema.tables where table_schema = '{_schema}' order by 1");
            var parts = new List<string>();
            foreach (string table in tables)
                parts.AddRange(await StringsAsync($"select '{table}:' || row_to_json(t)::text from {table} t order by 1"));
            return string.Join("\n", parts);
        }

        private static async Task ExecAsync(string conn, string sql)
        {
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<long> ScalarAsync(string sql)
        {
            await using var c = new NpgsqlConnection(_conn);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, c);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private async Task<List<string>> StringsAsync(string sql)
        {
            await using var c = new NpgsqlConnection(_conn);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<string>();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }
    }
}
