#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Collection;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The player, on this device: their name and their decks
    /// (dev-docs/data/player-data-and-telemetry.md:11).
    ///
    /// <para><b>There is no roster any more (ADR-0009, Sam 2026-09-04).</b> <c>Character</c> held a
    /// class pairing and nothing else the game needed, so it was deleted and the pairing moved onto
    /// <see cref="DeckList"/>. What this class lost with it is a whole store — <c>characters.json</c>
    /// and every method that read, wrote or cascaded through it. A deck is now the top-level thing a
    /// player owns, and <see cref="DeckCreation.MaxPerAccount"/> is how many of them they may have.</para>
    ///
    /// <para><b>This class is the missing wire, and it is the only reason it exists.</b>
    /// <c>Kingdom.Core.Collection</c> shipped complete and green, and until now nothing under
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
        private static PlayerAccount? _local;

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
        /// <see cref="Local"/> after a switch would write one player's deck into another's file.
        /// </summary>
        public static void InvalidateLocal() => _local = null;

        private readonly IDeckStore _decks;
        private readonly IProfileStore _profiles;
        private readonly IOwnedCardStore _ownedCards;
        private PlayerProfile? _profile;

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

        /// <summary>The last deck actually submitted into a match, or empty if none yet.</summary>
        public string LastPlayedDeckId => Profile.LastPlayedDeckId;

        /// <summary>
        /// Remember the deck a match was just started with, so Find Game can pre-arm the same pick
        /// next time instead of forcing a fresh Select (Sam, 2026-08-09). Call this once the deck is
        /// known good — this stores it as-is, with no eligibility check of its own.
        ///
        /// <para>It took a character id alongside the deck id until 2026-09-04 (ADR-0009); the deck
        /// is the whole of the selection now, because the deck carries the pairing.</para>
        /// </summary>
        public void RecordLastPlayed(string deckId)
        {
            if (string.IsNullOrEmpty(deckId)) return;

            var profile = Profile;
            profile.LastPlayedDeckId = deckId;
            _profiles.Save(profile);
        }

        // ── Decks ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every saved deck, fresh off disk — including drafts (§3: a store never filters).
        ///
        /// <para>Not cached, deliberately: decks are written from more than one screen and a stale
        /// list is how a created deck fails to appear (see <see cref="Profile"/> for where the line
        /// is drawn). An <i>orphan</i> used to be in this list too; ADR-0009 made the state
        /// unreachable rather than merely unqueried.</para>
        /// </summary>
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
        public IOwnedCardCounts? OwnedCards() => _ownedCards.Load();

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
        /// This account's deck rows in stored order, each carrying its queue-eligibility
        /// (<see cref="DeckQuery.ForAccount"/>). One disk read.
        ///
        /// <para>Ownership rides along so the switcher's draft badge and the queue gate answer with the
        /// same rule (NOR-238). It changes nothing until this account has a record.</para>
        /// </summary>
        public IReadOnlyList<DeckSummary> DeckRows(ICardDatabase db)
            => DeckQuery.ForAccount(_decks.LoadAll(), db, OwnedCards());

        /// <summary>
        /// Whether a generated deck can be offered for a pairing at all — a pool too thin for a legal
        /// 30 refuses up front instead of throwing halfway through creation
        /// (<c>DefaultDeckGenerator.CanGenerate</c>). Real today: not every pairing can fill a deck.
        ///
        /// <para>This is about the POOL, never about how many decks the account already holds — that
        /// is <see cref="CanCreateDeck"/>, and the two refuse for reasons a player would phrase
        /// differently.</para>
        /// </summary>
        public static bool CanCreate(CardClass primary, CardClass secondary, ICardDatabase db,
                                    IOwnedCardCounts? owned = null)
            => DeckList.IsValidPairing(primary, secondary)
               && DefaultDeckGenerator.CanGenerate(primary, secondary, db, owned);

        /// <summary>
        /// Has this account room for another deck (<see cref="DeckCreation.MaxPerAccount"/>)? What the
        /// "+" plate is disabled by. Delegates rather than re-deriving: the cap the client draws and
        /// the cap the deck write route refuses on have to be the same number.
        /// </summary>
        public bool CanCreateDeck() => DeckCreation.CanCreate(Decks());

        /// <summary>
        /// Whether the create flow offers the blank-or-generated choice — false for the very first
        /// deck, which always gets a playable one (§2.11). Delegates rather than re-deriving:
        /// a policy the UI re-implements is a policy that drifts.
        /// </summary>
        public bool OffersStarterChoice() => DeckCreation.OffersStarterChoice(Decks());

        /// <summary>
        /// Create a deck for a pairing and persist it, generated or blank.
        ///
        /// <para>The one create path there is (ADR-0009). <c>CreateCharacter</c>,
        /// <c>CreateCharacterWithoutDeck</c> and the roster's per-character <c>CreateDeck</c> all
        /// collapsed into this: a deck is the top-level thing now, so there is nothing to create it
        /// against and nothing to bind it to afterwards.</para>
        ///
        /// <para>Here rather than in the core assembly for the reason every other mint is: that
        /// assembly holds no clock and mints no identity, so somebody has to know that an id is a
        /// GUID and that the seed comes from it. Seeding from the deck's own id rather than a clock
        /// keeps a generated deck a function of its identity — re-running a crashed create cannot
        /// produce a different 30.</para>
        ///
        /// <para><b>The cap is NOT enforced here.</b> <see cref="CanCreateDeck"/> is what the "+"
        /// plate consults, and <c>PUT /decks/{id}</c> is what actually refuses the ninth. A throw
        /// from this method would turn a full roster into an error dialog on a screen that should
        /// simply not have offered the plate.</para>
        ///
        /// <para>Check <see cref="CanCreate"/> before asking for <see cref="StarterDeck.Generated"/>;
        /// a pool too thin for a legal 30 throws rather than returning something illegal.</para>
        /// </summary>
        /// <exception cref="ArgumentException">The pairing is invalid (see <see cref="DeckList.IsValidPairing"/>).</exception>
        /// <exception cref="DeckGenerationException">Generated was asked for and the pool cannot supply a legal 30.</exception>
        public DeckList CreateDeck(ICardDatabase db, string name, CardClass primary, CardClass secondary,
                                   StarterDeck kind = StarterDeck.Blank)
        {
            string deckId = NewId();
            DeckList deck = DeckCreation.Create(
                _decks, db, new DeterministicRandom(SeedFrom(deckId)),
                deckId, name, primary, secondary, kind, OwnedCards());

            Push(t => RosterGateway.PushDeckSavedAsync(t, deck));
            return deck;
        }

        /// <summary>Prepare an unsaved candidate for server-confirmed creation.</summary>
        public DeckList PrepareDeck(ICardDatabase db, string name, CardClass primary, CardClass secondary,
                                    StarterDeck kind)
        {
            string deckId = NewId();
            return DeckCreation.Prepare(db, new DeterministicRandom(SeedFrom(deckId)),
                deckId, name, primary, secondary, kind, OwnedCards());
        }

        /// <summary>Only an accepted server write may put a newly created deck in the cache.</summary>
        public Task<RosterWriteStatus> ConfirmNewDeckAsync(string accessToken, DeckList deck) =>
            RosterGateway.ConfirmNewDeckAsync(accessToken, deck, _decks);

        public Task<RosterWriteStatus> ConfirmDeckEditAsync(string accessToken, DeckList deck) =>
            RosterGateway.ConfirmDeckEditAsync(accessToken, deck, _decks);

        /// <summary>Persist an edit. Never legality-gated — a draft is a savable deck (§1).</summary>
        public void Save(DeckList deck)
        {
            _decks.Save(deck);
            Push(t => RosterGateway.PushDeckSavedAsync(t, deck));
        }

        public void DeleteDeck(string deckId)
        {
            _decks.Delete(deckId);
            Push(t => RosterGateway.PushDeckDeletedAsync(t, deckId));
        }

        // ── Mirroring to the server ─────────────────────────────────────────────────────

        /// <summary>
        /// True for the one account the player is actually playing — <see cref="Local"/>.
        ///
        /// <para><b>This is what keeps an editor tool off the wire.</b> The constructor stays public
        /// so a tool or a test scene can point at a scratch directory, and every one of those would
        /// otherwise mirror its throwaway decks into the signed-in player's real server roster —
        /// a self-check that quietly adds "Ashen Vow" to somebody's account and then deletes it
        /// again. Reference equality against the static rather than a flag, so it cannot be set
        /// wrongly.</para>
        /// </summary>
        private bool IsLocal => ReferenceEquals(this, _local);

        /// <summary>
        /// Mirror a local write to the server, if this is the real account and somebody is signed in.
        ///
        /// <para><b>Fire and forget, deliberately.</b> The local write has already happened and is
        /// what the player sees; the push is a best-effort mirror whose every failure is a log line
        /// inside <see cref="RosterGateway"/>. Awaiting it would make a save wait on the network, and
        /// returning its status would invite a caller to undo a save the player already watched
        /// succeed.</para>
        ///
        /// <para><b>The push lives here rather than at the call sites</b> for the reason
        /// <c>SignInGateController.Succeed</c> is one method: there are seven screens that save a
        /// deck or a character, and a screen that forgot to push would silently stop backing up the
        /// player's work with nothing to catch it.</para>
        /// </summary>
        private void Push(Func<string, Task<RosterWriteStatus>> call)
        {
            if (!IsLocal) return;

            string token = RosterGateway.AccessToken;
            if (string.IsNullOrEmpty(token)) return;

            _ = call(token);
        }

        /// <summary>
        /// Replace this account's whole roster with what the server just said it is.
        ///
        /// <para><b>Only <see cref="RosterGateway"/> may call this, and only on a Known answer</b> —
        /// the same rule, for the same reason, as <see cref="SaveOwnedCards"/>. Since the login
        /// handshake landed, <c>decks.json</c> is a CACHE of the server-side player data store, not
        /// the record. There is no offline mode, so there is no
        /// merge and no last-write-wins: a Known answer replaces, and every other answer leaves the
        /// files exactly as they are.</para>
        ///
        /// <para><b>An empty roster here is a real answer and DOES empty the device.</b> A player who
        /// deleted their last deck must not have it restored by the next sign-in. That is only
        /// safe because <c>RosterSnapshot.ShouldReplaceLocal</c> is false for every outcome that is
        /// not an answer — a failed read never reaches this method.</para>
        ///
        /// <para><b>A wholesale overwrite, and it NEVER READS THE FILES IT REPLACES.</b> The obvious
        /// implementation — load what is there, delete what the server no longer has, save the rest —
        /// makes the local cache able to block its own repair: a <c>decks.json</c> this build's
        /// reader refuses is exactly the file that most needs replacing, and every read-modify-write
        /// path throws on it. Measured on a device 2026-09-02, where an Android auto-backup restored
        /// a pre-numeric-migration <c>decks.json</c>; the deck drawer threw, and no sync could clear
        /// it because clearing it started by reading it. <c>ReplaceAll</c> exists for this.</para>
        ///
        /// <para><b>One store, one call, since ADR-0009.</b> This used to write decks first and then
        /// characters — the mirror of creation's order, so a failure between the two left a deck with
        /// no character rather than the reverse. There is no second write to order against any more,
        /// and no half-replaced state to reason about.</para>
        /// </summary>
        public void ReplaceRoster(IReadOnlyList<DeckList> decks)
        {
            if (decks == null) throw new ArgumentNullException(nameof(decks));

            _decks.ReplaceAll(decks);
        }

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
