using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>Why the tool stopped without a plan, or refused one. Every one of these goes to Sam.</summary>
    internal enum Refusal
    {
        None,

        /// <summary>No Supabase user has that address. The skill drafts the "write from the address you
        /// sign in with" reply.</summary>
        NoSuchUser,

        MultipleUsers,

        /// <summary>The Supabase user exists but the server has no account row for it. Refused rather
        /// than planned as "auth only": if the derivation ever disagreed with the server's, every
        /// count would read zero, the login would go and the real rows would stay.</summary>
        NoAccountRow,

        /// <summary><c>credential_kind</c> or <c>credential_hash</c> does not match the Supabase user.</summary>
        IdentityMismatch,

        /// <summary><c>account.email</c> is empty or another address.</summary>
        EmailMismatch,

        /// <summary>Another account row carries the same address.</summary>
        EmailOnOtherAccount,

        /// <summary><c>account.kind</c> is not <c>standard</c> (a superuser).</summary>
        NotStandardAccount,

        /// <summary>The deletion-record table does not exist yet: the server that creates it has not
        /// been deployed.</summary>
        NoRecordTable,
    }

    internal sealed record Resolution(
        Refusal Refusal, AuthUser? User = null, string CredentialHash = "", string PlayerId = "");

    /// <summary>
    /// The dry run: resolve the identity, count what each step would touch, and build the plan. Reads
    /// only. Execute calls the same code to prove nothing moved since the plan was approved.
    /// </summary>
    internal sealed class Planner
    {
        private readonly IAuthDirectory _auth;
        private readonly IDeletionAccounts _accounts;
        private readonly IDeletionTelemetry _telemetry;
        private readonly IPreregStore _prereg;

        public Planner(IAuthDirectory auth, IDeletionAccounts accounts, IDeletionTelemetry telemetry, IPreregStore prereg)
        {
            _auth = auth;
            _accounts = accounts;
            _telemetry = telemetry;
            _prereg = prereg;
        }

        /// <summary>Email → Supabase user → <c>sub</c> → <c>credential_hash</c> → <c>player_id</c>,
        /// cross-checked against <c>account.email</c> and <c>account.credential_hash</c>.</summary>
        public async Task<Resolution> ResolveAsync(string email, CancellationToken ct = default)
        {
            IReadOnlyList<AuthUser> users = await _auth.FindByEmailAsync(email, ct);
            if (users.Count == 0) return new Resolution(Refusal.NoSuchUser);
            if (users.Count > 1) return new Resolution(Refusal.MultipleUsers);

            AuthUser user = users[0];
            string credentialHash = CredentialHash.Of(user.Id);
            string playerId = AccountIdentity.DerivePlayerId(SupabaseKind, credentialHash);

            AccountFacts? account = await _accounts.FindAccountAsync(playerId, ct);
            if (account is null) return new Resolution(Refusal.NoAccountRow, user);
            if (!string.Equals(account.Kind, "standard", StringComparison.Ordinal))
                return new Resolution(Refusal.NotStandardAccount, user);
            if (account.CredentialKind != SupabaseKind || account.CredentialHash != credentialHash)
                return new Resolution(Refusal.IdentityMismatch, user);
            if (account.Email is null || DeletionPlan.NormalizeEmail(account.Email) != email)
                return new Resolution(Refusal.EmailMismatch, user);
            if (await _accounts.CountOtherAccountsWithEmailAsync(email, playerId, ct) != 0)
                return new Resolution(Refusal.EmailOnOtherAccount, user);

            return new Resolution(Refusal.None, user, credentialHash, playerId);
        }

        /// <summary>Every count, for the resolved player and these devices.</summary>
        public async Task<SortedDictionary<string, long>> CountAsync(
            Resolution r, string email, IReadOnlyCollection<string> devices, CancellationToken ct = default)
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [CountKeys.AuthUsers] = 1,
                [CountKeys.AuthIdentities] = r.User!.IdentityCount,
            };
            foreach (var kv in await _accounts.CountAsync(r.PlayerId, ct)) counts[kv.Key] = kv.Value;
            foreach (var kv in await _telemetry.CountAsync(r.PlayerId, devices, ct)) counts[kv.Key] = kv.Value;
            counts[CountKeys.Prereg] = await _prereg.CountAsync(email, ct);
            return counts;
        }

        /// <summary>The plan, or the refusal that stopped it. Writes nothing anywhere.</summary>
        public async Task<(DeletionPlan? Plan, Refusal Refusal)> PlanAsync(
            string rawEmail, string requestMessageId, DateTimeOffset requestReceivedAt, DateTimeOffset now,
            CancellationToken ct = default)
        {
            if (!await _accounts.RecordTableExistsAsync(ct)) return (null, Refusal.NoRecordTable);

            string email = DeletionPlan.NormalizeEmail(rawEmail);
            Resolution r = await ResolveAsync(email, ct);
            if (r.Refusal != Refusal.None) return (null, r.Refusal);

            IReadOnlyList<string> devices = await _telemetry.BoundDevicesAsync(r.PlayerId, ct);
            var plan = new DeletionPlan
            {
                CreatedAtUtc = now.ToUniversalTime(),
                RequestMessageId = requestMessageId,
                RequestReceivedAtUtc = requestReceivedAt.ToUniversalTime(),
                Email = email,
                SupabaseUserId = r.User!.Id,
                Providers = r.User.Providers.ToList(),
                CredentialHash = r.CredentialHash,
                PlayerId = r.PlayerId,
                TombstoneId = DeletionPlan.NewTombstone(),
                Devices = devices.OrderBy(d => d, StringComparer.Ordinal).ToList(),
                Counts = await CountAsync(r, email, devices, ct),
                ArchiveExpiresOnIfRunToday = DeletionPlan.ArchiveExpiry(now),
            };
            return (plan, Refusal.None);
        }

        internal const string SupabaseKind = "supabase";
    }
}
