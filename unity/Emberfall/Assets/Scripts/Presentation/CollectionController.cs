using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The card wall, in one of two modes (#144, Sam, 2026-08-11). This screen used to be two
    /// screens — <c>CollectionController</c> and <c>DeckbuilderController</c> — on the argument that
    /// the deckbuilder's wall is character-scoped by construction and so structurally cannot show
    /// you the game. That is true, and it is a <i>mode</i> rather than a screen:
    ///
    /// <list type="bullet">
    /// <item><b>Browse</b> — no character. The whole pool, no deck panel, nothing a tap can do.</item>
    /// <item><b>Deckbuild</b> — a character. The pool force-filtered to that character's pairing
    /// plus neutral, the deck panel present, a tap adds a copy.</item>
    /// </list>
    ///
    /// <para><b>The mode is decided by <see cref="PendingCharacterId"/>, and reading it consumes
    /// it.</b> A static that survives one navigation would silently put the browse screen into
    /// deckbuild mode on the visit after a deckbuild visit, which is the one failure this design can
    /// have. <see cref="PendingDeckId"/> has always been consume-on-read for the same reason.</para>
    ///
    /// <para><b>It shows what you own when the account knows, and the whole pool when it doesn't.</b>
    /// Ownership has three states, not two:</para>
    ///
    /// <list type="bullet">
    /// <item>No ownership record (<see cref="PlayerAccount.OwnedCards"/> returns null) — nobody has told
    /// this account what it owns. The wall shows the full collectible pool and the label says "in the
    /// game". This is every machine today.</item>
    /// <item>A record, scoped to it — the wall shows only cards with at least one copy, and the label
    /// says "in your collection". This is the default the moment a grant path writes that record.</item>
    /// <item>A record, showing everything anyway — the Owned/All toggle (#146). Same wall as the first
    /// state and the same label, from a completely different reason.</item>
    /// </list>
    ///
    /// <para><b>So the player-facing copy rule holds in two of the three:</b> do not describe this
    /// screen as a collection of owned cards while it is showing the pool. The label is the only thing
    /// that says which of the two you are looking at, so it is not decoration — read
    /// <see cref="OwnedOnly"/> and <see cref="UpdateWallLabels"/> before changing its wording.</para>
    ///
    /// <para><b>The toggle is a scope, not a facet</b>, which is why it is not a chip on
    /// <see cref="CardQueryBar"/>: the bar's chips are generated from the card enums and filter a pool,
    /// while this decides which pool there is. It therefore rebuilds the pool rather than re-querying,
    /// and <see cref="CardQueryBar.Reset"/> deliberately leaves it alone — resetting the filters should
    /// not silently move you between your collection and the game.</para>
    ///
    /// <para><b>That ownership scoping is browse-only</b>, and deliberately: in deckbuild mode an
    /// unowned card stays on the wall and is refused at the copy cap instead (see
    /// <see cref="BuildPool"/>), because #150 wants it visible-but-spent rather than absent.</para>
    ///
    /// <para><b>One plate declares every region; the mode hides what it does not use</b> (#153).
    /// The deck panel, the browse header, the query bar and the three filter chips all exist on
    /// CollectionScreen.uxml in both modes. <see cref="StampMode"/> writes a modifier onto the plate
    /// root and the stylesheet does the hiding, so <b>an absent element is now a broken plate rather
    /// than a mode</b>. The null guards below are diagnostics, not a second way to configure this
    /// screen: a missing deck list while deckbuilding means the UXML is wrong, not that this visit
    /// declined the deck panel.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// CollectionScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CollectionController : MonoBehaviour
    {
        /// <summary>Where the exit button returns to. Set by the caller; the roster is the only door today.</summary>
        public static string ReturnScene = "CharacterRoster";

        /// <summary>Which character to build a deck for, or empty to browse. Consumed on read.</summary>
        public static string PendingCharacterId;

        /// <summary>Which saved deck to open, or empty to use that character's normal opening deck.</summary>
        public static string PendingDeckId;

        private PlayerAccount _account;
        private ICardDatabase _db;
        private CardFaceBaker _faceBaker;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();

        /// <summary>The character being built for, or null in browse mode. This field IS the mode.</summary>
        private Character _character;
        private DeckList _deck;

        /// <summary>
        /// This account's ownership record, or null when it has none — the full-pool alpha, which is
        /// every machine until a grant path writes one. Read once per visit: nothing opens a pack from
        /// inside this screen, so it cannot change while it is up.
        /// </summary>
        private IOwnedCardCounts _owned;

        /// <summary>
        /// Which pool the browse wall is showing: true = only what this account owns, false = every
        /// collectible card. Meaningless without <see cref="_owned"/> — see <see cref="OwnedOnly"/>,
        /// which is the predicate everything reads.
        ///
        /// <para><b>Per visit, and deliberately not static.</b> A remembered scope is exactly the shape
        /// of the <see cref="PendingCharacterId"/> failure this screen already guards: a browse visit
        /// that silently shows the game because of a choice made on a previous one. You arrive at your
        /// collection; All is a thing you ask for.</para>
        /// </summary>
        private bool _ownedScope = true;

        // Cached elements. All of these exist on the plate in both modes; a null here is a broken
        // UXML, not a mode that declined the region. See the "one plate declares every region" remark.
        private VisualElement _root;
        private VisualElement _grid;
        private CardWall _wall;
        private CardQueryBar _query;
        private Label _countLabel;
        private Label _emptyLabel;
        private ListView _deckList;
        private VisualElement _curve;
        private Label _deckCountLabel;
        private Button _filterPrimaryButton;
        private Button _filterSecondaryButton;
        private Button _filterNeutralButton;
        private VisualElement _scopeRow;
        private Button _scopeOwnedButton;
        private Button _scopeAllButton;

        /// <summary>The pairing chips' selected classes (deckbuild only). Multi-select, and empty
        /// means unconstrained — the same all-off convention <see cref="CardQueryBar"/> uses for its
        /// own class facet. Composed into the one wall query by <see cref="ApplyQuery"/>.</summary>
        private readonly HashSet<CardClass> _chipClasses = new HashSet<CardClass>();

        /// <summary>True when this visit is editing a deck. The one predicate every mode-dependent
        /// branch below reads, so the mode is never re-derived from something correlated with it.</summary>
        private bool Deckbuilding => _character != null;

        /// <summary>
        /// True when the wall is scoped to what this account owns — the one predicate for it, read by
        /// both the pool and the count label so the two can never disagree about which of them the
        /// player is looking at.
        ///
        /// <para>False while deckbuilding, and that is the mode's own rule rather than a side effect:
        /// that pool is scoped by the character's pairing and never by ownership, because #150 wants an
        /// unowned card visible-but-spent (see <see cref="BuildPool"/>). Folding the mode in here rather
        /// than testing it again at each site is why <see cref="UpdateWallLabels"/> still needs no mode
        /// check.</para>
        /// </summary>
        private bool OwnedOnly => !Deckbuilding && _owned != null && _ownedScope;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Collection] No rootVisualElement — is the UXML assigned?"); return; }

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Collection] could not open this device's collection: {ex.Message}");
                return;
            }

            LoadOwnership();

            _grid = _root.Q<VisualElement>("collection-grid");
            if (_grid == null)
            {
                Debug.LogError("[Collection] 'collection-grid' not found in the UXML. If the whole " +
                               "screen is empty, check the UXML for a double dash inside a comment " +
                               "— that fails the import silently.");
                return;
            }

            if (!ResolveMode()) return;

            StampMode();
            BuildPool();
            CacheElements();
            InitFaceBaker();
            BuildWall();
            WireQueryBar();
            WireCollectionFilters();
            WireScopeToggle();
            WireDeckList();
            WireChrome();

            ApplyQuery();
            if (Deckbuilding) Refresh();

            WireCollectionScroll();
        }

        /// <summary>
        /// Decide browse vs deckbuild and, when deckbuilding, open the deck. Returns false only when
        /// the screen cannot open at all.
        ///
        /// <para>Consumes <see cref="PendingCharacterId"/>, so a stale one cannot leak into the next
        /// visit. A pending id that matches nothing on the roster falls back to browse rather than
        /// erroring: the roster is the only thing that sets it, and a browse wall is a truthful
        /// screen where a half-initialised deckbuilder is not.</para>
        /// </summary>
        private bool ResolveMode()
        {
            string pendingCharacter = PendingCharacterId;
            PendingCharacterId = "";
            if (string.IsNullOrEmpty(pendingCharacter)) return true;

            IReadOnlyList<Character> roster = _account.Roster();
            foreach (var c in roster)
                if (c.Id == pendingCharacter) { _character = c; break; }

            if (_character == null)
            {
                Debug.LogWarning($"[Collection] no character '{pendingCharacter}' on this account; " +
                                 "opening the browse wall instead of a deckbuilder with no deck.");
                return true;
            }

            CharacterDecks characterDecks = _account.DecksOf(_character, _db);
            _deck = FindPendingDeck(characterDecks.Decks) ?? characterDecks.Opening?.Deck;
            if (_deck == null)
            {
                Debug.LogError($"[Collection] '{_character.Name}' has no deck to open.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Put the mode on the plate root, which is what decides the whole screen's shape (#153).
        ///
        /// <para><b>Both modifiers are written every visit, one on and one off</b>, rather than one
        /// modifier and a bare-class default. The stylesheet then never needs <c>:not()</c>, whose
        /// support in UI Toolkit is narrower than in CSS — a negation that silently failed to match
        /// would show the browse chrome and the deck panel at the same time.</para>
        ///
        /// <para>The class goes on the element NAMED "root", not on <c>rootVisualElement</c>: the
        /// latter is the document's own container and carries none of this plate's classes, so
        /// <c>.collection</c> and its modifiers would never meet on the same element.</para>
        /// </summary>
        private void StampMode()
        {
            VisualElement plate = _root.Q<VisualElement>("root");
            if (plate == null)
            {
                Debug.LogError("[Collection] no element named 'root' in the UXML, so the mode class " +
                               "has nowhere to go — the deck panel and the browse header will both " +
                               "be visible. Check CollectionScreen.uxml.");
                return;
            }
            plate.EnableInClassList("collection--deckbuild", Deckbuilding);
            plate.EnableInClassList("collection--browse", !Deckbuilding);
        }

        private static DeckList FindPendingDeck(IReadOnlyList<DeckSummary> decks)
        {
            string pending = PendingDeckId;
            PendingDeckId = "";
            if (string.IsNullOrEmpty(pending)) return null;
            for (int i = 0; i < decks.Count; i++)
                if (decks[i].Deck.Id == pending) return decks[i].Deck;
            return null;
        }

        /// <summary>
        /// This account's ownership record, or null. Never fatal to this screen: a browse surface that
        /// refuses to open because an ownership file is unreadable helps nobody, and the gates that
        /// matter — <see cref="DeckValidator"/> L6 here, and the server at match-join — read the same
        /// store and still refuse. Logged as an error rather than swallowed, because the visible symptom
        /// of the fallback is a screen that looks completely normal.
        /// </summary>
        private void LoadOwnership()
        {
            try
            {
                _owned = _account.OwnedCards();
            }
            catch (Exception ex)
            {
                _owned = null;
                Debug.LogError($"[Collection] could not read this account's card ownership " +
                               $"({ex.GetType().Name}: {ex.Message}); showing the full card pool instead " +
                               "of your collection. The count label says which one you are looking at.");
            }
        }

        /// <summary>
        /// The wall's pool, scoped by mode.
        ///
        /// <para><b>Deckbuild</b> — the character's pairing plus neutral. Ownership does NOT scope it:
        /// an unowned card stays on the wall and <see cref="AddCopy"/> refuses it at the copy cap,
        /// because the copy limit is already <c>min(MaxCopies, copies owned)</c>. Removing it from the
        /// pool instead would make a card you nearly own invisible, and #150 wants it dimmed.</para>
        ///
        /// <para><b>Browse</b> — every card this account owns a copy of, or, when it has no ownership
        /// record or the player has asked for All (#146), every collectible card in the game. These two
        /// pools look identical on screen and mean completely different things, which is why the count
        /// label is load-bearing.</para>
        ///
        /// <para>A card recorded at 0 copies is NOT owned. Zero is a real recorded value — every copy
        /// dusted, or a refund — and it is exactly the case a truthiness check on "is it in the
        /// record" would get wrong, so this asks for the count.</para>
        ///
        /// <para><b>Safe to call again</b>, and <see cref="SetScope"/> does: the pool is what changes
        /// when the scope does, so the wall is re-poolled rather than re-queried. That costs no bakes
        /// after the first widening — <see cref="CardFaceBaker"/> caches faces by card id for the life
        /// of the screen and <see cref="CardWall.SetPool"/> only rebuilds the (cheap) tiles.</para>
        /// </summary>
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
            {
                if (Deckbuilding)
                {
                    if (!_character.AllowsClass(def.Class)) continue;
                }
                else if (OwnedOnly && _owned.CopiesOwned(def.Id) <= 0) continue;

                _pool.Add(def);
            }
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));
        }

        private void CacheElements()
        {
            _countLabel      = _root.Q<Label>("collection-count");
            _emptyLabel      = _root.Q<Label>("collection-empty");
            _deckList        = _root.Q<ListView>("deck-list");
            _curve           = _root.Q<VisualElement>("curve-strip");
            _deckCountLabel  = _root.Q<Label>("count-label");
            _filterPrimaryButton   = _root.Q<Button>("filter-primary");
            _filterSecondaryButton = _root.Q<Button>("filter-secondary");
            _filterNeutralButton   = _root.Q<Button>("filter-neutral");
            _scopeRow            = _root.Q<VisualElement>("scope-row");
            _scopeOwnedButton    = _root.Q<Button>("scope-owned");
            _scopeAllButton      = _root.Q<Button>("scope-all");
        }

        // The real card, baked to a texture — one card front implementation
        // (BoardController.CreatePresenter), photographed, never a second UI Toolkit
        // re-implementation of the card face drifting from it.
        private void InitFaceBaker()
        {
            var bakerGo = new GameObject("CardFaceBaker");
            bakerGo.transform.SetParent(transform, false);
            _faceBaker = bakerGo.AddComponent<CardFaceBaker>();
            _faceBaker.Initialize(_db);
        }

        // The tile building, the bake, the per-row gutter solve and the Yoga-safe deferred layout all
        // live in CardWall (Sam, 2026-08-07), so both modes render the same wall. What stays here is
        // the part that is genuinely about a DECK: what a tap does, and which tiles are at their cap.
        private void BuildWall()
        {
            _wall = new CardWall(_grid, _faceBaker);

            if (Deckbuilding)
            {
                _wall.TileClicked += AddCopy;

                // The copy-cap fade — the ONLY per-tile deck-state treatment on the wall, and the
                // reason CardWall exposes a decorator rather than owning this itself: "you already
                // hold every copy" is a fact about a deck, and browse mode has none.
                //
                // This deliberately does NOT ask DeckValidator.CanAdd. CanAdd is false for L1 (deck
                // at 30) and L4 (off-class) too, and keying the fade on it is exactly the bug removed
                // in c31da55: a deck at 30 refuses every card, so every tile faded and the wall the
                // screen exists to show went dark at the moment the player stopped building and
                // started browsing. The fade means one thing only — "you already hold every copy of
                // THIS card you are allowed" — so it reads the copy cap directly. It can never take
                // more than a handful of tiles, and clears the moment a copy comes back out.
                //
                // MaxCopies is rarity-dependent (L3 Legendary = 1, L2 everything else = 2), so a
                // Legendary fades at one copy, not two.
                //
                // NOR-238 replaced MaxCopies with CopyLimit — min(MaxCopies, copies you own), or
                // MaxCopies alone when this account has no ownership record. The fade's ONE meaning is
                // unchanged and that is why it could absorb L6 rather than needing a treatment of its
                // own: "you already hold every copy of THIS card you are allowed" is the same sentence
                // whether the limit came from the format or from your collection. A card you own none
                // of therefore fades at zero copies in the deck, which is exactly what AddCopy refuses.
                _wall.TileDecorator = (def, tile) =>
                    tile.EnableInClassList("card-tile--maxed",
                        _deck.CountOf(def.Id) >= DeckValidator.CopyLimit(def.Rarity, _owned, def.Id));
            }

            // No TileClicked handler in browse mode on purpose: there is no deck to add to, and
            // in-match inspect was cut (board layout rulings, 2026-07-23). A tap does nothing until
            // there is something for it to do.

            _wall.SetPool(_pool);
        }

        private void WireQueryBar()
        {
            _query = new CardQueryBar(_root);
            _query.Changed += ApplyQuery;

            var reset = _root.Q<Button>("query-reset");
            if (reset != null) reset.clicked += () => _query.Reset();
        }

        // ---- Pairing chips: primary / secondary / neutral (#160) ------------------------------
        // The class axis for deckbuild. Three chips — the character's primary class, its secondary
        // class, and NEUTRAL — each a multi-select toggle where all-off means unconstrained, matching
        // CardQueryBar's own class-facet convention. That is what replaced the old radio All/Class/
        // Neutral set: "all" is now nothing selected, not a fourth chip. Labels are the real class
        // names, written here at runtime rather than authored in UXML, so a rename can never drift.
        //
        // Meaningless without a pairing, so the plate hides them while browsing and this returns early
        // in that mode — two guards for one fact, deliberately: the class is what the player sees and
        // the early return is what stops us wiring a callback onto a chip that can never be pressed.
        //
        // These do NOT write the wall themselves. Both filter surfaces — these chips and the query
        // bar — compose into one CardFilter at the single ApplyQuery site, so neither clobbers the
        // other (and, critically, a chip press no longer resets the player's chosen sort).
        private void WireCollectionFilters()
        {
            if (!Deckbuilding || _filterPrimaryButton == null) return;

            _filterPrimaryButton.text   = ClassLabel(_character.PrimaryClass);
            _filterSecondaryButton.text = ClassLabel(_character.SecondaryClass);
            _filterNeutralButton.text   = ClassLabel(CardClass.NEUTRAL);

            WireChipToggle(_filterPrimaryButton,   _character.PrimaryClass);
            WireChipToggle(_filterSecondaryButton, _character.SecondaryClass);
            WireChipToggle(_filterNeutralButton,   CardClass.NEUTRAL);
        }

        private void WireChipToggle(Button chip, CardClass klass)
        {
            chip.RegisterCallback<ClickEvent>(_ =>
            {
                if (!_chipClasses.Remove(klass)) _chipClasses.Add(klass);
                StampChips();
                ApplyQuery();
            });
        }

        // Every chip is repainted from the set, not just the one clicked — a character whose primary
        // and secondary happen to coincide would otherwise leave the other chip's highlight stale.
        private void StampChips()
        {
            _filterPrimaryButton.EnableInClassList("db-chip--on", _chipClasses.Contains(_character.PrimaryClass));
            _filterSecondaryButton.EnableInClassList("db-chip--on", _chipClasses.Contains(_character.SecondaryClass));
            _filterNeutralButton.EnableInClassList("db-chip--on", _chipClasses.Contains(CardClass.NEUTRAL));
        }

        // ---- Owned / All scope toggle (#146) --------------------------------------------------
        //
        // Not a fourth filter chip. The chips above and the query bar both narrow a pool; this one
        // decides which pool exists, so it rebuilds it — see BuildPool for why that is cheap after the
        // first widening.

        /// <summary>
        /// Offer the toggle, but only to an account that has an ownership record.
        ///
        /// <para><b>With no record there is no second pool to switch to</b>: the wall is already every
        /// collectible card in the game, so the control would offer a choice between a pool and itself,
        /// and "All" would read as a claim that some other view is being withheld. That is every machine
        /// today, which is exactly why this must be hidden rather than shown-and-inert — an alpha player
        /// meeting a toggle that does nothing learns the wrong thing about their collection.</para>
        ///
        /// <para>Hidden in deckbuild mode by the plate, not by this: the whole browse header is
        /// display:none there (#153), so the row is unpickable and this needs no mode check.</para>
        /// </summary>
        private void WireScopeToggle()
        {
            if (_scopeRow == null || _scopeOwnedButton == null || _scopeAllButton == null)
            {
                Debug.LogError("[Collection] the Owned/All scope row is missing from the UXML, so a " +
                               "player with an ownership record can only ever see what they own. " +
                               "Check CollectionScreen.uxml for 'scope-row', 'scope-owned', 'scope-all'.");
                return;
            }

            _scopeRow.EnableInClassList("col-scope--on", _owned != null);
            StampScope();
            if (_owned == null) return;

            _scopeOwnedButton.RegisterCallback<ClickEvent>(_ => SetScope(true));
            _scopeAllButton.RegisterCallback<ClickEvent>(_ => SetScope(false));
        }

        /// <summary>
        /// Switch pools. Rebuilds rather than re-queries, because ownership is a fact about the account
        /// and not a facet of a card — <c>CardFilter</c> has nowhere to put it and putting it there
        /// would push an account concern into the shared query layer every screen reads.
        ///
        /// <para>The wall scrolls back to the top as a side effect of being re-poolled, which is the
        /// behaviour you want here: the set under the cursor is not the set that was there.</para>
        /// </summary>
        private void SetScope(bool ownedOnly)
        {
            if (_ownedScope == ownedOnly) return;
            _ownedScope = ownedOnly;

            StampScope();
            BuildPool();
            _wall.SetPool(_pool);     // keeps the wall's current filter and sort
            UpdateWallLabels();
        }

        // Both chips are written on every change, one on and one off — same reason as StampMode.
        private void StampScope()
        {
            _scopeOwnedButton.EnableInClassList("db-chip--on", OwnedOnly);
            _scopeAllButton.EnableInClassList("db-chip--on", !OwnedOnly);
        }

        // ---- Deck ListView ----
        private void WireDeckList()
        {
            if (!Deckbuilding || _deckList == null) return;

            _deckList.fixedItemHeight = 54;
            _deckList.selectionType = SelectionType.None;
            _deckList.itemsSource = _deck.Entries;
            _deckList.makeItem = MakeCardRow;
            _deckList.bindItem = BindDeckRow;
            _deckList.RegisterCallback<ClickEvent>(OnDeckClick);

            // No visible scrollbars anywhere (Sam, 2026-08-01 ruling). ListView doesn't expose
            // verticalScrollerVisibility itself — only its internal ScrollView does — so this can't be
            // set from UXML like collection-scroll's.
            var innerScroll = _deckList.Q<ScrollView>();
            if (innerScroll != null)
            {
                innerScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                innerScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }
        }

        private void BindDeckRow(VisualElement row, int index)
        {
            var entry = _deck.Entries[index];
            if (!_db.TryGet(entry.CardId, out var def)) { row.style.display = DisplayStyle.None; return; }
            row.style.display = DisplayStyle.Flex;
            row.userData = def;
            SetCardRow(row, def);
            row.EnableInClassList("card-row--disabled", false);
        }

        private void OnDeckClick(ClickEvent evt)
        {
            var e = evt.target as VisualElement;
            while (e != null && !(e.userData is CardDefinition)) e = e.parent;
            if (!(e?.userData is CardDefinition def)) return;
            RemoveCopy(def);
        }

        // ---- Shared row factory (§2.3 tile is Phase 2; the spike uses a legible full-width row) ----
        private static VisualElement MakeCardRow()
        {
            var row = new VisualElement();
            row.AddToClassList("card-row");
            // var cost = new Label { name = "cost" };   cost.AddToClassList("card-cost");
            // var dot  = new VisualElement { name = "dot" }; dot.AddToClassList("card-rarity-dot");
            var name = new Label { name = "name" };   name.AddToClassList("card-name");
            //  row.Add(cost); row.add(dot); removed from the line below for offending someones sensibilities
             row.Add(name);
            return row;
        }

        private static void SetCardRow(VisualElement row, CardDefinition def)
        {
            // row.Q<Label>("cost").text = def.Cost.ToString();
            row.Q<Label>("name").text = def.Name;

            SwapPrefixedClass(row, "edge--", ClassEdgeClass(def.Class));
        }

        // ---- Mutations ----
        private void AddCopy(CardDefinition def)
        {
            // L1 ceiling / L2/L3 cap / L4 off-class / L6 you don't own another copy.
            if (!DeckValidator.CanAdd(_deck, _character, def, _owned)) return;
            int i = _deck.Entries.FindIndex(x => x.CardId == def.Id);
            if (i >= 0) _deck.Entries[i] = _deck.Entries[i].With(_deck.Entries[i].Count + 1);
            else        _deck.Entries.Add(new DeckEntry(def.Id, 1));
            SortDeck();
            Refresh();
        }

        private void RemoveCopy(CardDefinition def)
        {
            int i = _deck.Entries.FindIndex(x => x.CardId == def.Id);
            if (i < 0) return;
            int c = _deck.Entries[i].Count - 1;
            if (c <= 0) _deck.Entries.RemoveAt(i);
            else        _deck.Entries[i] = _deck.Entries[i].With(c);
            Refresh();
        }

        private void SortDeck()
        {
            _deck.Entries.Sort((a, b) =>
            {
                _db.TryGet(a.CardId, out var da);
                _db.TryGet(b.CardId, out var db);
                int ca = da?.Cost ?? 0, cb = db?.Cost ?? 0;
                return ca != cb ? ca.CompareTo(cb)
                                : string.CompareOrdinal(da?.Name, db?.Name);
            });
        }

        // ---- Collection scroll (NOR-360) ----
        //
        // The Android system back gesture (and keyboard Escape on PC) routes through
        // NavigationCancelEvent. This is the belt-and-suspenders exit: back-button and done-button
        // are already wired below, but both require the player to scroll back to them. The gesture
        // escapes the screen regardless of scroll position.
        //
        // The DEVELOPMENT_BUILD block logs the data the issue asks for: scroll offset, grid content
        // height, and layout call count sampled every 500ms. On a loop-free build the layout count
        // should only tick up on resize or filter changes, not while idle or during a scroll gesture.
        private void WireCollectionScroll()
        {
            _root.RegisterCallback<NavigationCancelEvent>(_ => Exit());

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var scroll = _root.Q<ScrollView>("collection-scroll");
            if (scroll != null && _wall != null && _grid != null)
            {
                scroll.schedule.Execute(() =>
                {
                    Debug.Log($"[NOR-360] offset={scroll.scrollOffset.y:F1} " +
                              $"content.h={_grid.resolvedStyle.height:F1} " +
                              $"layouts={_wall.LayoutCount}");
                }).Every(500);
            }
#endif
        }

        // ---- Chrome (buttons) ----
        //
        // Both exits are called "Back" and both live on the plate; only their names differ, because
        // each sits in a region the other mode hides — back-button in the browse header, done-button
        // in the deck panel. Wire both: the hidden one is display:none, so it is unpickable and the
        // player only ever meets the one belonging to the mode they are in.
        //
        // The exit saves in deckbuild mode, and it is the only way out. The alternative is a confirm
        // dialog on a screen whose whole model is "an illegal deck is a savable draft" — there is
        // nothing to lose by writing it. There is deliberately no second Save button beside it (Sam,
        // 2026-08-07): two controls performing one write is two things to explain and one of them
        // redundant.
        private void WireChrome()
        {
            _root.Q<Button>("back-button")?.RegisterCallback<ClickEvent>(_ => Exit());
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ => Exit());
        }

        private void Exit()
        {
            if (Deckbuilding) SaveDeck();
            Go(ReturnScene);
        }

        /// <summary>Persist the deck as-is. Never legality-gated (collection-deckbuilder-spec §1,
        /// Sam 2026-07-26): an illegal deck is a savable draft, not a blocked action — only queue
        /// eligibility (computed fresh on load, never stored) refuses it a match.</summary>
        private void SaveDeck()
        {
            _account.Save(_deck);
            Debug.Log($"[Collection] Saved '{_deck.Name}' ({_deck.TotalCount} cards) for {_character.Name}.");
        }

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the Editor).
        /// Mirrors <c>MainMenuController.Go</c> — see there for why <c>CanStreamedLevelBeLoaded</c>
        /// over <c>GetBuildIndexByScenePath</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[Collection] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        /// <summary>
        /// The one site that writes the wall query. Composes the two filter surfaces — the query bar
        /// and (in deckbuild) the pairing chips — into a single <see cref="CardFilter"/>, so neither
        /// clobbers the other the way two separate <c>SetQuery</c> writers did before #160.
        ///
        /// <para>The bar's own class facet is hidden in deckbuild (the plate hides <c>query-classes</c>
        /// there), so its <see cref="CardFilter.Classes"/> is empty in that mode and the pairing chips
        /// own the class axis with no contention. In browse the chips are never wired, <see
        /// cref="_chipClasses"/> stays empty, and this passes the bar's filter through untouched.</para>
        ///
        /// <para>Runs in both modes; on a plate that declares no query controls the filter is empty,
        /// which matches everything. The sort is always the bar's own — a chip press no longer reverts
        /// it to default.</para>
        /// </summary>
        private void ApplyQuery()
        {
            CardFilter filter = _query.Filter;
            if (_chipClasses.Count > 0)
                filter.Classes = new List<CardClass>(_chipClasses);

            _wall.SetQuery(filter, _query.Sort);
            UpdateWallLabels();
        }

        // Class enum to its player-facing name, matching how CardQueryBar labels its own class chips
        // (Title case, underscores to spaces) so the pairing chips read like the rest of the bar.
        private static string ClassLabel(CardClass c)
        {
            string s = c.ToString();
            if (s.Length <= 1) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant().Replace('_', ' ');
        }

        /// <summary>
        /// The wall's own count and empty-state text. Every path that changes what the wall shows
        /// ends here — the query bar and the three filter chips both — so the count can never
        /// describe a wall the player is no longer looking at.
        ///
        /// <para>Both labels now live on the same plate as both filter surfaces, so routing every
        /// path through here is what keeps that true. It writes to the count label in deckbuild mode
        /// too, where the browse header hiding it makes the write invisible rather than wrong — the
        /// alternative is a mode check here, which is one more place to get the mode wrong.</para>
        /// </summary>
        private void UpdateWallLabels()
        {
            int shown = _wall.Visible.Count;

            // The label is the ONLY thing that distinguishes "your collection" from "every card in
            // the game" — the wall is identical either way (see the class remarks). So it names the
            // pool in both directions rather than only the interesting one: a bare count would leave
            // the two states differing by nothing a player could see.
            //
            // It reads OwnedOnly, never `_owned != null`. Those were the same predicate until the
            // toggle landed, and the difference is the whole of #146: an account WITH a record that
            // has asked for All is looking at the game, and telling it "in your collection" is the
            // one lie this screen is built to avoid.
            if (_countLabel != null)
            {
                string noun = OwnedOnly ? "in your collection" : "in the game";
                _countLabel.text = shown == _pool.Count
                    ? $"{_pool.Count} {noun}"
                    : $"{shown} of {_pool.Count} {noun}";
            }

            // A wall that goes blank with no text reads as a broken screen rather than an empty
            // result, and this one can go blank easily — the facets are ANDed, so "Legendary" plus
            // "Weapon" plus cost 0 is one tap away from matching nothing.
            //
            // Two ways to be empty now, and blaming the filters for the other one is a lie the player
            // cannot get past: an account that owns nothing has an empty pool with no filter set, and
            // "Nothing matches those filters" would send them tapping Reset forever.
            //
            // That message points at the toggle, and can: OwnedOnly is true only when a record exists,
            // which is the same condition that puts the scope row on screen. The empty All pool is a
            // broken card database rather than a state a player can reach, so it keeps the filter text.
            if (_emptyLabel != null)
            {
                _emptyLabel.text = _pool.Count == 0 && OwnedOnly
                    ? "You don't own any cards yet — switch to All to see the game."
                    : "Nothing matches those filters.";
                _emptyLabel.EnableInClassList("col-empty--on", shown == 0);
            }
        }

        // ---- The live legality loop: single source, runs on every mutation ----
        private void Refresh()
        {
            if (_deckList != null)
            {
                _deckList.itemsSource = _deck.Entries;
                _deckList.RefreshItems();
            }
            _wall.RefreshTiles();
            RebuildCurve();

            if (_deckCountLabel != null)
                _deckCountLabel.text = $"{_deck.TotalCount} / {DeckValidator.DeckSize}";
        }

        // Bar height is pixels, not Length.Percent — curve-col is content-sized (no explicit height,
        // since .db-curve uses align-items: flex-end rather than stretch), so a percent height has no
        // parent size to resolve against and silently collapses to 0. This is why the curve never
        // showed anything. maxBarPx is db-curve's 56px strip minus the cost label's line height +
        // margin below each bar.
        private const float CurveMaxBarPx = 36f;

        private void RebuildCurve()
        {
            if (_curve == null) return;

            _curve.Clear();
            const int buckets = 8;                 // 0..6, 7+
            var counts = new int[buckets];
            int peak = 1;
            foreach (var e in _deck.Entries)
            {
                if (!_db.TryGet(e.CardId, out var def)) continue;
                int b = Mathf.Clamp(def.Cost, 0, buckets - 1);
                counts[b] += e.Count;
                if (counts[b] > peak) peak = counts[b];
            }
            for (int i = 0; i < buckets; i++)
            {
                var col = new VisualElement(); col.AddToClassList("curve-col");
                var bar = new VisualElement(); bar.AddToClassList("curve-bar");
                bar.style.height = CurveMaxBarPx * counts[i] / peak;
                var label = new Label(i < buckets - 1 ? i.ToString() : "7+");
                label.AddToClassList("curve-label");
                col.Add(bar); col.Add(label);
                _curve.Add(col);
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor/spike-only: fill the deck toward <paramref name="target"/> cards from the pool,
        /// respecting L1–L5 via the real add path, to exercise the live legality loop for verification.
        /// </summary>
        public void DevFill(int target)
        {
            if (!Deckbuilding) return;
            bool progressed = true;
            while (_deck.TotalCount < target && progressed)
            {
                progressed = false;
                foreach (var def in _pool)
                {
                    if (_deck.TotalCount >= target) break;
                    if (DeckValidator.CanAdd(_deck, _character, def, _owned)) { AddCopy(def); progressed = true; }
                }
            }
        }
#endif

        // ---- token helpers (class names mirror deckbuilder.uss) ----
        private static readonly string[] AllRarity = { "rarity--common", "rarity--rare", "rarity--epic", "rarity--legendary" };
        private static readonly string[] AllEdge = { "edge--neutral", "edge--emberward", "edge--tidecaller", "edge--gravekeep", "edge--verdant", "edge--tempest", "edge--nightveil", "edge--clockwright", "edge--stonewarden" };

        private static string RarityClass(Rarity r) => r switch
        {
            Rarity.RARE => "rarity--rare",
            Rarity.EPIC => "rarity--epic",
            Rarity.LEGENDARY => "rarity--legendary",
            _ => "rarity--common",
        };

        private static string ClassEdgeClass(CardClass c) => c switch
        {
            CardClass.EMBERWARD => "edge--emberward",
            CardClass.TIDECALLER => "edge--tidecaller",
            CardClass.GRAVEKEEP => "edge--gravekeep",
            CardClass.VERDANT => "edge--verdant",
            CardClass.CLOCKWRIGHT => "edge--clockwright",
            CardClass.STONEWARDEN => "edge--stonewarden",
            CardClass.NIGHTVEIL => "edge--nightveil",
            CardClass.TEMPEST => "edge--tempest",
            _ => "edge--neutral",
        };

        private static void SwapPrefixedClass(VisualElement e, string prefix, string keep)
        {
            var all = prefix == "rarity--" ? AllRarity : AllEdge;
            foreach (var c in all) e.EnableInClassList(c, c == keep);
        }
    }
}
