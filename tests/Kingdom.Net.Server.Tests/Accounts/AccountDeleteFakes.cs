using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Tools.AccountDelete;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>In-memory stand-ins for the deletion tool's four stores. Each records what was done to
    /// it, in one shared log, so a test can read the order of the run.</summary>
    internal sealed class DeletionWorld
    {
        public readonly List<string> Log = new List<string>();
        public readonly FakeAuthDirectory Auth;
        public readonly FakeDeletionAccounts Accounts;
        public readonly FakeDeletionTelemetry Telemetry;
        public readonly FakePrereg Prereg;
        public DateTimeOffset Now = new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero);

        public DeletionWorld()
        {
            Auth = new FakeAuthDirectory(Log);
            Accounts = new FakeDeletionAccounts(Log);
            Telemetry = new FakeDeletionTelemetry(Log);
            Prereg = new FakePrereg(Log);
        }

        public Planner Planner() => new Planner(Auth, Accounts, Telemetry, Prereg);
        public Executor Executor() => new Executor(Auth, Accounts, Telemetry, Prereg, () => Now);

        /// <summary>Changes made to any store, as opposed to reads.</summary>
        public IEnumerable<string> Writes => Log.Where(l => !l.StartsWith("read:", StringComparison.Ordinal));
    }

    internal sealed class FakeAuthDirectory : IAuthDirectory
    {
        private readonly List<string> _log;
        public readonly List<(string Id, string Email, string[] Providers)> Users = new();
        public FakeAuthDirectory(List<string> log) => _log = log;

        public Task<IReadOnlyList<AuthUser>> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            _log.Add("read:auth");
            IReadOnlyList<AuthUser> found = Users.Where(u => u.Email == email)
                .Select(u => new AuthUser(u.Id, u.Providers, u.Providers.Length)).ToList();
            return Task.FromResult(found);
        }

        public Task<AuthDeleteResult> DeleteUserAsync(string userId, CancellationToken ct = default)
        {
            _log.Add("auth.delete");
            int removed = Users.RemoveAll(u => u.Id == userId);
            return Task.FromResult(removed > 0 ? AuthDeleteResult.Deleted : AuthDeleteResult.NotFound);
        }
    }

    internal sealed class FakeDeletionAccounts : IDeletionAccounts
    {
        private readonly List<string> _log;
        public readonly Dictionary<string, AccountFacts> Accounts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal) { [CountKeys.Account] = 1, [CountKeys.Deck] = 2 };
        public readonly Dictionary<string, DeletionRecord> Records = new(StringComparer.Ordinal);
        public bool TableExists = true;
        public FakeDeletionAccounts(List<string> log) => _log = log;

        public Task<AccountFacts?> FindAccountAsync(string playerId, CancellationToken ct = default)
        {
            _log.Add("read:account");
            return Task.FromResult(Accounts.TryGetValue(playerId, out var a) ? a : null);
        }

        public Task<long> CountOtherAccountsWithEmailAsync(string email, string playerId, CancellationToken ct = default) =>
            Task.FromResult((long)Accounts.Count(kv => kv.Key != playerId && kv.Value.Email?.ToLowerInvariant() == email));

        public Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, CancellationToken ct = default)
        {
            _log.Add("read:accounts.count");
            return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>(Counts));
        }

        public Task<bool> ExecuteAsync(string playerId, string tombstoneId, string planSha256, DateTimeOffset at,
            CancellationToken ct = default)
        {
            var r = Records[planSha256];
            if (r.AccountsDbAt is not null) return Task.FromResult(false);
            _log.Add("accounts.execute");
            Records[planSha256] = r with { AccountsDbAt = at };
            return Task.FromResult(true);
        }

        public Task<bool> RecordTableExistsAsync(CancellationToken ct = default) => Task.FromResult(TableExists);

        public Task<DeletionRecord?> GetRecordAsync(string planSha256, CancellationToken ct = default) =>
            Task.FromResult(Records.TryGetValue(planSha256, out var r) ? r : null);

        public Task<bool> TryInsertRecordAsync(DeletionRecord record, CancellationToken ct = default)
        {
            if (Records.ContainsKey(record.PlanSha256)) return Task.FromResult(false);
            _log.Add("record.insert");
            Records[record.PlanSha256] = record;
            return Task.FromResult(true);
        }

        public Task StampAsync(string planSha256, DeletionStep step, DateTimeOffset at, DateOnly? archiveExpiresOn = null,
            CancellationToken ct = default)
        {
            _log.Add("record." + step);
            var r = Records[planSha256];
            Records[planSha256] = step switch
            {
                DeletionStep.AuthDeleteStarted => r with { AuthDeleteStartedAt = r.AuthDeleteStartedAt ?? at },
                DeletionStep.AuthDeleted => r with { AuthDeletedAt = r.AuthDeletedAt ?? at },
                DeletionStep.AccountsDb => r with { AccountsDbAt = r.AccountsDbAt ?? at },
                DeletionStep.TelemetryDb => r with { TelemetryDbAt = r.TelemetryDbAt ?? at },
                DeletionStep.Prereg => r with { PreregAt = r.PreregAt ?? at },
                _ => r with { CompletedAt = r.CompletedAt ?? at, ArchiveExpiresOn = r.ArchiveExpiresOn ?? archiveExpiresOn },
            };
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeDeletionTelemetry : IDeletionTelemetry
    {
        private readonly List<string> _log;
        public readonly List<string> Devices = new() { "aaaaaaaaaaaaaaaa" };
        public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal) { [CountKeys.MatchSeat] = 3 };
        public int FailNextExecutes;
        public FakeDeletionTelemetry(List<string> log) => _log = log;

        public Task<IReadOnlyList<string>> BoundDevicesAsync(string playerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Devices.ToList());

        public Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default)
        {
            _log.Add("read:telemetry.count");
            return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>(Counts));
        }

        public Task ExecuteAsync(string playerId, string tombstoneId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default)
        {
            if (FailNextExecutes > 0)
            {
                FailNextExecutes--;
                throw new InvalidOperationException("telemetry unreachable");
            }
            _log.Add("telemetry.execute");
            return Task.CompletedTask;
        }
    }

    internal sealed class FakePrereg : IPreregStore
    {
        private readonly List<string> _log;
        public long Rows = 1;
        public FakePrereg(List<string> log) => _log = log;

        public Task<long> CountAsync(string email, CancellationToken ct = default) => Task.FromResult(Rows);

        public Task<long> DeleteAsync(string email, CancellationToken ct = default)
        {
            _log.Add("prereg.delete");
            long n = Rows;
            Rows = 0;
            return Task.FromResult(n);
        }
    }
}
