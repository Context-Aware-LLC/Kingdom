using System;

namespace Kingdom.Net.Server.Accounts
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
    /// binds a credential (a Supabase subject for a player, a roster credential for a bot) to a
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
        /// for bot accounts, which never present an auth token.</summary>
        string? Email = null,
        AccountKind Kind = AccountKind.Standard,
        /// <summary>Has this player been GIVEN and OPENED their starter gift (Sam, 2026-11-02)? Derived
        /// from the ledger by <c>OnboardingGrantService.MarkOnboardedIfCompleteAsync</c>, the only
        /// writer; one-way — see <c>IAccountStore.MarkOnboardedAsync</c>. Nothing on the client routes
        /// on it today: the gift routes on the starter-pack balance it is derived from.
        ///
        /// <para><b>A default of false is correct HERE and dangerous ON THE WIRE.</b> Server-side a
        /// new account genuinely has not onboarded. But whatever projects this to a client must keep
        /// "no answer" distinct from false, or a failed read reads as a new player and reproduces the
        /// bug the flag removes.</para></summary>
        bool Onboarded = false,
        /// <summary>When this player's client last synced its collection (<c>GET /collection</c>,
        /// which every launch does), by the server's clock — see <c>IAccountStore.MarkSeenAsync</c>.
        /// <b>Null means never recorded</b>, not "never played": every account that predates the
        /// column is null until its next launch, and a bot never syncs at all. It is not backfilled from
        /// <see cref="CreatedAt"/>, which would invent a login nobody made.</summary>
        DateTimeOffset? LastSeen = null);
}
