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
        /// <para>Decks bound to it are deliberately left alone. They surface as L5-invalid on open
        /// (§3), which is a repairable state the player can see; cascading the delete would silently
        /// destroy thirty cards of work to tidy up a dangling id.</para>
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

    /// <summary>The file plumbing both stores share: read-or-null, and a crash-safe write.</summary>
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
