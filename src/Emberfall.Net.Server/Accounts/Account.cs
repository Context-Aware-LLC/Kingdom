using System;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// A durable player account (EF-072) — the stable identity the entitlement ledger is keyed on. It
    /// binds a credential (a device token today; an OAuth subject once the platform decision lands) to a
    /// server-side <see cref="PlayerId"/> that survives restarts and is independent of any store.
    ///
    /// <para><b>The hash, never the credential.</b> Like <c>AccountId</c>, only the SHA-256 of the
    /// bearer credential is kept, so a leaked account row cannot be replayed as somebody's identity.
    /// This type is server-side only and must never reach the wire or hash-canonical
    /// <c>GameState</c>.</para>
    /// </summary>
    public sealed record Account(
        string PlayerId,
        string CredentialKind,
        string CredentialHash,
        DateTimeOffset CreatedAt,
        /// <summary>Verified email at link time (EF-074c). The player's recovery path — stored so
        /// Supabase can send a magic link if the device is lost. Null for pre-EF-074c accounts and
        /// for guest accounts that never presented an auth token.</summary>
        string? Email = null);
}
