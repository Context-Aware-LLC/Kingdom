using System.IO;
using System.Text;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// Where a player's collection lives on disk (dev-docs/data/player-data-and-telemetry.md:11:
    /// keyed by the guest device token until identity lands).
    ///
    /// <para>The <b>convention</b> lives here — in the UnityEngine-free assembly — while the
    /// <b>root directory</b> is supplied by the caller. Unity passes
    /// <c>Application.persistentDataPath</c> and <c>DeviceIdentity.Token</c>; a headless test or a
    /// future server-side importer passes a temp directory and an account id. Neither side hardcodes
    /// the layout, so both agree on it.</para>
    ///
    /// <para>Scoping by token is what makes the M3 account migration a copy: today's folder is one
    /// guest, and a converted account keeps its own folder rather than merging into a shared file.</para>
    /// </summary>
    public static class CollectionPaths
    {
        /// <summary>
        /// The LEGACY roster file. Nothing writes it any more (ADR-0009, 2026-09-04) and nothing in
        /// this assembly reads it at all — the one reader left is the <c>CollectionMigrate</c> TOOL
        /// (<c>src/Emberfall.Tools.CollectionMigrate</c>), which needs the name in order to find the
        /// file, inherit its pairings onto the decks, and delete it.
        ///
        /// RETIREMENT TRIGGER: delete this and <see cref="Characters"/> when that tool goes. They
        /// have no other caller, and a path helper for a file nothing produces is a trap.
        /// </summary>
        public const string CharactersFileName = "characters.json";
        public const string DecksFileName = "decks.json";

        /// <summary>
        /// The player behind the roster (<see cref="PlayerProfile"/>). A third file rather than a
        /// header in <c>decks.json</c>: the deck list is rewritten on every deck save, and a
        /// name has no reason to be at risk of a roster write.
        /// </summary>
        public const string ProfileFileName = "profile.json";

        /// <summary>
        /// The accounts on this device (<see cref="LocalAccount"/>). <b>Lives at the root, not in a
        /// scope</b> — you have to know which account you are before you can open a scope directory,
        /// so the list of them cannot be inside one.
        /// </summary>
        public const string AccountsFileName = "accounts.json";

        /// <summary>
        /// What this account owns, per card id (<see cref="IOwnedCardStore"/>). A fourth file in the
        /// scope rather than a section of any existing one, for the reason the profile is its own file:
        /// ownership changes every time a pack is opened and the roster does not, and neither should be
        /// at risk of the other's write. <b>Its absence is meaningful</b> — see
        /// <see cref="IOwnedCardStore"/> for why "no file" is not "owns nothing".
        /// </summary>
        public const string OwnedCardsFileName = "owned-cards.json";

        /// <summary>Longest storage key we will turn into a directory name (a UUID is 36).</summary>
        private const int MaxKeyLength = 64;

        /// <summary>The per-identity folder: <c>&lt;root&gt;/&lt;sanitized key&gt;</c>.</summary>
        public static string ScopeDirectory(string rootDirectory, string storageKey)
            => Path.Combine(rootDirectory, SanitizeKey(storageKey));

        public static string Characters(string rootDirectory, string storageKey)
            => Path.Combine(ScopeDirectory(rootDirectory, storageKey), CharactersFileName);

        public static string Decks(string rootDirectory, string storageKey)
            => Path.Combine(ScopeDirectory(rootDirectory, storageKey), DecksFileName);

        public static string Profile(string rootDirectory, string storageKey)
            => Path.Combine(ScopeDirectory(rootDirectory, storageKey), ProfileFileName);

        public static string OwnedCards(string rootDirectory, string storageKey)
            => Path.Combine(ScopeDirectory(rootDirectory, storageKey), OwnedCardsFileName);

        /// <summary>
        /// The device's account registry — the one collection file that takes no storage key, because
        /// it is what tells you what the storage keys are.
        /// </summary>
        public static string Accounts(string rootDirectory)
            => Path.Combine(rootDirectory, AccountsFileName);

        /// <summary>
        /// Reduce a storage key to a safe single path segment: <c>[A-Za-z0-9-_]</c> survives,
        /// everything else becomes <c>_</c>, and the result is capped at <see cref="MaxKeyLength"/>.
        ///
        /// <para>Today's key is a locally-minted GUID, so nothing here is reachable. It is written
        /// anyway because the whole point of taking the key by parameter is that the caller decides
        /// what it is — and the day it becomes an account id from a server response, a key of
        /// <c>"../../"</c> would otherwise write outside the collection root. A guard on the way in
        /// costs ten lines; noticing later that it was missing costs an incident.</para>
        /// </summary>
        public static string SanitizeKey(string storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey))
                throw new System.ArgumentException("Storage key must be a non-empty identity.", nameof(storageKey));

            var sb = new StringBuilder(storageKey.Length);
            foreach (char c in storageKey)
            {
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                            || (c >= '0' && c <= '9') || c == '-' || c == '_';
                sb.Append(safe ? c : '_');
                if (sb.Length >= MaxKeyLength) break;
            }
            return sb.ToString();
        }
    }
}
