using System;
using System.Collections.Generic;

namespace Kingdom.Core.Collection
{
    /// <summary>
    /// One account on this device — a storage key and a wire token, and nothing else.
    ///
    /// <para><b>Why this exists at all:</b> the collection is scoped by identity
    /// (<see cref="CollectionPaths.ScopeDirectory"/>), which was always enough for one player per
    /// device. Two players per device needs a list of the identities that exist, and that list cannot
    /// live inside a scope — you have to pick a scope before you can read one. So it lives at the
    /// collection <b>root</b>, one level up.</para>
    ///
    /// <para><b>Testing-only in intent, not in code.</b> Sam's scope (2026-07-27) is two accounts on
    /// one machine so he can play his own deckbuilder against himself; the public account system is a
    /// separate, undecided design (decision 6a, ADR-0001). Only the switch/create <i>affordance</i> is
    /// build-gated. This type and its store are compiled and exercised everywhere, because a registry
    /// that only ran in development builds would be the exact shape of the Addressables defect that
    /// hid for four assets: a code path nobody tests is not a fallback.</para>
    ///
    /// <para><b>Two ids, not one, and they are not interchangeable.</b>
    /// <see cref="Id"/> becomes a directory name and gets logged; <see cref="WireToken"/> is a bearer
    /// credential — whoever holds it can claim these matches — and must not be. They coincide for the
    /// grandfathered first account (see <see cref="LocalAccountRoster.EnsureDefault"/>) because that
    /// account's folder was already named after the device token before this type existed. Every
    /// account minted afterwards gets two distinct values, which is also the shape identity has when
    /// it lands for real: a public user id plus a private credential.</para>
    ///
    /// <para><b>No display name here.</b> The name lives in each account's own
    /// <see cref="PlayerProfile"/>, one scope down. A copy in the registry would be a second writer
    /// for a string that already has exactly one, and the switcher can afford to read two small files.</para>
    /// </summary>
    public sealed class LocalAccount
    {
        /// <summary>The collection scope key — the folder this account's files live in.</summary>
        public string Id { get; }

        /// <summary>
        /// The authenticated user id that has claimed this account, or empty when nothing has.
        ///
        /// <para><b>A pointer, never a folder name.</b> Scoping the collection by the authenticated id
        /// directly would be the tidier design and it is the wrong one: <see cref="Id"/> is already a
        /// directory on a real player's disk, so binding by rename relocates a collection that may be
        /// the only copy in existence. Stamping the identity onto the row costs nothing, is reversible,
        /// and leaves every existing folder exactly where it is.</para>
        ///
        /// <para><b>Empty is the pre-binding state and it is normal</b> — not a defect and not a
        /// migration to run. Every account minted before this field existed carries it empty, as does
        /// every account on a build with no auth project configured. That is what makes
        /// <see cref="LocalAccountRoster.PlanBinding"/>'s adopt case the ordinary first sign-in rather
        /// than an upgrade path.</para>
        /// </summary>
        public string AuthUserId { get; }

        /// <summary>
        /// What this account presents to the match server (<c>HelloMsg.DeviceToken</c>). Distinct per
        /// account so two seats from one machine are two accounts to the server and to telemetry —
        /// verified: <c>MatchGate.PairAsync</c> never compares accounts, so today the pairing already
        /// works and both seats simply arrive as the <i>same</i> account, indistinguishable.
        /// </summary>
        public string WireToken { get; }

        public LocalAccount(string id, string wireToken, string? authUserId = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A local account needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(wireToken))
                throw new ArgumentException("A local account needs a wire token.", nameof(wireToken));

            Id = id;
            WireToken = wireToken;
            AuthUserId = authUserId ?? "";
        }

        /// <summary>True when this account's folder is named after its own bearer token — the
        /// grandfathered first account, and only ever that one.</summary>
        public bool IsGrandfathered => Id == WireToken;

        /// <summary>True when a signed-in identity has claimed this account.</summary>
        public bool IsClaimed => AuthUserId.Length > 0;

