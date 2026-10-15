using System;
using System.Collections.Generic;
using Kingdom.Core.Collection;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// Which player is sitting at this machine, and which others have signed in on it.
    ///
    /// <para><b>Why there can be more than one.</b> Each signed-in identity gets its own local
    /// account and scope folder (<see cref="BindToIdentity"/>), so a second person signing in on
    /// this device never inherits the first one's roster. It began as a two-seat testing rig
    /// (2026-07-27); the switcher UI and the per-process pin were removed 2026-10-15.</para>
    ///
    /// <para><b>The split with <see cref="DeviceIdentity"/> is load-bearing.</b> That token is minted
    /// once and persisted forever, and it must stay that way: C-G1-8 turns on every telemetry row
    /// already emitted being attributable, and re-minting orphans all of them. So it is never
    /// rewritten. Instead the first account <i>adopts</i> it — same folder, same wire token, byte for
    /// byte what this machine did yesterday — and accounts minted afterwards get their own. A release
    /// build on a fresh machine ends up with exactly one account holding the device token, which is
    /// today's behaviour reached by the new path.</para>
    ///
    /// <para><b>Not the public account system.</b> That is a separate, undecided design (decision 6a,
    /// ADR-0001). Nothing here is thrown away when it lands — the storage key becomes a server-issued
    /// user id and each folder migrates by being copied, which is what the per-identity scoping was
    /// for.</para>
    /// </summary>
    public static class DeviceAccounts
    {
        /// <summary>
        /// The signed-in account. Lives beside <c>DeviceIdentity</c>'s token in <c>PlayerPrefs</c>
        /// rather than in <c>accounts.json</c>, because it is the one piece of this that does not
        /// migrate: the registry becomes a server table, while "who is signed in" becomes a session
        /// token in this same slot.
        /// </summary>
        private const string ActivePrefsKey = "kingdom.account.active";
        private const string FormerActivePrefsKey = "emberfall.account.active";

        private static LocalAccount _active;

        /// <summary>Where every account's scope folder sits — and where <c>accounts.json</c> sits, one level up.</summary>
        public static string Root => Application.persistentDataPath;

        private static JsonLocalAccountStore Store() => JsonLocalAccountStore.InRoot(Root);

        /// <summary>
        /// Every account on this device, first-created first, with the registry created on first touch.
        /// Reads two small files; not cached, for the same reason the roster isn't.
        /// </summary>
        /// <exception cref="CollectionFormatException">The registry exists but cannot be read.</exception>
        public static IReadOnlyList<LocalAccount> All()
            => LocalAccountRoster.EnsureDefault(Store(), DeviceIdentity.Token);

        /// <summary>
        /// The account this session is playing as. Cached — every screen that draws a nameplate asks,
        /// and it only changes through <see cref="SwitchTo"/>, which clears it.
        ///
        /// <para>A pointer naming an account that no longer exists falls back to the first one rather
        /// than failing a boot. That is a real case and not a corruption: forgetting an account leaves
        /// the pointer behind.</para>
        /// </summary>
        /// <exception cref="CollectionFormatException">The registry exists but cannot be read.</exception>
        public static LocalAccount Active
        {
            get
            {
                if (_active != null) return _active;

                var all = All();
                string wanted = RenamedPrefs.GetString(ActivePrefsKey, FormerActivePrefsKey);
                var found = LocalAccountRoster.Find(all, wanted);

                if (found == null && !string.IsNullOrEmpty(wanted))
                    Debug.LogWarning($"[DeviceAccounts] active account '{wanted}' is no longer in the " +
                                     "registry; falling back to the first account.");

                _active = found ?? all[0];
                return _active;
            }
        }

        /// <summary>Drop the cached active account so the next read re-resolves it.</summary>
        public static void Invalidate()
        {
            _active = null;
            PlayerAccount.InvalidateLocal();
            // The Supabase session is stored per local account (EF-074e), so a switch that left the
            // provider cached would hand the new account the old one's sign-in.
            AuthGateway.Invalidate();
            // The sync writes into PlayerAccount.Local, which is scoped to the signed-in account — a
            // gateway that outlived a switch would write one player's collection into another's folder.
            CollectionGateway.Invalidate();
            // Same hazard, worse consequence: the roster gateway REPLACES characters and decks.
            RosterGateway.Invalidate();
            // Nothing of this one's is written to disk, but it holds a transport pointed at a host that
            // BaseUrl can move underneath it, and it spends entitlements.
            PackGateway.Invalidate();
            FriendsGateway.Invalidate();
            // The social socket is this account being online to its friends; it must not outlive it.
            SocialGateway.Invalidate();
        }

        /// <summary>
        /// This seat's identity on the match wire (<c>HelloMsg.DeviceToken</c>), guaranteed not to
        /// throw.
        ///
        /// <para>A corrupt registry must not be able to stop this machine joining a match, and the
        /// honest degradation is the identity it had before accounts existed — so the fallback is
        /// <see cref="DeviceIdentity.Token"/>, which is also the first account's token.</para>
        /// </summary>
        public static string WireToken()
        {
            try
            {
                return Active.WireToken;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeviceAccounts] account registry unreadable ({ex.GetType().Name}: " +
                                 $"{ex.Message}); joining as the device token.");
                return DeviceIdentity.Token;
            }
        }

        /// <summary>
        /// A label for listing accounts: this account's chosen display name, or a positional fallback for
        /// one that has never been named. Reads that account's profile off disk — never throws, since a
        /// list of accounts must still render when one of them is unreadable.
        /// </summary>
        public static string LabelOf(LocalAccount account, int index)
        {
            if (account == null) return "—";
            try
            {
                var profile = JsonProfileStore.InDirectory(Root, account.Id).Load();
                if (profile.HasDisplayName) return profile.DisplayName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeviceAccounts] profile for account {index + 1} unreadable " +
                                 $"({ex.GetType().Name}); labelling it positionally.");
                return $"Account {index + 1} (unreadable)";
            }
            return $"Account {index + 1} (unnamed)";
        }

        /// <summary>
        /// Mint a new account. It starts with nothing — no profile, no roster — so whoever signs into
        /// it lands in the same first-run flow a fresh install gets. It does not switch to it; callers
        /// do that with <see cref="SwitchTo"/>.
        ///
        /// <para>Two ids, minted separately. <see cref="LocalAccount.Id"/> names a directory and is
        /// logged; <see cref="LocalAccount.WireToken"/> is a bearer credential and is not. Only the
        /// grandfathered first account conflates them, and only because its folder was already named
        /// after the device token.</para>
        /// </summary>
        private static LocalAccount Create()
        {
            var account = new LocalAccount(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            Store().Save(account);
            return account;
        }

        /// <summary>
        /// Sign in as another account. Persists immediately — an unsaved switch that dies with the
        /// process would put the next launch on a different account than the one on screen.
        ///
        /// <para><b>Callers must reload the scene afterwards.</b> This clears the two caches it owns,
        /// but a live screen is holding presenters, nameplates and a bound deckbuilder built from the
        /// previous account. Chasing every one of those is how a switch half-happens; reloading is one
        /// line and cannot be incomplete.</para>
        /// </summary>
        private static void SwitchTo(string accountId)
        {
            if (string.IsNullOrEmpty(accountId))
                throw new ArgumentException("Cannot switch to an empty account id.", nameof(accountId));

            PlayerPrefs.SetString(ActivePrefsKey, accountId);
            PlayerPrefs.Save();
            Invalidate();
        }

        /// <summary>
        /// Point this device's registry at the identity that just signed in, and make that account
        /// the active one. Returns the account now in force.
        ///
        /// <para><b>This is the wire that was missing, and its absence is what made a reinstall look
        /// like data loss.</b> The server has always been portable — <c>MatchGate</c> verifies the
        /// Supabase JWT, hashes the subject and resolves a stable <c>player_id</c> that the entitlement
        /// ledger is keyed on, so what a player OWNS already survives anything. The collection did not:
        /// <see cref="Active"/> resolved to an id minted on the device, with nothing anywhere
        /// connecting it to the signed-in identity. Same email, same server account, different folder.
        /// Measured on device 2026-08-29 — <c>ad7278eb…</c> before an uninstall and <c>44f5749f…</c>
        /// after, with the name and roster stranded in the first.</para>
        ///
        /// <para><b>It binds by POINTER, never by rename</b> — see <see cref="LocalAccount.AuthUserId"/>.
        /// Scoping the folder by the authenticated id would be tidier and would relocate a collection
        /// that is the only copy in existence, which is the same class of damage a changed
        /// <c>productName</c> does.</para>
        ///
        /// <para><b>What it cannot do.</b> Binding makes the folder findable again; it does not put
        /// files back into one the OS deleted with the app. On a reinstalled device this lands on an
        /// empty scope, and the player's roster and decks are genuinely gone — only the ledger and
        /// (via <c>AuthSession.DisplayName</c>) their name come back. Cloud save is a separate thing
        /// and this is not a substitute for it.</para>
        ///
        /// <para>Safe to call on every sign-in: the ordinary case is an account that already carries
        /// this identity, which writes nothing.</para>
        /// </summary>
        /// <param name="authUserId">The signed-in Supabase user id. An empty one is a no-op.</param>
        public static LocalAccount BindToIdentity(string authUserId)
        {
            if (string.IsNullOrEmpty(authUserId)) return Active;

            var all = All();
            var plan = LocalAccountRoster.PlanBinding(all, Active.Id, authUserId, out var target);

            switch (plan)
            {
                case LocalAccountRoster.BindingPlan.SwitchToExisting:
                    if (target!.Id == Active.Id) return Active;
                    Debug.Log($"[DeviceAccounts] this identity owns account '{target.Id}'; switching to it.");
                    SwitchTo(target.Id);
                    return Active;

                case LocalAccountRoster.BindingPlan.AdoptCurrent:
                    Debug.Log($"[DeviceAccounts] binding account '{target!.Id}' to the signed-in identity.");
                    Store().Save(target.ClaimedBy(authUserId));
                    Invalidate();
                    return Active;

                default:
                    // Every account here belongs to somebody else. Minting is the caller's job in the
                    // core assembly and this one's here, because only Unity has a GUID to spend.
                    var minted = Create().ClaimedBy(authUserId);
                    Store().Save(minted);
                    Debug.Log($"[DeviceAccounts] no account on this device belongs to the signed-in " +
                              $"identity; minted '{minted.Id}'.");
                    SwitchTo(minted.Id);
                    return Active;
            }
        }

        /// <summary>
        /// <see cref="BindToIdentity"/>, guaranteed not to throw. A corrupt registry must not be able to turn a successful sign-in into a failed launch —
        /// the honest degradation is the unbound account the device was already playing, which is
        /// precisely today's behaviour.
        /// </summary>
        public static void TryBindToIdentity(string authUserId)
        {
            try { BindToIdentity(authUserId); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeviceAccounts] could not bind this device to the signed-in " +
                                 $"identity ({ex.GetType().Name}: {ex.Message}); staying on the " +
                                 "current account.");
            }
        }
    }
}
