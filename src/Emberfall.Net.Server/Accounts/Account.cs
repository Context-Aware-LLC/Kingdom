using System;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// An account's standing. <see cref="Superuser"/> is never self-assigned through
    /// <c>ResolveOrCreateAsync</c> — it is promoted explicitly via <c>IAccountStore.SetKindAsync</c>,
    /// so a client credential can never grant it to itself.
    /// </summary>
    public enum AccountKind
    {
        Standard,

        /// <summary>Bypasses L6 card-ownership legality (see <c>AccountOwnedCounts</c>) — for bot
        /// opponents and internal/QA identities that need every card legal without a real collection
        /// or entitlement history behind them.</summary>
        Superuser,
    }

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
        string? Email = null,
        AccountKind Kind = AccountKind.Standard,
        /// <summary>Has this player ever completed onboarding? One-way — see
        /// <c>IAccountStore.MarkOnboardedAsync</c>. It exists so a client can tell "brand new player"
        /// from "the read failed" when a roster comes back empty; those are the same absence
        /// otherwise, and creating a second time is the damaging reading.
        ///
        /// <para><b>A default of false is correct HERE and dangerous ON THE WIRE.</b> Server-side a
        /// new account genuinely has not onboarded. But whatever projects this to a client must keep
        /// "no answer" distinct from false, or a failed read reads as a new player and reproduces the
        /// bug the flag removes.</para></summary>
        bool Onboarded = false);
}
