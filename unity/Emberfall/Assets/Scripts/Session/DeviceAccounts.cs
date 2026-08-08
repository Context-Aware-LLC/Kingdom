using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// Which player is sitting at this machine, and which others exist on it
    /// (Sam 2026-07-27: two accounts on one device, on a testing basis).
    ///
    /// <para><b>Why this exists.</b> Sam cannot test the deckbuilder as two people without two
    /// collections and two wire identities on one machine. Everything underneath was already built for
    /// it — <see cref="CollectionPaths.ScopeDirectory"/> takes the identity as a parameter and
    /// <c>Two_identities_hold_two_profiles</c> has been green since the collection landed — so the only
    /// missing piece is a device that knows more than one identity. That is all this class is.</para>
    ///
    /// <para><b>The split with <see cref="DeviceIdentity"/> is load-bearing.</b> That token is minted
    /// once and persisted forever, and it must stay that way: C-G1-8 turns on every telemetry row
    /// already emitted being attributable, and re-minting orphans all of them. So it is never
    /// rewritten. Instead the first account <i>adopts</i> it — same folder, same wire token, byte for
    /// byte what this machine did yesterday — and accounts minted afterwards get their own. A release
    /// build on a fresh machine ends up with exactly one account holding the device token, which is
    /// today's behaviour reached by the new path.</para>
    ///
    /// <para><b>One code path, deliberately.</b> The registry is compiled and run in every
    /// configuration; only the create/switch <i>affordance</i> is gated to editor and development
    /// builds. Gating the mechanism too would leave the release path untested, which is precisely the
    /// defect that hid an unregistered background from four shipped assets: a branch that only runs in
    /// the configuration nobody tests is not a fallback.</para>
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
        private const string ActivePrefsKey = "emberfall.account.active";

        /// <summary>
        /// Launch argument that pins this process to one account: <c>-emberfallAccount 2</c> (a 1-based
        /// position) or <c>-emberfallAccount &lt;id&gt;</c>.
        ///
        /// <para><b>This is what makes two seats on one machine actually work.</b> PlayerPrefs and
        /// <see cref="Application.persistentDataPath"/> are both keyed on companyName/productName, so
        /// two processes of the same product share them. Sharing the data path is the point — one
        /// <c>accounts.json</c>, visible to both. Sharing the <i>active pointer</i> is not: without an
        /// override, two instances resolve to the same account, and whichever one switches last wins
        /// for both. That applies to two copies of a build and, as far as we have verified, to
        /// Multiplayer Play Mode virtual players too, which share the same product identity.</para>
        ///
        /// <para><b>Read-only, and never written back.</b> Persisting it would clobber the other seat's
        /// pointer through the very prefs store they share, which is the bug this exists to avoid.</para>
        /// </summary>
        private const string AccountArgument = "-emberfallAccount";

        private static LocalAccount _active;

        /// <summary>Where every account's scope folder sits — and where <c>accounts.json</c> sits, one level up.</summary>
        public static string Root => Application.persistentDataPath;

        private static JsonLocalAccountStore Store() => JsonLocalAccountStore.InRoot(Root);

        /// <summary>
        /// Every account on this device, first-created first, with the registry created on first touch.
        /// Reads two small files; not cached, for the same reason the roster isn't — a switcher showing
        /// a stale list is how a just-created account fails to appear.
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

                // A launch argument wins over the stored pointer — see AccountArgument for why the
                // stored pointer cannot distinguish two processes of the same product.
                var pinned = FromArgument(all);
                if (pinned != null)
                {
                    _active = pinned;
                    Debug.Log($"[DeviceAccounts] pinned to '{LabelOf(pinned, PositionOf(all, pinned))}' " +
                              $"by {AccountArgument}; the stored pointer is ignored for this process.");
                    return _active;
                }

                string wanted = PlayerPrefs.GetString(ActivePrefsKey, string.Empty);
                var found = LocalAccountRoster.Find(all, wanted);

                if (found == null && !string.IsNullOrEmpty(wanted))
                    Debug.LogWarning($"[DeviceAccounts] active account '{wanted}' is no longer in the " +
                                     "registry; falling back to the first account.");

                _active = found ?? all[0];
                return _active;
            }
        }

        /// <summary>
        /// The account named by <see cref="AccountArgument"/>, or null when it is absent. Accepts a
        /// 1-based position (easier to type, and what you actually know at launch) or a full account id.
        /// A value that matches nothing warns and falls through to the stored pointer rather than
        /// failing the boot — a typo in a launch argument should not look like a broken build.
        /// </summary>
        private static LocalAccount FromArgument(IReadOnlyList<LocalAccount> all)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], AccountArgument, StringComparison.OrdinalIgnoreCase)) continue;

                string value = args[i + 1];
                if (int.TryParse(value, out int position))
                {
                    if (position >= 1 && position <= all.Count) return all[position - 1];
                    Debug.LogWarning($"[DeviceAccounts] {AccountArgument} {position} — this device has " +
                                     $"{all.Count} account(s). Ignoring.");
                    return null;
                }

                var byId = LocalAccountRoster.Find(all, value);
                if (byId != null) return byId;
                Debug.LogWarning($"[DeviceAccounts] {AccountArgument} '{value}' matches no account. Ignoring.");
                return null;
            }
            return null;
        }

        /// <summary>0-based position in the roster, or 0 when absent — <c>IReadOnlyList</c> has no IndexOf.</summary>
        private static int PositionOf(IReadOnlyList<LocalAccount> all, LocalAccount account)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Id == account.Id) return i;
            return 0;
        }

        /// <summary>True when a launch argument pinned this process; the switcher cannot override it.</summary>
        public static bool IsPinnedByArgument => FromArgumentSafe() != null;

        private static LocalAccount FromArgumentSafe()
        {
            try { return FromArgument(All()); }
            catch { return null; }
        }

        /// <summary>Drop the cached active account so the next read re-resolves it.</summary>
        public static void Invalidate()
        {
            _active = null;
            PlayerAccount.InvalidateLocal();
            // The Supabase session is stored per local account (EF-074e), so a switch that left the
            // provider cached would hand seat two seat one's sign-in.
            AuthGateway.Invalidate();
        }

        /// <summary>
        /// This seat's identity on the match wire (<c>HelloMsg.DeviceToken</c>), guaranteed not to
        /// throw.
        ///
        /// <para>A corrupt registry must not be able to stop this machine joining a match, and the
        /// honest degradation is the identity it had before accounts existed — so the fallback is
        /// <see cref="DeviceIdentity.Token"/>, which is also the first account's token. The failure
        /// mode is therefore "both seats look like one account again", which is exactly the state this
        /// feature was built to fix and is visible the moment it happens.</para>
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
        /// A label for the switcher: this account's chosen display name, or a positional fallback for
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
        /// Mint a new account. It starts with nothing — no profile, no roster — so whichever process
        /// signs into it lands in the same first-run flow a fresh install gets.
        ///
        /// <para><b>It does NOT switch to it, and that is the whole point.</b> This originally did, on
        /// the reasoning that someone creating an account wants to be in it. That is wrong for the case
        /// this feature exists to serve: an account is usually created *for the other seat*, and
        /// switching moved the creating process instead — then the second process, sharing the same
        /// prefs store, came up on the new account too. Both seats landed on one account and wrote two
        /// characters into it before anyone noticed (2026-07-27). Creating a seat must never move the
        /// seat you are sitting in; use <see cref="SwitchTo"/> deliberately, or pin the other process
        /// with <c>-emberfallAccount</c>.</para>
        ///
        /// <para>Two ids, minted separately. <see cref="LocalAccount.Id"/> names a directory and is
        /// logged; <see cref="LocalAccount.WireToken"/> is a bearer credential and is not. Only the
        /// grandfathered first account conflates them, and only because its folder was already named
        /// after the device token.</para>
        /// </summary>
        public static LocalAccount Create()
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
        /// line and cannot be incomplete. <c>MainMenuController</c> does it.</para>
        /// </summary>
        public static void SwitchTo(string accountId)
        {
            if (string.IsNullOrEmpty(accountId))
                throw new ArgumentException("Cannot switch to an empty account id.", nameof(accountId));

            // Refuse rather than write. This process's account came from a launch argument, and the
            // prefs store it would write to is SHARED with the other seat — so switching here would
            // silently move the other process's account while leaving this one pinned where it was.
            if (IsPinnedByArgument)
                throw new InvalidOperationException(
                    $"This process is pinned by {AccountArgument}; change the launch argument instead. " +
                    "Writing the stored pointer here would move the other seat's account, not this one.");

            PlayerPrefs.SetString(ActivePrefsKey, accountId);
            PlayerPrefs.Save();
            Invalidate();
        }

        /// <summary>
        /// Forget an account, leaving its collection on disk (<see cref="ILocalAccountStore.Delete"/> —
        /// a mis-click on a testing affordance must not destroy a real roster). Refuses to forget the
        /// account currently signed in, and refuses to leave the device with none.
        /// </summary>
        public static void Forget(string accountId)
        {
            var all = All();
            if (all.Count <= 1)
                throw new InvalidOperationException("A device keeps at least one account.");
            if (accountId == Active.Id)
                throw new InvalidOperationException("Switch to another account before forgetting this one.");

            Store().Delete(accountId);
            Invalidate();
        }
    }
}
