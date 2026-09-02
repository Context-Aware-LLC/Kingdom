using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// The player, on this device: their name, their characters, and their decks
    /// (dev-docs/data/player-data-and-telemetry.md:11).
    ///
    /// <para><b>This class is the missing wire, and it is the only reason it exists.</b>
    /// <c>Emberfall.Core.Collection</c> shipped complete and green, and until now nothing under
    /// <c>Assets/</c> referenced a single line of it — so no collection file had ever been written on
    /// any machine. That assembly deliberately takes its storage root and its identity key as
    /// arguments and mints no ids, which leaves exactly one job undone: somebody has to know that the
    /// root is <see cref="Application.persistentDataPath"/>, the key is
    /// <see cref="DeviceIdentity.Token"/>, and an id is a GUID. That somebody is here, once, rather
    /// than at every call site.</para>
    ///
    /// <para><b>Sessions read through <see cref="Local"/>.</b> A constructor taking root and key stays
    /// public so an editor tool or a test scene can point at a scratch directory — the same seam the
    /// core assembly gives its stores, kept open for the same reason.</para>
    ///
    /// <para><b>Not a MonoBehaviour and not in a scene.</b> Identity must resolve the same way from
    /// the main menu, from a board boot that skipped it, and from an editor menu item. A scene object
    /// would mean the first two disagree about whether it exists.</para>
    /// </summary>
    public sealed class PlayerAccount
    {
        private static PlayerAccount _local;

        /// <summary>
        /// The signed-in player, composed on first touch from <see cref="DeviceAccounts.Root"/> and the
        /// active account's storage key. Both reads are main-thread-only Unity API, so this must not be
        /// touched from the network receive pump.
        ///
        /// <para>The key used to be <see cref="DeviceIdentity.Token"/> directly. It now comes from
        /// <see cref="DeviceAccounts"/>, which grandfathers that token onto the first account — so a
        /// machine that has been played resolves to the identical folder it always did, and a machine
        /// with a second account resolves to whichever one is signed in.</para>
        /// </summary>
        public static PlayerAccount Local =>
            _local ??= new PlayerAccount(DeviceAccounts.Root, DeviceAccounts.Active.Id);

        /// <summary>
        /// Drop the cached account. Called by <see cref="DeviceAccounts.SwitchTo"/> — a stale
        /// <see cref="Local"/> after a switch would write one player's character into another's file.
        /// </summary>
        public static void InvalidateLocal() => _local = null;

        private readonly ICharacterStore _characters;
        private readonly IDeckStore _decks;
        private readonly IProfileStore _profiles;
        private readonly IOwnedCardStore _ownedCards;
        private PlayerProfile _profile;

        /// <summary>The directory the scope folder sits in — <c>persistentDataPath</c> for the real account.</summary>
        public string RootDirectory { get; }

        /// <summary>The identity this collection is scoped by — the device token for the real account.</summary>
        public string StorageKey { get; }

        public PlayerAccount(string rootDirectory, string storageKey)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("A collection needs a root directory.", nameof(rootDirectory));

            RootDirectory = rootDirectory;
            StorageKey = storageKey;   // CollectionPaths.SanitizeKey rejects an empty one for us
            _characters = JsonCharacterStore.InDirectory(rootDirectory, storageKey);
            _decks = JsonDeckStore.InDirectory(rootDirectory, storageKey);
            _profiles = JsonProfileStore.InDirectory(rootDirectory, storageKey);
            _ownedCards = JsonOwnedCardStore.InDirectory(rootDirectory, storageKey);
        }

        /// <summary>Where this player's files live — worth logging once at boot, since nobody has ever seen them.</summary>
        public string ScopeDirectory => CollectionPaths.ScopeDirectory(RootDirectory, StorageKey);

        // ── Profile ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The player's profile, read once and held.
        ///
        /// <para><b>The one cached thing here</b>, against the stores' explicit no-cache rule. That
        /// rule exists because two store instances with private copies silently overwrite each other;
        /// a display name has exactly one writer in this process (<see cref="SetDisplayName"/>) and is
        /// read by every screen that draws a nameplate, so the trade runs the other way. If a second
        /// writer ever appears — an editor tool, a settings screen on another account — call
        /// <see cref="ReloadProfile"/> rather than adding a second cache.</para>
        /// </summary>
        public PlayerProfile Profile => _profile ??= _profiles.Load();

        /// <summary>Drop the cached profile so the next read comes off disk.</summary>
        public void ReloadProfile() => _profile = null;

        /// <summary>
        /// True when this device has never been asked for a name — the signal for the first-run
        /// prompt. Not an error state: a boot straight into a board scene has nowhere to ask.
        /// </summary>
        public bool NeedsDisplayName => !Profile.HasDisplayName;

        /// <summary>
        /// The name to render, or <paramref name="fallback"/> when there isn't one yet. Callers use
        /// this instead of inventing and saving a default — a saved "Player" is indistinguishable from
        /// a chosen one, and the prompt would then never fire again.
        /// </summary>
        public string DisplayNameOr(string fallback) => Profile.HasDisplayName ? Profile.DisplayName : fallback;

        /// <summary>
        /// This device's name for a nameplate, guaranteed not to throw.
        ///
        /// <para>A store refuses to read a corrupt collection (so the next write cannot overwrite
        /// something recoverable), which means <see cref="Local"/> can throw — and a boot path that
        /// only wanted a label for a nameplate must not die of it. The screen that can actually do
        /// something about a broken collection is the main menu, which reports it; every other caller
        /// draws the fallback and logs.</para>
        /// </summary>
        public static string LocalDisplayNameOr(string fallback)
        {
            try
            {
                return Local.DisplayNameOr(fallback);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerAccount] display name unavailable ({ex.GetType().Name}: {ex.Message}); " +
                                 $"using '{fallback}'.");
                return fallback;
            }
        }

        /// <summary>
        /// Validate and persist a display name. Returns the verdict so a text field can render the
        /// refusal reason; nothing is written unless it is valid, and the stored value is the
        /// normalized one (see <see cref="PlayerName"/>).
        /// </summary>
        public NameCheck SetDisplayName(string raw)
        {
            NameCheck check = PlayerName.Check(raw);
            if (!check.IsValid) return check;

            var profile = Profile;
            profile.DisplayName = check.Normalized;
            _profiles.Save(profile);          // re-validates and normalizes the object in place
            return check;
        }

        /// <summary>The character half of the last deck actually submitted into a match, or empty if none yet.</summary>
        public string LastPlayedCharacterId => Profile.LastPlayedCharacterId;

        /// <summary>The deck half of the last deck actually submitted into a match, or empty if none yet.</summary>
        public string LastPlayedDeckId => Profile.LastPlayedDeckId;

        /// <summary>
        /// Remember the character/deck a match was just started with, so Find Game can pre-arm the
        /// same pick next time instead of forcing a fresh Select (Sam, 2026-08-09). Call this once the
        /// pair is known good — this stores it as-is, with no eligibility check of its own.
        /// </summary>
        public void RecordLastPlayed(string characterId, string deckId)
        {
            if (string.IsNullOrEmpty(characterId) || string.IsNullOrEmpty(deckId)) return;

            var profile = Profile;
            profile.LastPlayedCharacterId = characterId;
            profile.LastPlayedDeckId = deckId;
            _profiles.Save(profile);
        }

        // ── Roster and decks ────────────────────────────────────────────────────────────

        /// <summary>
        /// Every character, fresh off disk. Not cached, deliberately — the deckbuilder writes the
        /// roster from more than one screen and a stale list is how a created character fails to
        /// appear (see <see cref="Profile"/> for where the line is drawn).
        /// </summary>
        public IReadOnlyList<Character> Roster() => _characters.LoadAll();

        /// <summary>Every saved deck, fresh off disk — including drafts and orphans (§3: a store never filters).</summary>
        public IReadOnlyList<DeckList> Decks() => _decks.LoadAll();

        /// <summary>
        /// What this account owns, per card id — the L6 input — or <b>null when it has no ownership
        /// record at all</b>, which is every account on every machine until something grants cards.
        ///
        /// <para>Null is not "owns nothing" and callers must not treat it as such: it means ownership is
        /// unknown, and every L6 consumer takes it to mean "do not constrain", which is the full-pool
        /// alpha the game runs today. Pass the result straight through to <see cref="DeckValidator"/>,
        /// <see cref="QueueEligibility"/> or <c>DeckQuery</c> rather than substituting an empty
        /// <c>OwnedCardCounts</c> for it — an empty record asserts the player owns nothing, which would
        /// turn every deck on the device into a draft. <see cref="IOwnedCardStore"/> carries the full
        /// rationale.</para>
        ///
        /// <para>Fresh off disk on every call, like the roster: ownership changes underneath a screen
        /// whenever a pack is opened, and this is a few kilobytes.</para>
        /// </summary>
        public IOwnedCardCounts OwnedCards() => _ownedCards.Load();

        /// <summary>
        /// Replace this account's ownership record with what the server just said it owns.
        ///
        /// <para><b>Only <see cref="CollectionGateway"/> may call this, and only on a Synced answer.</b>
        /// Since 2026-09-01 this file is a CACHE of the entitlement ledger, not the record — so the
        /// only legitimate writer is the one holding a server answer. Writing an empty record from any
        /// other source asserts "this player owns nothing" and turns every deck on the device into a
        /// draft; the local game has no way to know that, which is why there is no other caller and no
        /// convenience overload that mints one.</para>
        ///
        /// <para>An empty <paramref name="owned"/> is legal and meaningful — the server does say
        /// "you own nothing" — which is exactly why the caller, not this method, decides whether the
        /// answer was real. See <see cref="IOwnedCardStore"/> for the absent/empty distinction.</para>
        /// </summary>
        public void SaveOwnedCards(OwnedCardCounts owned)
        {
            if (owned == null) throw new ArgumentNullException(nameof(owned));
            _ownedCards.Save(owned);
        }

        /// <summary>
        /// The roster panel's rows for one character (<c>DeckQuery.ForCharacter</c>) — default first,
        /// then stored order, each carrying its queue-eligibility. One disk read for both files.
        ///
        /// <para>Ownership rides along so the switcher's draft badge and the queue gate answer with the
        /// same rule (NOR-238). It changes nothing until this account has a record.</para>
        /// </summary>
        public CharacterDecks DecksOf(Character character, ICardDatabase db)
            => DeckQuery.ForCharacter(character, _decks.LoadAll(), db, OwnedCards());

        /// <summary>
        /// Whether the create affordance should be offered for a pairing at all — a pool too thin for
        /// a legal 30 refuses up front instead of throwing halfway through creation
        /// (<c>DefaultDeckGenerator.CanGenerate</c>). Real today: not every pairing can fill a deck.
        /// </summary>
        public static bool CanCreate(CardClass primary, CardClass secondary, ICardDatabase db)
            => Character.IsValidPairing(primary, secondary)
               && DefaultDeckGenerator.CanGenerate(new Character("probe", "probe", primary, secondary), db);

        /// <summary>
        /// Whether the create form offers the blank-or-generated choice — false for the very first
        /// character, which always gets a playable deck (§2.11). Delegates rather than re-deriving:
        /// a policy the UI re-implements is a policy that drifts.
        /// </summary>
        public bool OffersStarterChoice() => CharacterCreation.OffersStarterChoice(Roster());

        /// <summary>
        /// Create a character with a starter deck and persist both (deck first — see
        /// <see cref="CharacterCreation"/> for why the order is load-bearing).
        /// </summary>
        /// <exception cref="ArgumentException">The pairing is invalid.</exception>
        /// <exception cref="DeckGenerationException">The pool cannot supply a legal 30.</exception>
        public Character CreateCharacter(string name, CardClass primary, CardClass secondary,
                                         ICardDatabase db, StarterDeck starter = StarterDeck.Generated)
        {
            string characterId = NewId();
            string deckId = NewId();

            // Seeded from the character's own id rather than a clock, so the starter deck is a
            // function of the character and re-running a crashed creation cannot produce a different
            // 30. Unity has a clock and the core assembly does not — using it here anyway would put
            // the one unreproducible input into the one place it is avoidable.
            var rng = new DeterministicRandom(SeedFrom(characterId));

            return CharacterCreation.Create(_characters, _decks, db, rng,
                                            characterId, name, primary, secondary, deckId, starter);
        }

        /// <summary>
        /// Create a character with <b>no deck</b> and persist just the roster row (NOR-278). The
        /// portrait-grid creation flow picks only the pairing and a name; the first deck is chosen
        /// later from the roster's "+" plate, so the character lands as the same lone-"+" row a
        /// character whose decks were all deleted already shows.
        ///
        /// <para>Unlike <see cref="CreateCharacter"/> this consults no card pool and needs no
        /// <see cref="ICardDatabase"/> or seed: a deckless character is always creatable for any valid
        /// pairing, however thin that pairing's pool is. Whether the pool can supply a legal 30 is the
        /// "+" plate's concern (<see cref="DefaultDeckGenerator.CanGenerate"/>), not creation's.</para>
        /// </summary>
        /// <exception cref="ArgumentException">The pairing is invalid.</exception>
        public Character CreateCharacterWithoutDeck(string name, CardClass primary, CardClass secondary)
            => CharacterCreation.CreateWithoutDeck(_characters, NewId(), name, primary, secondary);

        /// <summary>
        /// Add a deck to an existing character and persist it, generated or blank.
        ///
        /// <para>The sibling of <see cref="CreateCharacter"/> for the roster's "+" plate, and here for
        /// the same reason that one is: the core assembly mints no ids and holds no clock, so
        /// somebody has to know that an id is a GUID and that the seed comes from it. Seeding from
        /// the deck's own id rather than a clock keeps a generated deck a function of its identity —
        /// re-running a crashed create cannot produce a different 30.</para>
        ///
        /// <para>Check <see cref="DefaultDeckGenerator.CanGenerate"/> before asking for
        /// <see cref="StarterDeck.Generated"/>; a pool too thin for a legal 30 throws rather than
        /// returning something illegal.</para>
        /// </summary>
        /// <exception cref="DeckGenerationException">Generated was asked for and the pool cannot supply a legal 30.</exception>
        public DeckList CreateDeck(Character character, ICardDatabase db, string name,
                                   StarterDeck kind = StarterDeck.Blank)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));

            string deckId = NewId();
            DeckList deck = kind == StarterDeck.Generated
                ? DefaultDeckGenerator.Generate(character, db, new DeterministicRandom(SeedFrom(deckId)), deckId, name)
                : new DeckList { Id = deckId, CharacterId = character.Id, Name = name };

            // Generate() builds the deck already bound (L5), but a blank one is bound here — and the
            // binding is the whole reason a deck cannot be created anywhere but against a character.
            deck.CharacterId = character.Id;

            _decks.Save(deck);
            return deck;
        }

        /// <summary>
        /// The decks a delete would destroy — what the confirm dialog must name before the player
        /// authorises the cascade (§2.6). Call this, then <see cref="DeleteCharacter"/>.
        /// </summary>
        public IReadOnlyList<DeckList> DecksLostWith(string characterId)
            => CharacterDeletion.DecksOf(characterId, _decks.LoadAll());

        /// <summary>
        /// Delete a character and every deck bound to it, returning the deck count destroyed.
        /// Goes through <see cref="CharacterDeletion"/>, not the store — the store deletes only the
        /// roster row and would leave the decks orphaned.
        /// </summary>
        public int DeleteCharacter(string characterId)
            => CharacterDeletion.Delete(_characters, _decks, characterId);

        /// <summary>Persist a character (rename, or "Set as default" writing <c>DefaultDeckId</c>).</summary>
        public void Save(Character character) => _characters.Save(character);

        /// <summary>Persist a deck. Never legality-gated — a draft is a savable deck (§1).</summary>
        public void Save(DeckList deck) => _decks.Save(deck);

        public void DeleteDeck(string deckId) => _decks.Delete(deckId);

        // ── Identity minting ────────────────────────────────────────────────────────────

        /// <summary>
        /// A new character or deck id. <b>This is the job the core assembly refuses</b> — it mints no
        /// ambient identity so that a test, an importer and this client can all decide for themselves.
        /// A GUID because it needs no coordination and, per <see cref="DeviceIdentity"/>, is the shape
        /// Supabase keys rows on when accounts land (ADR-0001), so nothing has to be re-keyed.
        /// </summary>
        public static string NewId() => Guid.NewGuid().ToString("D");

        /// <summary>
        /// A stable 64-bit seed from an id string — FNV-1a, written out rather than using
        /// <c>string.GetHashCode</c>, which is randomized per process on modern .NET and would give
        /// the same character a different starter deck on every launch.
        /// </summary>
        private static ulong SeedFrom(string id)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (char c in id)
            {
                hash ^= c;
                hash *= prime;
            }
            return hash;
        }
    }
}
