using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// JSON-file implementations of <see cref="ICharacterStore"/> and <see cref="IDeckStore"/>
    /// (ui/collection-deckbuilder.md §3 — local, on-device, keyed by the guest device token).
    ///
    /// <para><b>The path is a constructor argument, not a lookup.</b> That is the whole reason this
    /// compiles without UnityEngine: the Unity side composes
    /// <c>CollectionPaths.Characters(Application.persistentDataPath, DeviceIdentity.Token)</c> and
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
    public sealed class JsonCharacterStore : ICharacterStore
    {
        private readonly string _filePath;

        public JsonCharacterStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Character store needs a file path.", nameof(filePath));
            _filePath = filePath;
        }

        /// <summary>Convenience for the common composition: a root directory scoped by identity key.</summary>
        public static JsonCharacterStore InDirectory(string rootDirectory, string storageKey)
            => new JsonCharacterStore(CollectionPaths.Characters(rootDirectory, storageKey));

        public string FilePath => _filePath;

        /// <summary>
        /// Every stored character, in stored order. A missing file is an empty roster (first run),
        /// not an error; an unreadable file throws <see cref="CollectionFormatException"/> rather
        /// than returning empty, because "empty" would be erased by the next Save.
        /// </summary>
        public IReadOnlyList<Character> LoadAll()
        {
            string? json = JsonStoreFile.ReadOrNull(_filePath);
            return json == null ? Array.Empty<Character>() : CollectionJson.ReadCharacters(json);
        }

        /// <summary>
        /// Upsert by <see cref="Character.Id"/>, replacing in place so the roster keeps its order.
        ///
        /// <para>Rejects an invalid pairing. <see cref="Character.IsValidPairing"/> leaves the call to
        /// its callers, and this caller's answer is no: persisting a Pyra+Pyra character writes a
        /// state with no legal repair — pairing is immutable by design, so the player could only
        /// delete it. Loading such a row still works (see <see cref="CollectionJson"/>); the check
        /// belongs where the mistake is still fixable.</para>
        /// </summary>
        public void Save(Character character)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (string.IsNullOrWhiteSpace(character.Id))
                throw new ArgumentException("Character needs a non-empty Id to be stored.", nameof(character));
            if (!Character.IsValidPairing(character.PrimaryClass, character.SecondaryClass))
                throw new ArgumentException(
                    $"'{character.PrimaryClass}/{character.SecondaryClass}' is not a valid pairing " +
                    "(both classes must be real and distinct).", nameof(character));

            var all = new List<Character>(LoadAll());
            int existing = IndexOf(all, character.Id);
            if (existing >= 0) all[existing] = character; else all.Add(character);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteCharacters(all));
        }

        /// <summary>
        /// Remove a character. Deleting one that isn't there is a no-op, not an error — Delete is
        /// idempotent so a retried or double-clicked confirm cannot fail.
        ///
        /// <para>Touches the roster only — this store never reaches into the deck store. Deleting a
        /// character DOES destroy its decks (§3, Sam 2026-07-26), but that cascade is a composition
        /// over both stores and lives in <see cref="CharacterDeletion.Delete"/>, which is what a
        /// caller should be using. Calling this directly leaves the decks orphaned, reportable as
        /// L5-invalid, and sweepable by a later <see cref="CharacterDeletion.Delete"/> on the same id.</para>
        /// </summary>
        public void Delete(string characterId)
        {
            if (string.IsNullOrEmpty(characterId)) return;
            var all = new List<Character>(LoadAll());
            int existing = IndexOf(all, characterId);
            if (existing < 0) return;
            all.RemoveAt(existing);
            JsonStoreFile.Write(_filePath, CollectionJson.WriteCharacters(all));
        }

        private static int IndexOf(List<Character> all, string id)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>
    /// JSON-file <see cref="IDeckStore"/>. Same shape and same guarantees as
    /// <see cref="JsonCharacterStore"/> — see that type for the path, caching and atomicity rationale.
    ///
    /// <para><b>The store never validates a deck.</b> It stores what it is given and returns what it
    /// stored, including decks that reference a deleted character or a card a balance patch removed.
    /// That is §3's explicit requirement: those decks must surface as invalid on open, which they can
    /// only do if they survive the trip. Legality is <see cref="DeckValidator"/>'s job, and it is the
    /// caller's job to run it.</para>
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

        /// <summary>Upsert by <see cref="DeckList.Id"/>, replacing in place so saved decks keep their order.</summary>
        public void Save(DeckList deck)
        {
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            if (string.IsNullOrWhiteSpace(deck.Id))
                throw new ArgumentException("Deck needs a non-empty Id to be stored.", nameof(deck));

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

        private static int IndexOf(List<DeckList> all, string id)
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>
    /// JSON-file <see cref="IProfileStore"/> — one object in one file. Same path, caching and
    /// atomicity story as <see cref="JsonCharacterStore"/>; see that type.
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
        /// <see cref="JsonCharacterStore.Save"/> makes about a pairing: this is the last point at
        /// which the mistake is still fixable. It also means every future writer — a settings screen,
        /// an import, a migration — is gated by construction instead of by remembering.</para>
        /// </summary>
        public void Save(PlayerProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // ── VULN-001-adjacent (security/README.md §4, row "A player display name rendered on the
            //    opponent's client"). THIS IS THE LINE THAT ARMS IT. Today DisplayName is written and
            //    read on one device and rendered only in our own nameplate, so the validator below is
            //    belt-and-braces. The moment a display name is added to HelloMsg and carried into
            //    MatchStartMsg.Opponent.DisplayName, this string becomes peer-authored input rendered
            //    on OUR client — and a client that validates its own name proves nothing about the
            //    one the server hands it. Read VULN-002 before wiring that: the server must validate
            //    on arrival with these same rules, and the receiving nameplate must clip rather than
            //    trust a length.
            var check = PlayerName.Check(profile.DisplayName);
            if (profile.HasDisplayName && !check.IsValid)
                throw new ArgumentException(
                    $"'{profile.DisplayName}' is not a usable display name: {check.Reason}", nameof(profile));

            // Store the normalized form, not what was handed in — otherwise two players who typed the
            // same name hold different bytes, and a later uniqueness check would call them different.
            var stored = new PlayerProfile(profile.HasDisplayName ? check.Normalized : "");
            JsonStoreFile.Write(_filePath, CollectionJson.WriteProfile(stored));

            // The caller's object is the one the UI is still holding; leaving it un-normalized would
            // show a name that differs from the one on disk until the next load.
            profile.DisplayName = stored.DisplayName;
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
