using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The account persistence seam (EF-072) — resolves a credential to a stable player identity and
    /// persists the account. This is the decision-decoupler: the account work proceeds behind this
    /// interface <b>before</b> the platform (Supabase / UGS / …) decision is made, and that decision
    /// then binds an implementation rather than forcing a rewrite.
    ///
    /// <para>Identity today is only a per-match <c>guest-N</c> handle for gameplay plus a telemetry-only
    /// <c>AccountId</c> derived from the device token; this seam is the home for the real account once it
    /// holds value. It does not harden the device token — that is an authenticator's job (EF-074, gated
    /// on the platform decision) — it gives the token a durable identity to map to.</para>
    ///
    /// <para>Two implementations share this contract and one test suite: an in-memory one for tests and
    /// dev, and a Postgres one modelled on <c>TelemetryDrain</c> for production.</para>
    /// </summary>
    public interface IAccountStore
    {
        /// <summary>
        /// The account bound to this credential, creating one on first sight. Idempotent on
        /// <c>(credentialKind, credentialHash)</c>: the same credential always resolves to the same
        /// <see cref="Account.PlayerId"/>, across restarts and — for the durable store — across
        /// processes and races.
        /// </summary>
        /// <param name="email">Verified email at link time (EF-074c, VULN-003 §4.2). The player's
        /// recovery path; stored on first sight, never updated — Supabase holds the authoritative
        /// email. Null for guest accounts that have never signed in.</param>
        Task<Account> ResolveOrCreateAsync(string credentialKind, string credentialHash,
            string? email = null, CancellationToken ct = default);

        /// <summary>The account with this <see cref="Account.PlayerId"/>, or null if unknown.</summary>
        Task<Account?> FindByPlayerIdAsync(string playerId, CancellationToken ct = default);

        /// <summary>
        /// Promotes or demotes an account's <see cref="AccountKind"/>. Deliberately separate from
        /// <see cref="ResolveOrCreateAsync"/> — no client-supplied credential path can set this, so a
        /// caller must already hold the account's <see cref="Account.PlayerId"/> through some
        /// operator-side channel (a bot-provisioning script, an admin tool). Returns null if
        /// <paramref name="playerId"/> names no account.
        /// </summary>
        Task<Account?> SetKindAsync(string playerId, AccountKind kind, CancellationToken ct = default);

        /// <summary>
        /// Records that this player has completed onboarding. <b>One-way and idempotent</b> — there is
        /// deliberately no un-set: clearing it would replay character creation for a player who
        /// already has characters, which is the exact failure the flag exists to prevent. Returns null
        /// if <paramref name="playerId"/> names no account.
        /// </summary>
        Task<Account?> MarkOnboardedAsync(string playerId, CancellationToken ct = default);
    }

    /// <summary>
    /// Derives the stable <c>player_id</c> for a credential. Deterministic — a pure function of the
    /// credential — so get-or-create is race-safe <i>by construction</i>: concurrent first-sights
    /// compute the same id, one insert wins, and both read back the same row. 16 bytes of SHA-256 as
    /// hex, matching <c>AccountId</c>'s shape.
    ///
    /// <para>Account linking (two credentials, one player) is a later, platform-gated concern (EF-074).
    /// It would add an explicit credential→player mapping rather than change this derivation, so the
    /// seam above accommodates it without a signature change.</para>
    /// </summary>
    internal static class AccountIdentity
    {
        public static string DerivePlayerId(string credentialKind, string credentialHash)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(credentialKind + "\n" + credentialHash), digest);
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// The in-memory account store — the default for tests and single-box dev. Same idempotency
    /// semantics as the Postgres store; it holds accounts in two dictionaries (by credential, by player
    /// id). Thread-safe under a single lock.
    /// </summary>
    public sealed class InMemoryAccountStore : IAccountStore
    {
        private readonly ITimeSource _time;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Account> _byCredential = new Dictionary<string, Account>(StringComparer.Ordinal);
        private readonly Dictionary<string, Account> _byPlayerId = new Dictionary<string, Account>(StringComparer.Ordinal);

        public InMemoryAccountStore(ITimeSource time) => _time = time;

        public Task<Account> ResolveOrCreateAsync(string credentialKind, string credentialHash,
            string? email = null, CancellationToken ct = default)
        {
            Require(credentialKind, credentialHash);
            string credKey = credentialKind + "\n" + credentialHash;

            lock (_lock)
            {
                if (_byCredential.TryGetValue(credKey, out var existing))
                    return Task.FromResult(existing);

                var account = new Account(
                    AccountIdentity.DerivePlayerId(credentialKind, credentialHash),
                    credentialKind, credentialHash,
                    DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs),
                    email);
                _byCredential[credKey] = account;
                _byPlayerId[account.PlayerId] = account;
                return Task.FromResult(account);
            }
        }

        public Task<Account?> FindByPlayerIdAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _byPlayerId.TryGetValue(playerId, out var a);
                return Task.FromResult((Account?)a);
            }
        }

        public Task<Account?> SetKindAsync(string playerId, AccountKind kind, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_byPlayerId.TryGetValue(playerId, out var existing))
                    return Task.FromResult((Account?)null);

                var updated = existing with { Kind = kind };
                _byPlayerId[playerId] = updated;
                _byCredential[updated.CredentialKind + "\n" + updated.CredentialHash] = updated;
                return Task.FromResult((Account?)updated);
            }
        }

        public Task<Account?> MarkOnboardedAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_byPlayerId.TryGetValue(playerId, out var existing))
                    return Task.FromResult((Account?)null);

                var updated = existing with { Onboarded = true };
                _byPlayerId[playerId] = updated;
                _byCredential[updated.CredentialKind + "\n" + updated.CredentialHash] = updated;
                return Task.FromResult((Account?)updated);
            }
        }

        internal static void Require(string credentialKind, string credentialHash)
        {
            if (string.IsNullOrEmpty(credentialKind))
                throw new ArgumentException("credentialKind is required.", nameof(credentialKind));
            if (string.IsNullOrEmpty(credentialHash))
                throw new ArgumentException("credentialHash is required.", nameof(credentialHash));
        }
    }
}