        /// <summary>
        /// This account, claimed by <paramref name="authUserId"/>. Returns a new instance; the caller
        /// still has to <see cref="ILocalAccountStore.Save"/> it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Already claimed by a <i>different</i> identity. Re-stamping would hand one player's roster
        /// and decks to another player who merely signed in on the same device, and to the first player
        /// it would look like the collection had simply been lost. Mint a second account instead.
        /// </exception>
        /// <remarks>
        /// <b>VULN-003 §4.3 — this method IS the conversion that section names "device folder→account", and the
        /// guard below is what makes it one-way and one-time.</b> An unclaimed scope is a device-local
        /// folder bound to no identity; claiming it is the moment it becomes a signed-in player's. The rule that entry binds is that a device
        /// token must never again be sufficient to reach a linked account — so **if you are relaxing
        /// the check below, adding an "unclaim", or letting a claim be moved between identities, read
        /// VULN-003 §4.3 and §4.4 first.** The property holds today for a reason that is easy to break
        /// by accident: nothing server-side keys value on <see cref="WireToken"/>, so the token that
        /// survives an adoption reaches telemetry attribution and nothing else.
        /// </remarks>
        public LocalAccount ClaimedBy(string authUserId)
        {
            if (string.IsNullOrWhiteSpace(authUserId))
                throw new ArgumentException("An identity claim needs a user id.", nameof(authUserId));
            if (IsClaimed && !string.Equals(AuthUserId, authUserId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Account '{Id}' is already claimed by another identity; claiming it again would " +
                    "give this collection to a different player.");

            return new LocalAccount(Id, WireToken, authUserId);
        }
    }

    /// <summary>
    /// The accounts on this device. Sits at the collection root, beside the scope folders rather than
    /// inside one — see <see cref="LocalAccount"/> for why it cannot be anywhere else.
    ///
    /// <para><b>No active-account pointer.</b> Which account is signed in is machine state, not
    /// collection data: it belongs in <c>PlayerPrefs</c> next to the device token, and it is the one
    /// thing here that does <i>not</i> migrate when real accounts land — the registry becomes a server
    /// table, while "who is signed in" becomes a session token in the same slot it occupies now.</para>
    /// </summary>
    public interface ILocalAccountStore
    {
        /// <summary>Every account, in creation order. Empty on a device that predates the registry.</summary>
        IReadOnlyList<LocalAccount> LoadAll();

        /// <summary>Upsert by <see cref="LocalAccount.Id"/>, preserving order.</summary>
        void Save(LocalAccount account);

        /// <summary>
        /// Forget an account. Idempotent.
        ///
        /// <para><b>Removes the registry row only — it does not touch the account's files.</b> That is
        /// deliberate. A forgotten account's collection is
        /// still a collection, and the row can be written back to reach it again; deleting the folder
        /// would make a mis-click on a testing affordance destroy a real roster.</para>
        /// </summary>
        void Delete(string accountId);
    }

