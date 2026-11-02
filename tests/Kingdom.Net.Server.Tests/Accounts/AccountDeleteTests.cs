using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Tools.AccountDelete;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The account deletion tool's decisions, against in-memory stores: the identity it resolves, what
    /// stops it, the approval by hash, the order of the run and how it resumes. The SQL itself runs in
    /// <see cref="AccountDeletePostgresTests"/>.
    /// </summary>
    [TestFixture]
    public sealed class AccountDeleteTests
    {
        private const string Sub = "4f1c2b7e-9d3a-4c55-8e21-6b0f9a7d3c11";
        private const string Email = "player@example.com";
        private const string Request = "18f2a9c3d4e5b601";
        private static readonly DateTimeOffset Received = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        private static string PlayerIdOf(string sub) =>
            AccountIdentity.DerivePlayerId("supabase", CredentialHash.Of(sub));

        private static DeletionWorld World()
        {
            var w = new DeletionWorld();
            w.Auth.Users.Add((Sub, Email, new[] { "google" }));
            w.Accounts.Accounts[PlayerIdOf(Sub)] = new AccountFacts("supabase", CredentialHash.Of(Sub), "Player@Example.com", "standard");
            return w;
        }

        private static async Task<(DeletionPlan Plan, byte[] Bytes, string Hash)> PlanAsync(DeletionWorld w)
        {
            var (plan, refusal) = await w.Planner().PlanAsync("  Player@Example.COM ", Request, Received, w.Now);
            Assert.That(refusal, Is.EqualTo(Refusal.None));
            byte[] bytes = plan!.ToBytes();
            return (plan, bytes, DeletionPlan.Sha256Hex(bytes));
        }

        // ── Identity ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_plan_names_the_player_id_the_server_signs_this_user_in_as()
        {
            // The server's own path for a Supabase sign-in, not a copy of its formula.
            var store = new InMemoryAccountStore(new FixedTime(0));
            Account server = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Sub), Email);

            var (plan, _, _) = await PlanAsync(World());

            Assert.That(plan.PlayerId, Is.EqualTo(server.PlayerId));
            Assert.That(plan.CredentialHash, Is.EqualTo(server.CredentialHash));
            Assert.That(plan.SupabaseUserId, Is.EqualTo(Sub));
            Assert.That(plan.Email, Is.EqualTo(Email));
            Assert.That(plan.TombstoneId, Does.Match("^deleted-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"));
        }

        [Test]
        public async Task Each_plan_gets_a_fresh_tombstone()
        {
            var w = World();
            var (a, _, _) = await PlanAsync(w);
            var (b, _, _) = await PlanAsync(w);
            Assert.That(a.TombstoneId, Is.Not.EqualTo(b.TombstoneId));
            Assert.That(a.TombstoneId, Does.Not.Contain(a.PlayerId));
        }

        private static readonly Dictionary<string, (Action<DeletionWorld> Arrange, Refusal Expected)> RefusalCases = new()
        {
            ["No Supabase user"] = (w => w.Auth.Users.Clear(), Refusal.NoSuchUser),
            ["Two Supabase users"] = (w => w.Auth.Users.Add(("0c0c0c0c-0000-4000-8000-000000000000", Email, new[] { "email" })),
                Refusal.MultipleUsers),
            ["No account row"] = (w => w.Accounts.Accounts.Clear(), Refusal.NoAccountRow),
            ["Superuser"] = (w => Edit(w, a => a with { Kind = "superuser" }), Refusal.NotStandardAccount),
            ["Credential hash differs"] = (w => Edit(w, a => a with { CredentialHash = new string('0', 32) }), Refusal.IdentityMismatch),
            ["Credential kind differs"] = (w => Edit(w, a => a with { CredentialKind = "bot" }), Refusal.IdentityMismatch),
            ["Account email empty"] = (w => Edit(w, a => a with { Email = null }), Refusal.EmailMismatch),
            ["Account email differs"] = (w => Edit(w, a => a with { Email = "someone@else.com" }), Refusal.EmailMismatch),
            ["Address on a second account"] = (w => w.Accounts.Accounts["ffffffffffffffffffffffffffffffff"] =
                new AccountFacts("supabase", "x", Email, "standard"), Refusal.EmailOnOtherAccount),
            ["No deletion-record table"] = (w => w.Accounts.TableExists = false, Refusal.NoRecordTable),
        };

        private static void Edit(DeletionWorld w, Func<AccountFacts, AccountFacts> change) =>
            w.Accounts.Accounts[PlayerIdOf(Sub)] = change(w.Accounts.Accounts[PlayerIdOf(Sub)]);

        private static IEnumerable<string> RefusalNames() => RefusalCases.Keys;

        [TestCaseSource(nameof(RefusalNames))]
        public async Task The_dry_run_stops_without_a_plan(string scenario)
        {
            var (arrange, expected) = RefusalCases[scenario];
            var w = World();
            arrange(w);
            var (plan, refusal) = await w.Planner().PlanAsync(Email, Request, Received, w.Now);
            Assert.That(refusal, Is.EqualTo(expected));
            Assert.That(plan, Is.Null);
            Assert.That(w.Writes, Is.Empty);
        }

        [Test]
        public async Task The_dry_run_changes_nothing_anywhere()
        {
            var w = World();
            var (plan, _, _) = await PlanAsync(w);
            Assert.That(w.Writes, Is.Empty);
            Assert.That(w.Auth.Users, Has.Count.EqualTo(1));
            Assert.That(plan.Counts[CountKeys.AuthUsers], Is.EqualTo(1));
            Assert.That(plan.Counts[CountKeys.Prereg], Is.EqualTo(1));
            Assert.That(plan.Devices, Is.EqualTo(new[] { "aaaaaaaaaaaaaaaa" }));
        }

        // ── Approval ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Execute_refuses_a_hash_that_is_not_the_files()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            ExecuteResult r = await w.Executor().RunAsync(bytes, hash.Replace(hash[0], hash[0] == 'a' ? 'b' : 'a'));
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(w.Writes, Is.Empty);
        }

        [Test]
        public async Task Execute_refuses_an_edited_plan_under_the_old_hash()
        {
            var w = World();
            var (plan, _, hash) = await PlanAsync(w);
            plan.Devices.Clear();
            ExecuteResult r = await w.Executor().RunAsync(plan.ToBytes(), hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(w.Writes, Is.Empty);
        }

        [Test]
        public async Task Execute_refuses_when_a_count_moved_since_the_plan()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            w.Accounts.Counts[CountKeys.Deck] = 3;
            ExecuteResult r = await w.Executor().RunAsync(bytes, hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(r.Differences, Is.EqualTo(new[] { $"{CountKeys.Deck}: plan 2, now 3" }));
            Assert.That(w.Writes, Is.Empty);
        }

        [Test]
        public async Task Execute_refuses_when_a_new_device_was_bound_since_the_plan()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            w.Telemetry.Devices.Add("bbbbbbbbbbbbbbbb");
            ExecuteResult r = await w.Executor().RunAsync(bytes, hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(w.Writes, Is.Empty);
        }

        [Test]
        public async Task Execute_refuses_when_the_address_now_fails_the_cross_check()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            w.Accounts.Accounts[PlayerIdOf(Sub)] = w.Accounts.Accounts[PlayerIdOf(Sub)] with { Kind = "superuser" };
            ExecuteResult r = await w.Executor().RunAsync(bytes, hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Refused));
            Assert.That(w.Writes, Is.Empty);
        }

        // ── The run ───────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_login_goes_first_and_the_databases_wait_out_the_token()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            DateTimeOffset start = w.Now;

            ExecuteResult first = await w.Executor().RunAsync(bytes, hash);
            Assert.That(first.Status, Is.EqualTo(ExecuteStatus.Waiting));
            Assert.That(first.ResumeAfter, Is.GreaterThanOrEqualTo(start + TimeSpan.FromHours(1)));
            Assert.That(w.Writes, Is.EqualTo(new[] { "record.insert", "record.AuthDeleteStarted", "auth.delete", "record.AuthDeleted" }));
            Assert.That(w.Auth.Users, Is.Empty);

            // Half way through the wait: nothing new happens, and the login is not deleted twice.
            w.Now = start + TimeSpan.FromMinutes(30);
            w.Log.Clear();
            Assert.That((await w.Executor().RunAsync(bytes, hash)).Status, Is.EqualTo(ExecuteStatus.Waiting));
            Assert.That(w.Writes, Is.Empty);

            w.Now = first.ResumeAfter!.Value;
            ExecuteResult done = await w.Executor().RunAsync(bytes, hash);
            Assert.That(done.Status, Is.EqualTo(ExecuteStatus.Completed), done.Message);
            Assert.That(w.Writes, Is.EqualTo(new[]
            {
                "accounts.execute", "telemetry.execute", "record.TelemetryDb", "prereg.delete", "record.Prereg", "record.Completed",
            }));
            DeletionRecord rec = w.Accounts.Records[hash];
            Assert.That(rec.CompletedAt, Is.Not.Null);
            Assert.That(rec.ArchiveExpiresOn, Is.EqualTo(new DateOnly(2034, 4, 15)));
            Assert.That(rec.TombstoneId, Does.StartWith("deleted-"));
        }

        [Test]
        public async Task A_run_that_stops_partway_resumes_and_repeats_no_step()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            ExecuteResult first = await w.Executor().RunAsync(bytes, hash);
            w.Now = first.ResumeAfter!.Value;

            w.Telemetry.FailNextExecutes = 1;
            Assert.ThrowsAsync<InvalidOperationException>(() => w.Executor().RunAsync(bytes, hash));
            Assert.That(w.Accounts.Records[hash].AccountsDbAt, Is.Not.Null);
            Assert.That(w.Accounts.Records[hash].TelemetryDbAt, Is.Null);

            w.Log.Clear();
            ExecuteResult done = await w.Executor().RunAsync(bytes, hash);
            Assert.That(done.Status, Is.EqualTo(ExecuteStatus.Completed));
            Assert.That(w.Writes, Is.EqualTo(new[] { "telemetry.execute", "record.TelemetryDb", "prereg.delete", "record.Prereg", "record.Completed" }));
            Assert.That(w.Log, Has.None.EqualTo("read:accounts.count"), "a resumed run does not re-check the counts");
        }

        [Test]
        public async Task A_login_deleted_before_its_stamp_landed_is_taken_as_deleted_on_resume()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            await w.Executor().RunAsync(bytes, hash);

            // As if the run died after Supabase answered but before the stamp.
            w.Accounts.Records[hash] = w.Accounts.Records[hash] with { AuthDeletedAt = null };
            w.Log.Clear();
            ExecuteResult r = await w.Executor().RunAsync(bytes, hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Waiting));
            Assert.That(r.Message, Does.Not.Contain("already gone"));
            Assert.That(w.Writes, Is.EqualTo(new[] { "auth.delete", "record.AuthDeleted" }));
        }

        [Test]
        public async Task A_login_gone_on_the_first_attempt_is_reported()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            var auth = new VanishingAuth(w.Auth);
            var executor = new Executor(auth, w.Accounts, w.Telemetry, w.Prereg, () => w.Now);
            ExecuteResult r = await executor.RunAsync(bytes, hash);
            Assert.That(r.Status, Is.EqualTo(ExecuteStatus.Waiting));
            Assert.That(r.Message, Does.Contain("already gone"));
        }

        [Test]
        public async Task A_completed_plan_does_nothing_more()
        {
            var w = World();
            var (_, bytes, hash) = await PlanAsync(w);
            ExecuteResult first = await w.Executor().RunAsync(bytes, hash);
            w.Now = first.ResumeAfter!.Value;
            await w.Executor().RunAsync(bytes, hash);

            w.Log.Clear();
            ExecuteResult again = await w.Executor().RunAsync(bytes, hash);
            Assert.That(again.Status, Is.EqualTo(ExecuteStatus.Completed));
            Assert.That(again.Message, Does.Contain("already complete"));
            Assert.That(w.Writes, Is.Empty);
        }

        [TestCase(2026, 11, 2, 2034)]
        [TestCase(2027, 1, 1, 2035)]
        [TestCase(2027, 12, 31, 2035)]
        public void The_archive_expires_seven_years_from_April_15_after_the_deletion_year(int y, int m, int d, int expiryYear)
        {
            var at = new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero);
            Assert.That(DeletionPlan.ArchiveExpiry(at), Is.EqualTo(new DateOnly(expiryYear, 4, 15)));
        }

        [Test]
        public async Task A_plan_round_trips_byte_for_byte_through_its_file()
        {
            var (plan, bytes, _) = await PlanAsync(World());
            DeletionPlan back = DeletionPlan.FromBytes(bytes);
            Assert.That(back.ToBytes(), Is.EqualTo(bytes));
            Assert.That(back.Counts, Is.EqualTo(plan.Counts));
        }

        // ── Plan files ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Plan_files_are_never_written_inside_a_git_work_tree()
        {
            string root = Path.Combine(Path.GetTempPath(), "acctdel-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                string inside = Path.Combine(root, "plans");
                Assert.That(PlanFiles.EnclosingWorkTree(inside), Is.EqualTo(root));
                Assert.Throws<InvalidOperationException>(() => PlanFiles.Write(inside, new byte[] { 1 }, DateTimeOffset.UtcNow));
                Assert.That(Directory.Exists(inside), Is.False);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void Plan_files_are_owner_only_and_never_overwrite()
        {
            string dir = Path.Combine(Path.GetTempPath(), "acctdel-" + Guid.NewGuid().ToString("N"));
            if (PlanFiles.EnclosingWorkTree(dir) is not null) Assert.Ignore("The temp directory is inside a git work tree.");
            try
            {
                var now = DateTimeOffset.UtcNow;
                string a = PlanFiles.Write(dir, new byte[] { 1 }, now);
                string b = PlanFiles.Write(dir, new byte[] { 2 }, now);
                Assert.That(a, Is.Not.EqualTo(b));
                Assert.That(File.ReadAllBytes(a), Is.EqualTo(new byte[] { 1 }));
                if (!OperatingSystem.IsWindows())
                    Assert.That(File.GetUnixFileMode(a), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // ── The schema holds the tool to its tables ───────────────────────────────────────────

        [Test]
        public void The_deletion_record_has_no_email_or_player_id_column()
        {
            string ddl = AccountsSchema.Ddl;
            Match m = Regex.Match(ddl, @"create table if not exists account_deletion \((.*?)\n\);", RegexOptions.Singleline);
            Assert.That(m.Success, "account_deletion is in Accounts/schema.sql");
            string[] columns = m.Groups[1].Value.Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
                .Select(l => l.Split(' ')[0]).ToArray();
            Assert.That(columns, Has.Length.GreaterThan(5));
            Assert.That(columns, Has.None.Contains("email").And.None.Contains("player"));
        }

        [Test]
        public void Every_accounts_table_is_handled_by_the_tool()
        {
            var tables = CreatedTables(AccountsSchema.Ddl).ToHashSet(StringComparer.Ordinal);
            tables.Remove("account_deletion");   // the record itself
            Assert.That(PostgresDeletionAccounts.TableNames, Is.EquivalentTo(tables),
                "A table in Accounts/schema.sql that the deletion tool does not name: add it to the tool and the runbook.");
        }

        [Test]
        public void Every_telemetry_table_is_handled_or_left_alone_on_purpose()
        {
            string ddl = Resource("Kingdom.Net.Server.Telemetry.schema.sql");
            Assert.That(PostgresDeletionTelemetry.TableNames.Concat(PostgresDeletionTelemetry.Untouched),
                Is.EquivalentTo(CreatedTables(ddl)),
                "A table in Telemetry/schema.sql that the deletion tool does not name: add it to the tool and the runbook.");
        }

        [Test]
        public void The_D1_database_id_is_the_one_the_landing_site_binds()
        {
            string wrangler = File.ReadAllText(Path.Combine(RepoRoot(), "web", "landing", "wrangler.jsonc"));
            Match m = Regex.Match(wrangler, "\"database_name\"\\s*:\\s*\"kobi-site\"\\s*,\\s*\"database_id\"\\s*:\\s*\"([^\"]+)\"");
            Assert.That(m.Success, "kobi-site is bound in web/landing/wrangler.jsonc");
            Assert.That(m.Groups[1].Value, Is.EqualTo(D1PreregStore.KobiSiteDatabaseId));
        }

        // ── HTTP clients, against scripted answers ────────────────────────────────────────────

        [Test]
        public async Task Supabase_search_reads_every_page_and_matches_identity_emails()
        {
            var handler = new ScriptedHandler(req =>
            {
                int page = int.Parse(Regex.Match(req.RequestUri!.Query, "page=(\\d+)").Groups[1].Value);
                string users = page switch
                {
                    1 => "[{\"id\":\"11111111-1111-4111-8111-111111111111\",\"email\":\"other@example.com\",\"identities\":[]}]",
                    2 => "[{\"id\":\"" + Sub + "\",\"email\":null,\"identities\":[{\"provider\":\"google\"," +
                         "\"identity_data\":{\"email\":\"PLAYER@example.com\"}}]}]",
                    _ => "[]",
                };
                return Json("{\"users\":" + users + "}");
            });
            var dir = new SupabaseAuthDirectory(new HttpClient(handler), "https://proj.supabase.co/", "secret");

            IReadOnlyList<AuthUser> found = await dir.FindByEmailAsync(Email);

            Assert.That(found.Select(u => u.Id), Is.EqualTo(new[] { Sub }));
            Assert.That(found[0].Providers, Is.EqualTo(new[] { "google" }));
            Assert.That(handler.Requests, Has.Count.EqualTo(3));
            Assert.That(handler.Requests[0].Headers.GetValues("apikey").Single(), Is.EqualTo("secret"));
            Assert.That(handler.Requests[0].RequestUri!.AbsolutePath, Is.EqualTo("/auth/v1/admin/users"));
        }

        [Test]
        public async Task Supabase_delete_is_a_hard_delete_by_uuid()
        {
            string? body = null;
            var handler = new ScriptedHandler(req =>
            {
                body = req.Content!.ReadAsStringAsync().Result;
                return req.RequestUri!.AbsolutePath.EndsWith(Sub, StringComparison.Ordinal)
                    ? Json("{}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var dir = new SupabaseAuthDirectory(new HttpClient(handler), "https://proj.supabase.co", "secret");

            Assert.That(await dir.DeleteUserAsync(Sub), Is.EqualTo(AuthDeleteResult.Deleted));
            Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Delete));
            Assert.That(body, Is.EqualTo("{\"should_soft_delete\":false}"));
            Assert.That(await dir.DeleteUserAsync("22222222-2222-4222-8222-222222222222"), Is.EqualTo(AuthDeleteResult.NotFound));
            Assert.ThrowsAsync<ArgumentException>(() => dir.DeleteUserAsync("../other"));
        }

        [Test]
        public async Task D1_counts_and_deletes_by_the_address_as_a_bound_parameter()
        {
            var handler = new ScriptedHandler(req =>
            {
                string sent = req.Content!.ReadAsStringAsync().Result;
                Assert.That(sent, Does.Contain("\"params\":[\"player@example.com\"]"));
                return sent.Contains("count(*)")
                    ? Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[{\"n\":1}],\"meta\":{}}]}")
                    : Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[],\"meta\":{\"changes\":1}}]}");
            });
            var d1 = new D1PreregStore(new HttpClient(handler), "0123abcd", "token");

            Assert.That(await d1.CountAsync(Email), Is.EqualTo(1));
            Assert.That(await d1.DeleteAsync(Email), Is.EqualTo(1));
            Assert.That(handler.Requests[0].RequestUri!.ToString(), Is.EqualTo(
                $"https://api.cloudflare.com/client/v4/accounts/0123abcd/d1/database/{D1PreregStore.KobiSiteDatabaseId}/query"));
        }

        [Test]
        public void D1_failure_is_an_error_not_a_zero()
        {
            var d1 = new D1PreregStore(new HttpClient(new ScriptedHandler(_ =>
                Json("{\"success\":false,\"errors\":[{\"message\":\"no\"}],\"result\":[]}"))), "0123abcd", "token");
            Assert.ThrowsAsync<InvalidOperationException>(() => d1.CountAsync(Email));
        }

        [TestCase("18f2a9c3d4e5b601", true)]
        [TestCase("", false)]
        [TestCase("18f2/../x", false)]
        [TestCase("a b", false)]
        public void Request_ids_are_letters_and_digits(string id, bool ok) =>
            Assert.That(Kingdom.Tools.AccountDelete.Program.IsMessageId(id), Is.EqualTo(ok));

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private sealed class VanishingAuth : IAuthDirectory
        {
            private readonly IAuthDirectory _inner;
            public VanishingAuth(IAuthDirectory inner) => _inner = inner;
            public Task<IReadOnlyList<AuthUser>> FindByEmailAsync(string email, CancellationToken ct = default) =>
                _inner.FindByEmailAsync(email, ct);
            public Task<AuthDeleteResult> DeleteUserAsync(string userId, CancellationToken ct = default) =>
                Task.FromResult(AuthDeleteResult.NotFound);
        }

        private sealed class FixedTime : ITimeSource
        {
            public FixedTime(long ms) => NowMs = ms;
            public long NowMs { get; }
        }

        internal sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            public readonly List<HttpRequestMessage> Requests = new();
            public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(_answer(request));
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        internal static IEnumerable<string> CreatedTables(string ddl) =>
            Regex.Matches(ddl, @"^create table if not exists (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value);

        internal static string Resource(string name)
        {
            using Stream s = typeof(AccountsSchema).Assembly.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Kingdom.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("Kingdom.slnx not found above the test directory.");
        }
    }
}
