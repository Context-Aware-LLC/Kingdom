using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// The account deletion tool (<c>docs/dev-docs/operations/account-deletion.md</c>). Run by the
    /// delete-account skill; never run it against an account to "test" it.
    ///
    /// <code>
    ///   dotnet run --project src/Kingdom.Tools.AccountDelete -- plan --email &lt;address&gt; \
    ///       --request &lt;gmail message id&gt; --received &lt;ISO 8601 time&gt; [--out &lt;dir&gt;]
    ///   dotnet run --project src/Kingdom.Tools.AccountDelete -- execute --plan &lt;file&gt; --approved &lt;sha256&gt;
    /// </code>
    ///
    /// <para><c>plan</c> is the dry run and the default posture: it reads, writes one plan file outside
    /// the repo, and prints counts and the file's SHA-256. <c>execute</c> takes the hash Sam approved
    /// and refuses any other file.</para>
    ///
    /// <para>Secrets come only from the environment (or <c>.env</c>), never from arguments, and are
    /// never printed. The output names the request by its Gmail message id and prints no email or
    /// player id.</para>
    /// </summary>
    internal static class Program
    {
        internal const int ExitDone = 0, ExitRefused = 1, ExitError = 2, ExitWaiting = 3;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help") return Usage();
            Dictionary<string, string>? opts = ParseOptions(args, 1);
            if (opts is null) return Usage();

            try
            {
                return args[0] switch
                {
                    "plan" => await PlanAsync(opts),
                    "execute" => await ExecuteAsync(opts),
                    _ => Usage(),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED:");
                for (Exception? e = ex; e is not null; e = e.InnerException)
                    Console.Error.WriteLine($"  {e.GetType().Name}: {e.Message}");
                return ExitError;
            }
        }

        private static async Task<int> PlanAsync(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("email", out string? email) || !opts.TryGetValue("request", out string? request)
                || !opts.TryGetValue("received", out string? receivedRaw))
                return Usage();
            if (!IsMessageId(request))
                return Fail("--request is the Gmail message id: letters and digits only.");
            if (!DateTimeOffset.TryParse(receivedRaw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset received))
                return Fail("--received is the time the request arrived, ISO 8601.");

            string dir = PlanFiles.ResolveDirectory(opts.GetValueOrDefault("out"));
            if (PlanFiles.EnclosingWorkTree(dir) is string tree)
                return Fail($"Plan files are kept outside the repo; {dir} is inside the git work tree at {tree}.");

            Stores? s = Stores.FromEnvironment();
            if (s is null) return ExitError;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var (plan, refusal) = await new Planner(s.Auth, s.Accounts, s.Telemetry, s.Prereg)
                .PlanAsync(email, request, received, now);
            if (plan is null)
            {
                Console.WriteLine($"NO PLAN: {Explain(refusal)}");
                return refusal == Refusal.NoRecordTable ? ExitError : ExitRefused;
            }

            byte[] bytes = plan.ToBytes();
            string path = PlanFiles.Write(dir, bytes, now);
            Console.WriteLine("DRY RUN. Nothing was changed.");
            Console.WriteLine($"Request   : {plan.RequestMessageId}, received {plan.RequestReceivedAtUtc:u}");
            Console.WriteLine($"Sign-in   : {string.Join(", ", plan.Providers)}");
            Console.WriteLine(new string('-', 60));
            foreach (var kv in plan.Counts) Console.WriteLine($"  {kv.Key,-42} {kv.Value,8}");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine($"Kept      : {plan.Counts[CountKeys.GrantKept]} store/refund rows, archive expiry " +
                $"{plan.ArchiveExpiresOnIfRunToday:yyyy-MM-dd} if run today");
            Console.WriteLine($"Plan file : {path}");
            Console.WriteLine($"SHA-256   : {DeletionPlan.Sha256Hex(bytes)}");
            return ExitDone;
        }

        private static async Task<int> ExecuteAsync(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("plan", out string? path) || !opts.TryGetValue("approved", out string? approved))
                return Usage();
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return Fail($"No plan file at {path}.");

            Stores? s = Stores.FromEnvironment();
            if (s is null) return ExitError;

            byte[] bytes = await File.ReadAllBytesAsync(path);
            ExecuteResult result = await new Executor(s.Auth, s.Accounts, s.Telemetry, s.Prereg, () => DateTimeOffset.UtcNow)
                .RunAsync(bytes, approved);

            switch (result.Status)
            {
                case ExecuteStatus.Refused:
                    Console.WriteLine($"REFUSED: {result.Message}");
                    foreach (string d in result.Differences ?? Array.Empty<string>()) Console.WriteLine($"  {d}");
                    return ExitRefused;
                case ExecuteStatus.Waiting:
                    Console.WriteLine($"WAITING: {result.Message}");
                    return ExitWaiting;
                default:
                    Console.WriteLine($"DONE: {result.Message}");
                    Console.WriteLine($"Archive expiry: {result.ArchiveExpiresOn:yyyy-MM-dd}");
                    // A company admin record, deleted once the deletion record is complete (Sam, 2026-11-02).
                    File.Delete(path);
                    Console.WriteLine("Plan file deleted.");
                    return ExitDone;
            }
        }

        internal static string Explain(Refusal r) => r switch
        {
            Refusal.NoSuchUser => "no Supabase user has that address. Draft the reply asking them to write from the address they sign in with.",
            Refusal.MultipleUsers => "more than one Supabase user has that address. Hand to Sam.",
            Refusal.NoAccountRow => "the Supabase user has no account row on the server. Hand to Sam.",
            Refusal.IdentityMismatch => "the account row's credential does not match the Supabase user. Hand to Sam.",
            Refusal.EmailMismatch => "the account row's email is empty or a different address. Hand to Sam.",
            Refusal.EmailOnOtherAccount => "another account row carries the same address. Hand to Sam.",
            Refusal.NotStandardAccount => "the account is not a standard account (superuser). Hand to Sam.",
            Refusal.NoRecordTable => "the account_deletion table does not exist yet. Deploy the server that creates it first.",
            _ => r.ToString(),
        };

        internal static bool IsMessageId(string s)
        {
            if (s.Length is 0 or > 64) return false;
            foreach (char c in s) if (!char.IsAsciiLetterOrDigit(c)) return false;
            return true;
        }

        internal static Dictionary<string, string>? ParseOptions(string[] args, int start)
        {
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = start; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) return null;
                if (!opts.TryAdd(args[i].Substring(2), args[i + 1])) return null;
            }
            return opts;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return ExitError;
        }

        private static int Usage()
        {
            Console.Error.WriteLine(
                "usage:\n" +
                "  AccountDelete plan --email <address> --request <gmail message id> --received <ISO 8601> [--out <dir>]\n" +
                "  AccountDelete execute --plan <file> --approved <sha256>\n" +
                "exit: 0 done, 1 refused, 2 error, 3 waiting (run the same execute again later)");
            return ExitError;
        }

        /// <summary>The live stores, from the environment. Null (and a message) when any is missing:
        /// a plan that cannot count every store cannot be the plan Sam approves.</summary>
        private sealed record Stores(IAuthDirectory Auth, IDeletionAccounts Accounts, IDeletionTelemetry Telemetry, IPreregStore Prereg)
        {
            public static Stores? FromEnvironment()
            {
                string? accounts = KingdomEnv.Get("KINGDOM_ACCOUNTS_DB");
                string? telemetry = KingdomEnv.Get("KINGDOM_TELEMETRY_DB");
                string? url = KingdomEnv.Get("KINGDOM_SUPABASE_URL");
                string? key = KingdomEnv.Get(SupabaseAdmin.SecretKeyEnvVar);
                string? cfAccount = KingdomEnv.Get("KINGDOM_CLOUDFLARE_ACCOUNT_ID");
                string? cfToken = KingdomEnv.Get("KINGDOM_CLOUDFLARE_D1_TOKEN");

                var missing = new List<string>();
                if (accounts is null) missing.Add("KINGDOM_ACCOUNTS_DB");
                if (telemetry is null) missing.Add("KINGDOM_TELEMETRY_DB");
                if (url is null) missing.Add("KINGDOM_SUPABASE_URL");
                if (key is null) missing.Add(SupabaseAdmin.SecretKeyEnvVar);
                if (cfAccount is null) missing.Add("KINGDOM_CLOUDFLARE_ACCOUNT_ID");
                if (cfToken is null) missing.Add("KINGDOM_CLOUDFLARE_D1_TOKEN");
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine("Not set: " + string.Join(", ", missing) + ". Nothing was read or changed.");
                    return null;
                }

                var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                return new Stores(
                    new SupabaseAuthDirectory(http, url!, key!.Trim()),
                    new PostgresDeletionAccounts(TelemetryDrain.NormalizeConnString(accounts!)),
                    new PostgresDeletionTelemetry(TelemetryDrain.NormalizeConnString(telemetry!)),
                    new D1PreregStore(http, cfAccount!.Trim(), cfToken!.Trim()));
            }
        }
    }
}