    /// <summary>
    /// The one rule the registry has, kept out of the store so it can be tested without a filesystem
    /// and out of Unity so it can be tested at all.
    /// </summary>
    public static class LocalAccountRoster
    {
        /// <summary>
        /// Make sure this device has at least one account, grandfathering the caller's existing
        /// identity onto it. Returns the roster afterwards, first account first.
        ///
        /// <para><b>Grandfathering, not migrating.</b> A device that has been played already has a
        /// collection at <c>&lt;root&gt;/&lt;device token&gt;/</c> — on Sam's machine that is
        /// <c>MrSoNord</c> and the deck <c>Perdition</c>. Writing a registry row that points at
        /// that folder costs nothing; moving the folder to a tidier key would put the only real
        /// collection in existence at risk to buy consistency. So the first account keeps the device
        /// token as both its id and its wire token, permanently, and
        /// <see cref="LocalAccount.IsGrandfathered"/> says so out loud rather than leaving the
        /// asymmetry to be rediscovered later.</para>
        ///
        /// <para>Idempotent: a device that already has accounts is returned untouched, whatever its
        /// first account's key happens to be.</para>
        /// </summary>
        /// <param name="store">The device's registry.</param>
        /// <param name="existingIdentity">
        /// The identity already in use — Unity passes <c>DeviceIdentity.Token</c>. That token must
        /// never change (C-G1-8: every telemetry row already emitted is attributed to it), which is
        /// precisely why it is adopted here instead of replaced.
        /// </param>
        public static IReadOnlyList<LocalAccount> EnsureDefault(ILocalAccountStore store, string existingIdentity)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));

            var all = store.LoadAll();
            if (all.Count > 0) return all;

            store.Save(new LocalAccount(existingIdentity, existingIdentity));
            return store.LoadAll();
        }

        /// <summary>
        /// What signing in should do to this device's registry. See <see cref="PlanBinding"/>.
        /// </summary>
        public enum BindingPlan
        {
            /// <summary>An account on this device already carries this identity. Switch to it and write
            /// nothing — this is every launch after the first, and the case that makes a returning
            /// player's collection reappear.</summary>
            SwitchToExisting,

            /// <summary>The account being played is unclaimed, so this identity takes it — name, roster
            /// and decks included. The ordinary first sign-in on a device that has been played, and the
            /// reason binding is not a migration.</summary>
            AdoptCurrent,

            /// <summary>Every account on this device belongs to somebody else. The caller mints a fresh
            /// one and stamps it. A second player signing in on a borrowed phone.</summary>
            MintNew,
        }

        /// <summary>
        /// Decide which local account a freshly signed-in identity belongs to, without touching a
        /// filesystem or minting anything.
        ///
        /// <para><b>This is the whole of the reassociation rule and it lives here for one reason: it is
        /// the piece that decides whether a returning player gets their collection back or a blank
        /// one.</b> A wrong answer in either direction is silent and expensive — adopting an account
        /// that belongs to somebody else hands over their roster, and minting when we should have
        /// adopted looks exactly like the collection having been lost. Neither raises an error, so the
        /// rule is pinned in tests rather than trusted to a call site.</para>
        ///
        /// <para><b>Minting is the caller's job, not this one's.</b> This assembly holds no clock and
        /// mints no identity (see <c>PlayerAccount.NewId</c> for the same line drawn from the other
        /// side), so <see cref="BindingPlan.MintNew"/> comes back with a null
        /// <paramref name="target"/> and the caller supplies the ids.</para>
        /// </summary>
        /// <param name="accounts">Every account on the device.</param>
        /// <param name="currentAccountId">The account being played right now — the adoption candidate.</param>
        /// <param name="authUserId">The identity that just signed in.</param>
        /// <param name="target">
        /// The account to switch to (<see cref="BindingPlan.SwitchToExisting"/>) or to stamp and save
        /// (<see cref="BindingPlan.AdoptCurrent"/>); null for <see cref="BindingPlan.MintNew"/>.
        /// </param>
        public static BindingPlan PlanBinding(IReadOnlyList<LocalAccount>? accounts,
                                              string? currentAccountId,
                                              string authUserId,
                                              out LocalAccount? target)
        {
            if (string.IsNullOrWhiteSpace(authUserId))
                throw new ArgumentException("Binding needs a signed-in user id.", nameof(authUserId));

            target = null;
            if (accounts == null) return BindingPlan.MintNew;

            // Already ours. Checked first and against EVERY account, not just the current one: the
            // player may have been switched into somebody else's account when they signed in, and
            // sending them to their own is the entire point of a returning-player path.
            for (int i = 0; i < accounts.Count; i++)
            {
                if (string.Equals(accounts[i].AuthUserId, authUserId, StringComparison.Ordinal))
                {
                    target = accounts[i];
                    return BindingPlan.SwitchToExisting;
                }
            }

            // Unclaimed, and it is the one being played. Adopt it rather than minting beside it, or
            // every first sign-in would strand the collection the player has been building since
            // install behind a folder nothing points at any more.
            var current = Find(accounts, currentAccountId);
            if (current != null && !current.IsClaimed)
            {
                target = current;
                return BindingPlan.AdoptCurrent;
            }

            return BindingPlan.MintNew;
        }

        /// <summary>
        /// The account with this id, or null. A missing id is a real case, not a bug: the active
        /// pointer outlives the account it names when one is forgotten, and the caller's answer is to
        /// fall back to the first account rather than to fail a boot.
        /// </summary>
        public static LocalAccount? Find(IReadOnlyList<LocalAccount>? accounts, string? accountId)
        {
            if (accounts == null || string.IsNullOrEmpty(accountId)) return null;
            for (int i = 0; i < accounts.Count; i++)
                if (accounts[i].Id == accountId) return accounts[i];
            return null;
        }
    }
}
