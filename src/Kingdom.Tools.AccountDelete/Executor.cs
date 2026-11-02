using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Tools.AccountDelete
{
    internal enum ExecuteStatus
    {
        /// <summary>Every step is done and the plan file is deleted.</summary>
        Completed,

        /// <summary>The login is gone; the JWT wait has not run out. Run the same command again after
        /// <see cref="ExecuteResult.ResumeAfter"/>.</summary>
        Waiting,

        /// <summary>Nothing was changed by this run. The reason is in <see cref="ExecuteResult.Message"/>.</summary>
        Refused,
    }

    internal sealed record ExecuteResult(
        ExecuteStatus Status,
        string Message,
        DateTimeOffset? ResumeAfter = null,
        DateOnly? ArchiveExpiresOn = null,
        IReadOnlyList<string>? Differences = null);

    /// <summary>
    /// Runs one approved plan, in the runbook's order: Supabase Auth, the JWT wait, the accounts DB,
    /// the telemetry DB, D1, the record. Each step stamps the <c>account_deletion</c> row as it
    /// completes, so a run that stops resumes at the first unstamped step and never repeats one.
    ///
    /// <para><b>Why Auth goes first.</b> While the Supabase user exists, every endpoint get-or-creates
    /// the account from a verified token, so deleted rows would come back on the player's next boot.
    /// And a token already issued still verifies locally until it expires, so the databases wait out
    /// the token lifetime after the user is gone.</para>
    /// </summary>
    internal sealed class Executor
    {
        /// <summary>The Supabase access-token lifetime for the project: 1 hour, read off the data on
        /// 2026-11-02 (runbook, <i>Before the first run</i>). If that setting is ever raised, raise this.</summary>
        internal static readonly TimeSpan JwtLifetime = TimeSpan.FromHours(1);

        /// <summary>Clock skew between this machine, Supabase and the game server.</summary>
        internal static readonly TimeSpan WaitMargin = TimeSpan.FromMinutes(5);

        private readonly Planner _planner;
        private readonly IAuthDirectory _auth;
        private readonly IDeletionAccounts _accounts;
        private readonly IDeletionTelemetry _telemetry;
        private readonly IPreregStore _prereg;
        private readonly Func<DateTimeOffset> _now;

        public Executor(IAuthDirectory auth, IDeletionAccounts accounts, IDeletionTelemetry telemetry, IPreregStore prereg,
            Func<DateTimeOffset> now)
        {
            _auth = auth;
            _accounts = accounts;
            _telemetry = telemetry;
            _prereg = prereg;
            _now = now;
            _planner = new Planner(auth, accounts, telemetry, prereg);
        }

        /// <param name="planBytes">The plan file's bytes, exactly as read.</param>
        /// <param name="approvedSha256">The hash Sam approved. The run refuses any other file.</param>
        public async Task<ExecuteResult> RunAsync(byte[] planBytes, string approvedSha256, CancellationToken ct = default)
        {
            string hash = DeletionPlan.Sha256Hex(planBytes);
            if (!string.Equals(hash, (approvedSha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return Refuse($"The plan file's SHA-256 is {hash}, not the approved one. Nothing was changed.");

            DeletionPlan plan;
            try { plan = DeletionPlan.FromBytes(planBytes); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                return Refuse("The plan file does not parse: " + ex.Message);
            }

            if (!await _accounts.RecordTableExistsAsync(ct))
                return Refuse("The account_deletion table does not exist. Deploy the server that creates it first.");

            DeletionRecord? record = await _accounts.GetRecordAsync(hash, ct);
            if (record is null)
            {
                // First run of this plan: prove nothing moved since it was written and approved.
                ExecuteResult? moved = await VerifyUnchangedAsync(plan, ct);
                if (moved is not null) return moved;

                var fresh = new DeletionRecord(hash, plan.RequestMessageId, plan.RequestReceivedAtUtc, plan.TombstoneId, _now());
                if (!await _accounts.TryInsertRecordAsync(fresh, ct))
                    return Refuse("Another run of this plan started at the same moment. Nothing was changed by this one.");
                record = (await _accounts.GetRecordAsync(hash, ct))!;
            }
            else if (record.TombstoneId != plan.TombstoneId)
            {
                return Refuse("The deletion record does not match this plan's tombstone. Hand to Sam.");
            }

            if (record.CompletedAt is not null)
                return new ExecuteResult(ExecuteStatus.Completed, "This plan's deletion was already complete.",
                    ArchiveExpiresOn: record.ArchiveExpiresOn);

            // a. Supabase Auth.
            var notes = new List<string>();
            DateTimeOffset authDeletedAt;
            if (record.AuthDeletedAt is DateTimeOffset done) authDeletedAt = done;
            else
            {
                bool resuming = record.AuthDeleteStartedAt is not null;
                if (!resuming) await _accounts.StampAsync(hash, DeletionStep.AuthDeleteStarted, _now(), ct: ct);

                // A 404 on a resumed run is the previous run's delete having landed before it could
                // stamp. On a first attempt the user vanished after the check above: still gone, which
                // is the point of the step, but Sam should hear that something else removed it.
                if (await _auth.DeleteUserAsync(plan.SupabaseUserId, ct) == AuthDeleteResult.NotFound && !resuming)
                    notes.Add("The Supabase user was already gone when the run went to delete it. Tell Sam.");

                authDeletedAt = _now();
                await _accounts.StampAsync(hash, DeletionStep.AuthDeleted, authDeletedAt, ct: ct);
            }

            // b. Wait out the token lifetime from (a).
            DateTimeOffset resumeAfter = authDeletedAt + JwtLifetime + WaitMargin;
            if (_now() < resumeAfter)
                return new ExecuteResult(ExecuteStatus.Waiting,
                    Join(notes, $"The login is deleted. The databases wait until {resumeAfter:u}; run the same command again then."),
                    ResumeAfter: resumeAfter);

            // c. Accounts DB, stamped inside its own transaction.
            if (record.AccountsDbAt is null)
                await _accounts.ExecuteAsync(plan.PlayerId, plan.TombstoneId, hash, _now(), ct);

            // d. Telemetry DB. Its own database, so the stamp follows the commit; a run that stops
            // between the two repeats a transaction that by then matches nothing.
            if (record.TelemetryDbAt is null)
            {
                await _telemetry.ExecuteAsync(plan.PlayerId, plan.TombstoneId, plan.Devices, ct);
                await _accounts.StampAsync(hash, DeletionStep.TelemetryDb, _now(), ct: ct);
            }

            // e. Cloudflare D1 prereg.
            if (record.PreregAt is null)
            {
                await _prereg.DeleteAsync(plan.Email, ct);
                await _accounts.StampAsync(hash, DeletionStep.Prereg, _now(), ct: ct);
            }

            // f. The record.
            DateTimeOffset completedAt = _now();
            DateOnly expiry = DeletionPlan.ArchiveExpiry(completedAt);
            await _accounts.StampAsync(hash, DeletionStep.Completed, completedAt, expiry, ct);
            return new ExecuteResult(ExecuteStatus.Completed, Join(notes, "Deletion complete."), ArchiveExpiresOn: expiry);
        }

        private async Task<ExecuteResult?> VerifyUnchangedAsync(DeletionPlan plan, CancellationToken ct)
        {
            Resolution r = await _planner.ResolveAsync(plan.Email, ct);
            if (r.Refusal != Refusal.None)
                return Refuse($"The account no longer resolves cleanly ({r.Refusal}). Make a fresh plan.");
            if (r.User!.Id != plan.SupabaseUserId || r.PlayerId != plan.PlayerId || r.CredentialHash != plan.CredentialHash)
                return Refuse("The address now resolves to a different account. Make a fresh plan.");

            IReadOnlyList<string> devicesNow = await _telemetry.BoundDevicesAsync(plan.PlayerId, ct);
            var diffs = new List<string>();
            if (!devicesNow.OrderBy(d => d, StringComparer.Ordinal).SequenceEqual(
                    plan.Devices.OrderBy(d => d, StringComparer.Ordinal), StringComparer.Ordinal))
                diffs.Add("telemetry devices bound to the player changed");

            SortedDictionary<string, long> now = await _planner.CountAsync(r, plan.Email, plan.Devices, ct);
            diffs.AddRange(DeletionPlan.Differences(plan.Counts, now));
            return diffs.Count == 0 ? null
                : new ExecuteResult(ExecuteStatus.Refused,
                    "The counts moved since the plan was written. Make a fresh plan and ask Sam again. Nothing was changed.",
                    Differences: diffs);
        }

        private static string Join(List<string> notes, string message) =>
            notes.Count == 0 ? message : string.Join(" ", notes) + " " + message;

        private static ExecuteResult Refuse(string message) => new ExecuteResult(ExecuteStatus.Refused, message);
    }
}
