using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// JSON-file implementations of <see cref="IDeckStore"/> and <see cref="IProfileStore"/>
    /// (dev-docs/data/player-data-and-telemetry.md:11).
    ///
    /// <para><b><c>JsonCharacterStore</c> stood first in this file until 2026-09-04</b> and was
    /// deleted with the type it persisted (ADR-0009). The rationale below was written as its doc
    /// and pointed at from the other two stores; it is unchanged and now leads the file, because
    /// it was never about characters.</para>
    ///
    /// <para><b>The path is a constructor argument, not a lookup.</b> That is the whole reason this
    /// compiles without UnityEngine: the Unity side composes
    /// <c>CollectionPaths.Decks(Application.persistentDataPath, DeviceIdentity.Token)</c> and
    /// hands the result in. Tests hand in a temp directory. Neither the assembly nor the store knows
    /// which it got.</para>
    ///
    /// <para><b>No cache.</b> Every call reads the file. A collection is a handful of kilobytes read
    /// on screen transitions, so the cost is invisible, and the alternative — an in-memory copy that
    /// a second store instance silently overwrites — is the kind of bug that presents as "my deck
    /// disappeared" a week later.</para>
    ///
    /// <para><b>Writes are atomic.</b> Serialize to a sibling <c>.tmp</c>, then replace. A process
    /// killed mid-write leaves the previous collection intact rather than a truncated file that
    /// reads as corrupt on next launch.</para>
    /// </summary>
    /// <summary>
    /// JSON-file <see cref="IDeckStore"/>. Path, caching and atomicity are the file-level doc above.
    ///
    /// <para><b>The store never validates a deck's CONTENTS.</b> It stores what it is given and
    /// returns what it stored, including decks holding a card a balance patch removed. That is
    /// §3's explicit requirement: those decks must surface as invalid on open, which they can only
    /// do if they survive the trip. Legality is <see cref="DeckValidator"/>'s job, and it is the
    /// caller's job to run it. The PAIRING is the one exception — see <see cref="Save"/>.</para>
    /// </summary>
    public sealed class JsonDeckStore : IDeckStore
    {
        private readonly string _filePath;

        public JsonDeckStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Deck store needs a file path.", nameof(filePath));
            _filePath = filePath;
        }

        public static JsonDeckStore InDirectory(string rootDirectory, string storageKey)
            => new JsonDeckStore(CollectionPaths.Decks(rootDirectory, storageKey));

        public string FilePath => _filePath;

        public IReadOnlyList<DeckList> LoadAll()
        {
            string? json = JsonStoreFile.ReadOrNull(_filePath);
            return json == null ? Array.Empty<DeckList>() : CollectionJson.ReadDecks(json);
        }

        /// <summary>
        /// Upsert by <see cref="DeckList.Id"/>, replacing in place so saved decks keep their order.
        ///
        /// <para><b>Rejects an invalid pairing</b> — the one guard this store applies to a deck, and
        /// it moved here from <c>JsonCharacterStore.Save</c> with the pairing itself (ADR-0009). The
        /// reasoning is unchanged: persisting a Pyra+Pyra deck writes a state with no legal repair,
        /// because the pairing is immutable by design and the player could only delete the deck.
        /// Loading such a row still works (see <see cref="CollectionJson"/>); the check belongs where
        /// the mistake is still fixable, which is on the way in.</para>
        /// </summary>
        public void Save(DeckList deck)
        {
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            if (string.IsNullOrWhiteSpace(deck.Id))
                throw new ArgumentException("Deck needs a non-empty Id to be stored.", nameof(deck));
            if (!deck.HasValidPairing)
                throw new ArgumentException(
                    $"'{deck.PrimaryClass}/{deck.SecondaryClass}' is not a valid pairing " +
                    "(both classes must be real and distinct).", nameof(deck));

            var all = new List<DeckList>(LoadAll());
            int existing = IndexOf(all, deck.Id);
            if (existing >= 0) all[existing] = deck; else all.Add(deck);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteDecks(all));
        }

        public void Delete(string deckId)
        {
            if (string.IsNullOrEmpty(deckId)) return;
            var all = new List<DeckList>(LoadAll());
            int existing = IndexOf(all, deckId);
            if (existing < 0) return;
            all.RemoveAt(existing);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteDecks(all));
        }

        /// <summary>No <see cref="LoadAll"/> — see <see cref="IDeckStore.ReplaceAll"/>. Reading here
        /// would reintroduce the exact deadlock this exists to break.</summary>
        public void ReplaceAll(IReadOnlyList<DeckList> decks)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));
            JsonStoreFile.Write(_filePath, CollectionJson.WriteDecks(decks));
        }

        private static int IndexOf(List<DeckList> all, string id)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>
    /// JSON-file <see cref="IProfileStore"/> — one object in one file. Same path, caching and
    /// atomicity story as the rest of this file; see the file-level doc.
    /// </summary>
    public sealed class JsonProfileStore : IProfileStore
    {
        private readonly string _filePath;

        public JsonProfileStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Profile store needs a file path.", nameof(filePath));
            _filePath = filePath;
        }

        public static JsonProfileStore InDirectory(string rootDirectory, string storageKey)
            => new JsonProfileStore(CollectionPaths.Profile(rootDirectory, storageKey));

        public string FilePath => _filePath;

        /// <summary>
        /// The stored profile, or an unnamed one on first run. Never null — see
        /// <see cref="IProfileStore"/> for why that is the contract rather than a convenience.
        /// An unreadable file still throws <see cref="CollectionFormatException"/>: returning
        /// "unnamed" from a corrupt file would prompt for a name and then overwrite a recoverable one.
        /// </summary>
        public PlayerProfile Load()
        {
            string? json = JsonStoreFile.ReadOrNull(_filePath);
            return json == null ? new PlayerProfile() : CollectionJson.ReadProfile(json);
        }

        /// <summary>
        /// Write the profile, refusing a name that <see cref="PlayerName"/> rejects. Clearing the name
        /// (empty) is allowed and means "ask me again" — it is the one non-name this accepts.
        ///
        /// <para>Validating here rather than trusting the caller is the same call
        /// <see cref="JsonDeckStore.Save"/> makes about a pairing: this is the last point at
        /// which the mistake is still fixable. It also means every future writer — a settings screen,
        /// an import, a migration — is gated by construction instead of by remembering.</para>
        /// </summary>
        public void Save(PlayerProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // ── VULN-002 (dev-docs/quality/security.md) — ARMED AND MITIGATED as of EF-073. This line
            //    used to carry the "do not arm this" pointer; the name now rides HelloMsg, so what it
            //    warns about has happened and the warning has been replaced by the fact.
            //
            //    WHAT CHANGED FOR THIS LINE: nothing about its behaviour, and everything about what it
            //    proves. This check runs on the SENDER before a local save — it is a courtesy to the
            //    player typing, not a security control, because an attacker edits profile.json or
            //    patches the client and never reaches this code at all. The control is
            //    MatchGate.HandleFreshAsync, which re-runs the IDENTICAL PlayerName.Check on arrival.
            //
            //    SO: do not read "the name is validated on save" as "the name is validated". VULN-002
            //    §5 names that exact inference as how this ships by accident — a sweep sees a
            //    validator, tests, and a call on every save, and stops looking.
            var check = PlayerName.Check(profile.DisplayName);
            if (profile.HasDisplayName && !check.IsValid)
                throw new ArgumentException(
                    $"'{profile.DisplayName}' is not a usable display name: {check.Reason}", nameof(profile));

            // Store the normalized form, not what was handed in — otherwise two players who typed the
            // same name hold different bytes, and a later uniqueness check would call them different.
            var stored = new PlayerProfile(
                profile.HasDisplayName ? check.Normalized : "",
                profile.LastPlayedDeckId);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteProfile(stored));

            // The caller's object is the one the UI is still holding; leaving it un-normalized would
            // show a name that differs from the one on disk until the next load.
            profile.DisplayName = stored.DisplayName;
        }
    }

    /// <summary>
    /// JSON-file <see cref="ILocalAccountStore"/>. Same caching and atomicity story as the rest of
    /// this file; see the file-level doc.
    ///
    /// <para><b>The one store built from a root alone</b> — <see cref="InRoot"/> takes no storage key,
    /// because this file is what the storage keys come from. That asymmetry is the whole reason the
    /// registry is a separate file at a separate level rather than a header in one of the others.</para>
    /// </summary>
    public sealed class JsonLocalAccountStore : ILocalAccountStore
    {
        private readonly string _filePath;

        public JsonLocalAccountStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Account store needs a file path.", nameof(filePath));
            _filePath = filePath;
        }

        /// <summary>The registry for a collection root. Note the missing storage key — see the type remarks.</summary>
        public static JsonLocalAccountStore InRoot(string rootDirectory)
            => new JsonLocalAccountStore(CollectionPaths.Accounts(rootDirectory));

        public string FilePath => _filePath;

        /// <summary>
        /// Every account, in creation order. A missing file is an empty registry — which is every
        /// device that was played before this existed, not an error, and the case
        /// <see cref="LocalAccountRoster.EnsureDefault"/> is written for.
        /// </summary>
        public IReadOnlyList<LocalAccount> LoadAll()
        {
            string? json = JsonStoreFile.ReadOrNull(_filePath);
            return json == null ? Array.Empty<LocalAccount>() : CollectionJson.ReadAccounts(json);
        }

        /// <summary>
        /// Upsert by <see cref="LocalAccount.Id"/>, preserving order — so the grandfathered account
        /// stays first and the switcher's list does not reshuffle under the player.
        ///
        /// <para>Refuses to let a second account claim an existing account's wire token. Nothing in
        /// this file's format prevents it and the caller mints both values, so this is the last point
        /// at which the mistake is fixable — the same call <see cref="JsonDeckStore.Save"/> makes
        /// about a pairing. Two accounts sharing a token would present as one account to the server
        /// and defeat the entire point of the second seat, while looking correct on disk.</para>
        /// </summary>
        public void Save(LocalAccount account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));

            var all = new List<LocalAccount>(LoadAll());
            int existing = IndexOf(all, account.Id);

            for (int i = 0; i < all.Count; i++)
            {
                if (i == existing) continue;
                if (all[i].WireToken == account.WireToken)
                    throw new ArgumentException(
                        $"Account '{all[i].Id}' already holds that wire token; two accounts sharing one " +
                        "token arrive at the server as the same account.", nameof(account));
            }

            if (existing >= 0) all[existing] = account; else all.Add(account);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteAccounts(all));
        }

        /// <summary>Forget an account, leaving its collection on disk. See <see cref="ILocalAccountStore.Delete"/>.</summary>
        public void Delete(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return;
            var all = new List<LocalAccount>(LoadAll());
            int existing = IndexOf(all, accountId);
            if (existing < 0) return;
            all.RemoveAt(existing);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteAccounts(all));
        }

        private static int IndexOf(List<LocalAccount> all, string id)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>
    /// JSON-file <see cref="IOwnedCardStore"/> — one record in one file. Same path, caching and
    /// atomicity story as the rest of this file; see the file-level doc.
    ///
    /// <para><b>The one store whose Load can return null, and it must stay that way.</b> Every sibling
    /// here turns a missing file into an empty-but-valid value, because "no decks" and "no profile"
    /// are the same statement as "none stored". Ownership is not: an absent file means nobody has ever
    /// told this account what it owns, while an empty file means it owns nothing and every deck it holds
    /// is a draft. Collapsing the two would make the first launch after L6 landed lock every player out
    /// of the queue. <see cref="IOwnedCardStore"/> carries the full rationale.</para>
    /// </summary>
    public sealed class JsonOwnedCardStore : IOwnedCardStore
    {
        private readonly string _filePath;

        public JsonOwnedCardStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Owned-card store needs a file path.", nameof(filePath));
            _filePath = filePath;
        }

        public static JsonOwnedCardStore InDirectory(string rootDirectory, string storageKey)
            => new JsonOwnedCardStore(CollectionPaths.OwnedCards(rootDirectory, storageKey));

        public string FilePath => _filePath;

        /// <summary>
        /// The stored record, or null when this account has never had one. An unreadable file still
        /// throws <see cref="CollectionFormatException"/> rather than reading as absent: "unknown"
        /// unlocks the full pool, and silently unlocking it off the back of a corrupt file is the
        /// entitlement bypass this store exists to prevent.
        /// </summary>
        public OwnedCardCounts? Load()
        {
            string? json = JsonStoreFile.ReadOrNull(_filePath);
            return json == null ? null : CollectionJson.ReadOwnedCards(json);
        }

        /// <summary>
        /// Replace the record wholesale. Not an upsert, unlike every sibling store here: ownership is a
        /// projection of a grant history the client does not hold, so a merge would preserve rows the
        /// authority has since revoked — a dusted card would stay owned forever.
        /// </summary>
        public void Save(OwnedCardCounts owned)
        {
            if (owned == null) throw new ArgumentNullException(nameof(owned));
            JsonStoreFile.Write(_filePath, CollectionJson.WriteOwnedCards(owned));
        }
    }

    /// <summary>The file plumbing the stores share: read-or-null, and a crash-safe write.</summary>
    internal static class JsonStoreFile
    {
        /// <summary>Contents, or null when the file has never been written. UTF-8, no BOM.</summary>
        internal static string? ReadOrNull(string path)
            => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

        /// <summary>
        /// Write via a sibling temp file, then swap it into place. <see cref="File.Replace(string, string, string)"/>
        /// is the atomic path where the platform has one; where it doesn't (some mobile filesystems
        /// raise <see cref="PlatformNotSupportedException"/> or a plain <see cref="IOException"/>) we
        /// fall back to delete-then-move, which is a narrower window than writing the target directly.
        /// </summary>
        internal static void Write(string path, string contents)
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            if (dir.Length > 0) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            try
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException)
            {
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
    }
}
