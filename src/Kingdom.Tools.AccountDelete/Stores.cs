using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>One Supabase Auth user, as the Admin API lists it. No email is kept here: the caller
    /// already has the address it searched for.</summary>
    internal sealed record AuthUser(string Id, IReadOnlyList<string> Providers, int IdentityCount);

    internal enum AuthDeleteResult { Deleted, NotFound }

    /// <summary>Supabase Auth, through the Admin API with the secret key.</summary>
    internal interface IAuthDirectory
    {
        /// <summary>Every user whose own email, or any of whose identities' email, is
        /// <paramref name="email"/> (already normalised).</summary>
        Task<IReadOnlyList<AuthUser>> FindByEmailAsync(string email, CancellationToken ct = default);

        /// <summary>A hard delete: the user and its identities. Never a soft delete, which keeps the
        /// row and the email.</summary>
        Task<AuthDeleteResult> DeleteUserAsync(string userId, CancellationToken ct = default);
    }

    /// <summary>The landing site's pre-registration list, Cloudflare D1 <c>kobi-site</c>.</summary>
    internal interface IPreregStore
    {
        Task<long> CountAsync(string email, CancellationToken ct = default);
        Task<long> DeleteAsync(string email, CancellationToken ct = default);
    }

    /// <summary>The <c>account</c> columns the identity cross-check reads.</summary>
    internal sealed record AccountFacts(string CredentialKind, string CredentialHash, string? Email, string Kind);

    /// <summary>The steps of the run that stamp the deletion record, in order.</summary>
    internal enum DeletionStep { AuthDeleteStarted, AuthDeleted, AccountsDb, TelemetryDb, Prereg, Completed }

    /// <summary>One <c>account_deletion</c> row.</summary>
    internal sealed record DeletionRecord(
        string PlanSha256,
        string RequestMessageId,
        DateTimeOffset RequestReceivedAt,
        string TombstoneId,
        DateTimeOffset StartedAt,
        DateTimeOffset? AuthDeleteStartedAt = null,
        DateTimeOffset? AuthDeletedAt = null,
        DateTimeOffset? AccountsDbAt = null,
        DateTimeOffset? TelemetryDbAt = null,
        DateTimeOffset? PreregAt = null,
        DateTimeOffset? CompletedAt = null,
        DateOnly? ArchiveExpiresOn = null);

    /// <summary>The accounts database (<c>KINGDOM_ACCOUNTS_DB</c>), which also holds the deletion record.</summary>
    internal interface IDeletionAccounts
    {
        Task<AccountFacts?> FindAccountAsync(string playerId, CancellationToken ct = default);

        /// <summary>How many OTHER account rows carry this email (compared case-insensitively).</summary>
        Task<long> CountOtherAccountsWithEmailAsync(string email, string playerId, CancellationToken ct = default);

        /// <summary>The accounts-DB entries of <see cref="CountKeys"/>.</summary>
        Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, CancellationToken ct = default);

        /// <summary>Step c, in ONE transaction that also stamps <see cref="DeletionStep.AccountsDb"/>
        /// on the record. Returns false and changes nothing if the stamp was already there.</summary>
        Task<bool> ExecuteAsync(string playerId, string tombstoneId, string planSha256, DateTimeOffset at,
            CancellationToken ct = default);

        Task<bool> RecordTableExistsAsync(CancellationToken ct = default);
        Task<DeletionRecord?> GetRecordAsync(string planSha256, CancellationToken ct = default);

        /// <summary>False if a record for this plan already exists.</summary>
        Task<bool> TryInsertRecordAsync(DeletionRecord record, CancellationToken ct = default);

        Task StampAsync(string planSha256, DeletionStep step, DateTimeOffset at, DateOnly? archiveExpiresOn = null,
            CancellationToken ct = default);
    }

    /// <summary>The telemetry database (<c>KINGDOM_TELEMETRY_DB</c>).</summary>
    internal interface IDeletionTelemetry
    {
        /// <summary>Device hashes that appear on a row carrying this player's account id.</summary>
        Task<IReadOnlyList<string>> BoundDevicesAsync(string playerId, CancellationToken ct = default);

        /// <summary>The telemetry entries of <see cref="CountKeys"/>, for these devices.</summary>
        Task<IReadOnlyDictionary<string, long>> CountAsync(string playerId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default);

        /// <summary>Step d, in one transaction. The device-only rows go only for devices no other
        /// account is bound to, judged inside the transaction.</summary>
        Task ExecuteAsync(string playerId, string tombstoneId, IReadOnlyCollection<string> devices,
            CancellationToken ct = default);
    }
}
