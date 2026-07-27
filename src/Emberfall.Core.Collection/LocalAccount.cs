using System;
using System.Collections.Generic;

namespace Emberfall.Core.Collection
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
        /// What this account presents to the match server (<c>HelloMsg.DeviceToken</c>). Distinct per
        /// account so two seats from one machine are two accounts to the server and to telemetry —
        /// verified: <c>MatchGate.PairAsync</c> never compares accounts, so today the pairing already
        /// works and both seats simply arrive as the <i>same</i> account, indistinguishable.
        /// </summary>
        public string WireToken { get; }

        public LocalAccount(string id, string wireToken)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A local account needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(wireToken))
                throw new ArgumentException("A local account needs a wire token.", nameof(wireToken));

            Id = id;
            WireToken = wireToken;
        }

        /// <summary>True when this account's folder is named after its own bearer token — the
        /// grandfathered first account, and only ever that one.</summary>
        public bool IsGrandfathered => Id == WireToken;
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
        /// deliberate and it is not the same trade as <see cref="CharacterDeletion"/>, which cascades
        /// because a character's decks are worthless without it. A forgotten account's collection is
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
        /// <c>MrSoNord</c> and the character <c>Perdition</c>. Writing a registry row that points at
        /// that folder costs nothing; moving the folder to a tidier key would put the only real
        /// collection in existence at risk to buy consistency. So the first account keeps the device
        /// token as both its id and its wire token, permanently, and
        /// <see cref="LocalAccount.IsGrandfathered"/> says so out loud rather than leaving the
        /// asymmetry to be rediscovered.</para>
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
