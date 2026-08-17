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
    /// The one persistent card-collection screen (NOR-210). Browsing is the screen; editing a deck is
    /// an optional context presented in a right-side drawer. Opening that drawer never replaces the
    /// query bar, card wall, background or scene, so the old deckbuilder is no longer visible
    /// to the player as a second screen wearing shared controls.
    ///
    /// <para><see cref="PendingCharacterId"/> and <see cref="PendingDeckId"/> are only navigation
    /// conveniences for an existing roster doorway. They request that the drawer open after the
    /// Collection shell is live; they do not decide the screen's shape. Both are consumed once.</para>
    ///
    /// <para><b>It shows what you own when the account knows, and the whole pool when it doesn't.</b>
    /// Ownership has three states, not two:</para>
    ///
    /// <list type="bullet">
    /// <item>No ownership record (<see cref="PlayerAccount.OwnedCards"/> returns null) — nobody has told
    /// this account what it owns. The wall shows the full collectible pool and the scope toggle stays
    /// hidden. This is every machine today.</item>
    /// <item>A record, scoped to it — the wall shows only cards with at least one copy, and Owned is
    /// highlighted. This is the default the moment a grant path writes that record.</item>
    /// <item>A record, showing everything anyway — the Owned/All toggle (#146). Same wall as the first
    /// state, with All highlighted, from a completely different reason.</item>
    /// </list>
    ///
    /// <para><b>So the player-facing copy rule holds in two of the three:</b> do not describe this
    /// screen as a collection of owned cards while it is showing the pool. When ownership is known,
    /// the scope toggle is the visible source of truth for which pool the player is viewing.</para>
    ///
    /// <para><b>The toggle is a scope, not a facet</b>, which is why it is not a chip on
    /// <see cref="CardQueryBar"/>: the bar's chips are generated from the card enums and filter a pool,
    /// while this decides which pool there is. It therefore rebuilds the pool rather than re-querying,
    /// and <see cref="CardQueryBar.Reset"/> deliberately leaves it alone — resetting the filters should
    /// not silently move you between your collection and the game.</para>
    ///
    /// <para><b>Deck editing widens ownership scope to All while it is active</b>, preserving #150's
    /// visible-but-spent rule for unowned cards. Closing the editor restores the scope the player had
    /// before opening it. The query object itself stays mounted, so search, sort and facet selections
    /// survive the round trip.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// CollectionScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CollectionController : MonoBehaviour
    {
        /// <summary>Where the persistent screen's Back control returns. Set by the caller.</summary>
        public static string ReturnScene = "MainMenu";

        /// <summary>Which character to build a deck for, or empty to browse. Consumed on read.</summary>
        public static string PendingCharacterId;

        /// <summary>Which saved deck to open, or empty to use that character's normal opening deck.</summary>
        public static string PendingDeckId;

        private PlayerAccount _account;
        private ICardDatabase _db;
        private CardFaceBaker _faceBaker;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();

        /// <summary>The optional deck-edit context. Null means the drawer is browsing deck choices.</summary>
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

        // Cached elements. The collection shell is permanent; only the drawer's chooser/editor panes
        // trade visibility.
        private VisualElement _root;
        private VisualElement _plate;
        private VisualElement _grid;
        private CardWall _wall;
        private CardQueryBar _query;
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
        private VisualElement _drawer;
        private ScrollView _deckChooser;
        private VisualElement _deckChooserList;
        private Label _deckChooserEmpty;
        private VisualElement _deckEditor;
        private Label _drawerTitle;
        private Label _drawerSubtitle;
        private Label _deckCharacterName;
        private Label _deckName;
        private bool _drawerOpen;
        private bool _scopeBeforeEditing;

        /// <summary>The active deck's pairing-chip selections. Multi-select, and empty
        /// means unconstrained — the same all-off convention <see cref="CardQueryBar"/> uses for its
        /// own class facet. Composed into the one wall query by <see cref="ApplyQuery"/>.</summary>
        private readonly HashSet<CardClass> _chipClasses = new HashSet<CardClass>();

        private bool EditingDeck => _character != null && _deck != null;

        /// <summary>
        /// True when the wall is scoped to what this account owns — the one predicate for it, read by
        /// both the pool and the scope controls so the two can never disagree about which pool the
        /// player is looking at.
        ///
        /// </summary>
        private bool OwnedOnly => _owned != null && _ownedScope;

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

            CacheElements();
            BuildPool();
            InitFaceBaker();
            BuildWall();
            WireQueryBar();
            WireCollectionFilters();
            WireScopeToggle();
            WireDeckList();
            WireChrome();

            ApplyQuery();
            WireCollectionScroll();
            OpenPendingDeckIfAny();
        }

        /// <summary>
        /// Consume the legacy roster handoff after the Collection shell is fully live. A broken id
        /// leaves the player on the truthful collection wall rather than producing a half-open drawer.
        /// </summary>
        private void OpenPendingDeckIfAny()
        {
            string pendingCharacter = PendingCharacterId;
            PendingCharacterId = "";
            string pendingDeck = PendingDeckId;
            PendingDeckId = "";
            if (string.IsNullOrEmpty(pendingCharacter)) return;

            Character character = null;
            IReadOnlyList<Character> roster = _account.Roster();
            foreach (var c in roster)
                if (c.Id == pendingCharacter) { character = c; break; }

            if (character == null)
            {
                Debug.LogWarning($"[Collection] no character '{pendingCharacter}' on this account; " +
                                 "leaving the deck drawer closed.");
                return;
            }

            CharacterDecks characterDecks = DeckQuery.ForCharacter(
                character, _account.Decks(), _db, _owned);
            DeckList deck = FindDeck(characterDecks.Decks, pendingDeck)
                            ?? characterDecks.Opening?.Deck;
            if (deck == null)
            {
                Debug.LogWarning($"[Collection] '{character.Name}' has no deck to open; " +
                                 "opening the deck chooser instead.");
                OpenDeckDrawer();
                return;
            }

            OpenDeckDrawer();
            OpenDeckEditor(character, deck);
        }

        private static DeckList FindDeck(IReadOnlyList<DeckSummary> decks, string deckId)
        {
            if (string.IsNullOrEmpty(deckId)) return null;
            for (int i = 0; i < decks.Count; i++)
                if (decks[i].Deck.Id == deckId) return decks[i].Deck;
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
                               "of your collection.");
            }
        }

        /// <summary>
        /// The wall's pool, scoped by the optional edit context.
        ///
        /// <para><b>Editing</b> — the character's pairing plus neutral. Opening an editor switches
        /// ownership scope to All, so
        /// an unowned card stays on the wall and <see cref="AddCopy"/> refuses it at the copy cap,
        /// because the copy limit is already <c>min(MaxCopies, copies owned)</c>. Removing it from the
        /// pool instead would make a card you nearly own invisible, and #150 wants it dimmed.</para>
        ///
        /// <para><b>At rest</b> — every card this account owns a copy of, or, when it has no ownership
        /// record or the player has asked for All (#146), every collectible card in the game.</para>
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
                if (EditingDeck && !_character.AllowsClass(def.Class)) continue;
                if (OwnedOnly && _owned.CopiesOwned(def.Id) <= 0) continue;

                _pool.Add(def);
            }
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));
        }

        private void CacheElements()
        {
            _plate           = _root.Q<VisualElement>("root");
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
            _drawer              = _root.Q<VisualElement>("deck-drawer");
            _deckChooser         = _root.Q<ScrollView>("deck-chooser");
            _deckChooserList     = _root.Q<VisualElement>("deck-chooser-list");
            _deckChooserEmpty    = _root.Q<Label>("deck-chooser-empty");
            _deckEditor          = _root.Q<VisualElement>("deck-editor");
            _drawerTitle         = _root.Q<Label>("deck-drawer-title");
            _drawerSubtitle      = _root.Q<Label>("deck-drawer-subtitle");
            _deckCharacterName   = _root.Q<Label>("deck-character-name");
            _deckName            = _root.Q<Label>("deck-name");
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
        // live in CardWall (Sam, 2026-08-07), so every drawer state renders the same wall. What stays here is
        // the part that is genuinely about a DECK: what a tap does, and which tiles are at their cap.
        private void BuildWall()
        {
            _wall = new CardWall(_grid, _faceBaker);
            _wall.TileClicked += AddCopy;

            // The copy-cap fade is the only per-tile deck treatment. The decorator remains installed
            // because the wall remains installed; it simply has nothing to say without an edit context.
            _wall.TileDecorator = (def, tile) =>
                tile.EnableInClassList("card-tile--maxed",
                    EditingDeck && _deck.CountOf(def.Id) >=
                    DeckValidator.CopyLimit(def.Rarity, _owned, def.Id));

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
        // The class axis while editing. Three chips — the character's primary class, its secondary
        // class, and NEUTRAL — each a multi-select toggle where all-off means unconstrained, matching
        // CardQueryBar's own class-facet convention. That is what replaced the old radio All/Class/
        // Neutral set: "all" is now nothing selected, not a fourth chip. Labels are the real class
        // names, written here at runtime rather than authored in UXML, so a rename can never drift.
        //
        // These do NOT write the wall themselves. Both filter surfaces — these chips and the query
        // bar — compose into one CardFilter at the single ApplyQuery site, so neither clobbers the
        // other (and, critically, a chip press no longer resets the player's chosen sort).
        private void WireCollectionFilters()
        {
            _filterPrimaryButton?.RegisterCallback<ClickEvent>(_ =>
                TogglePairingClass(_character?.PrimaryClass));
            _filterSecondaryButton?.RegisterCallback<ClickEvent>(_ =>
                TogglePairingClass(_character?.SecondaryClass));
            _filterNeutralButton?.RegisterCallback<ClickEvent>(_ =>
                TogglePairingClass(EditingDeck ? CardClass.NEUTRAL : (CardClass?)null));
        }

        private void ConfigureCollectionFilters()
        {
            _chipClasses.Clear();
            if (!EditingDeck) return;

            if (_filterPrimaryButton != null)
                _filterPrimaryButton.text = ClassLabel(_character.PrimaryClass);
            if (_filterSecondaryButton != null)
                _filterSecondaryButton.text = ClassLabel(_character.SecondaryClass);
            if (_filterNeutralButton != null)
                _filterNeutralButton.text = ClassLabel(CardClass.NEUTRAL);
            StampChips();
        }

        private void TogglePairingClass(CardClass? klass)
        {
            if (!EditingDeck || !klass.HasValue) return;
            if (!_chipClasses.Remove(klass.Value)) _chipClasses.Add(klass.Value);
            StampChips();
            ApplyQuery();
        }

        // Every chip is repainted from the set, not just the one clicked — a character whose primary
        // and secondary happen to coincide would otherwise leave the other chip's highlight stale.
        private void StampChips()
        {
            if (!EditingDeck) return;
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
        /// <para>The persistent query bar keeps this scope visible while the drawer is open. Opening an
        /// editor selects All for #150; the player can still narrow to Owned explicitly.</para>
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

        // Both chips are written on every change so the control cannot retain stale state.
        private void StampScope()
        {
            _scopeOwnedButton.EnableInClassList("db-chip--on", OwnedOnly);
            _scopeAllButton.EnableInClassList("db-chip--on", !OwnedOnly);
        }

        // ---- Right-side deck drawer (NOR-210) -----------------------------------------------
        private void OpenDeckDrawer()
        {
            if (_drawer == null)
            {
                Debug.LogError("[Collection] 'deck-drawer' is missing from CollectionScreen.uxml.");
                return;
            }

            _drawerOpen = true;
            _drawer.AddToClassList("deck-drawer--open");
            if (EditingDeck) ShowDrawerPane(editor: true);
            else
            {
                RebuildDeckChooser();
                ShowDrawerPane(editor: false);
            }
        }

        private void CloseDeckDrawer()
        {
            if (!_drawerOpen) return;
            if (EditingDeck) SaveDeck();
            EndDeckEditing();
            _drawerOpen = false;
            _drawer?.RemoveFromClassList("deck-drawer--open");
        }

        private void ShowDeckChooser()
        {
            if (EditingDeck) SaveDeck();
            EndDeckEditing();
            RebuildDeckChooser();
            ShowDrawerPane(editor: false);
        }

        private void ShowDrawerPane(bool editor)
        {
            _deckChooser?.EnableInClassList("deck-chooser--hidden", editor);
            _deckEditor?.EnableInClassList("deck-editor--hidden", !editor);
            if (_drawerTitle != null) _drawerTitle.text = editor ? "Editing Deck" : "Decks";
            if (_drawerSubtitle != null)
                _drawerSubtitle.text = editor ? "Tap a card to add it" : "Choose a deck to edit";
        }

        private void RebuildDeckChooser()
        {
            if (_deckChooserList == null) return;
            _deckChooserList.Clear();

            IReadOnlyList<Character> characters = _account.Roster();
            IReadOnlyList<DeckList> decks = _account.Decks();
            int choices = 0;

            foreach (Character character in characters)
            {
                CharacterDecks characterDecks = DeckQuery.ForCharacter(character, decks, _db, _owned);
                if (characterDecks.Count == 0) continue;

                var group = new VisualElement();
                group.AddToClassList("deck-choice-group");

                var characterName = new Label(character.Name);
                characterName.AddToClassList("deck-choice-character");
                group.Add(characterName);

                var pairing = new Label(
                    $"{ClassLabel(character.PrimaryClass)} + {ClassLabel(character.SecondaryClass)}");
                pairing.AddToClassList("deck-choice-pairing");
                group.Add(pairing);

                foreach (DeckSummary summary in characterDecks.Decks)
                {
                    DeckList capturedDeck = summary.Deck;
                    Character capturedCharacter = character;
                    string defaultMark = summary.IsDefault ? " · Default" : "";
                    string standing = summary.IsDraft ? "Draft" : "Ready";
                    var button = new Button(() => OpenDeckEditor(capturedCharacter, capturedDeck))
                    {
                        text = $"{capturedDeck.Name}  ·  {summary.TotalCount}/{DeckValidator.DeckSize}" +
                               $"  ·  {standing}{defaultMark}"
                    };
                    button.AddToClassList("deck-choice");
                    button.AddToClassList(ClassEdgeClass(character.PrimaryClass));
                    group.Add(button);
                    choices++;
                }

                _deckChooserList.Add(group);
            }

            if (_deckChooserEmpty != null)
            {
                _deckChooserEmpty.text = choices == 0
                    ? "No decks yet — manage characters and decks to create one."
                    : "";
                _deckChooserEmpty.EnableInClassList("deck-chooser__empty--on", choices == 0);
            }
        }

        private void OpenDeckEditor(Character character, DeckList deck)
        {
            if (character == null || deck == null) return;
            if (EditingDeck) SaveDeck();

            _scopeBeforeEditing = _ownedScope;
            _ownedScope = false; // #150: unowned eligible cards stay visible-but-spent while editing.
            _character = character;
            _deck = deck;

            if (_deckCharacterName != null)
                _deckCharacterName.text = $"{character.Name} · " +
                                          $"{ClassLabel(character.PrimaryClass)} + " +
                                          ClassLabel(character.SecondaryClass);
            if (_deckName != null) _deckName.text = deck.Name;

            ConfigureCollectionFilters();
            StampEditing();
            StampScope();
            BuildPool();
            _wall.SetPool(_pool);
            BindOpenDeck();
            ApplyQuery();
            Refresh();
            ShowDrawerPane(editor: true);
        }

        private void EndDeckEditing()
        {
            if (!EditingDeck) return;

            _character = null;
            _deck = null;
            _chipClasses.Clear();
            _ownedScope = _scopeBeforeEditing;
            StampEditing();
            StampScope();

            if (_deckList != null)
            {
                _deckList.itemsSource = null;
                _deckList.RefreshItems();
            }

            BuildPool();
            _wall.SetPool(_pool);
            ApplyQuery();
            _wall.RefreshTiles();
        }

        private void StampEditing()
        {
            _plate?.EnableInClassList("collection--editing", EditingDeck);
        }

        private void ManageCharactersAndDecks()
        {
            if (EditingDeck) SaveDeck();
            CharacterRosterController.ReturnScene = "Collection";
            Go("CharacterRoster");
        }

        // ---- Deck ListView ----
        private void WireDeckList()
        {
            if (_deckList == null) return;

            _deckList.fixedItemHeight = 54;
            _deckList.selectionType = SelectionType.None;
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

        private void BindOpenDeck()
        {
            if (_deckList == null || !EditingDeck) return;
            _deckList.itemsSource = _deck.Entries;
            _deckList.RefreshItems();
        }

        private void BindDeckRow(VisualElement row, int index)
        {
            if (!EditingDeck || index < 0 || index >= _deck.Entries.Count)
            {
                row.style.display = DisplayStyle.None;
                return;
            }
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
            if (!EditingDeck) return;
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
            if (!EditingDeck) return;
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
        // NavigationCancelEvent. The gesture closes the drawer first, then exits Collection on the
        // next invocation, regardless of wall scroll position.
        //
        // The DEVELOPMENT_BUILD block logs the data the issue asks for: scroll offset, grid content
        // height, and layout call count sampled every 500ms. On a loop-free build the layout count
        // should only tick up on resize or filter changes, not while idle or during a scroll gesture.
        private void WireCollectionScroll()
        {
            _root.RegisterCallback<NavigationCancelEvent>(_ => HandleBack());

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
        // One persistent Back: dismiss and save the transient drawer first, leave Collection second.
        private void WireChrome()
        {
            _root.Q<Button>("decks-button")?.RegisterCallback<ClickEvent>(_ => OpenDeckDrawer());
            _root.Q<Button>("back-button")?.RegisterCallback<ClickEvent>(_ => HandleBack());
            _root.Q<Button>("deck-drawer-close")?.RegisterCallback<ClickEvent>(_ => CloseDeckDrawer());
            _root.Q<Button>("choose-another-deck")?.RegisterCallback<ClickEvent>(_ => ShowDeckChooser());
            _root.Q<Button>("manage-decks-button")?.RegisterCallback<ClickEvent>(_ => ManageCharactersAndDecks());
        }

        private void HandleBack()
        {
            if (_drawerOpen)
            {
                CloseDeckDrawer();
                return;
            }
            Exit();
        }

        private void Exit()
        {
            if (EditingDeck) SaveDeck();
            Go(ReturnScene);
        }

        /// <summary>Persist the deck as-is. Never legality-gated (collection-deckbuilder-spec §1,
        /// Sam 2026-07-26): an illegal deck is a savable draft, not a blocked action — only queue
        /// eligibility (computed fresh on load, never stored) refuses it a match.</summary>
        private void SaveDeck()
        {
            if (!EditingDeck) return;
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
        /// and (while editing) the pairing chips — into a single <see cref="CardFilter"/>, so neither
        /// clobbers the other the way two separate <c>SetQuery</c> writers did before #160.
        ///
        /// <para>The bar's class selections are suppressed, not cleared, while editing. The active
        /// deck's three pairing chips own that axis; closing the drawer restores exactly the browse
        /// class filter the player had before opening it.</para>
        ///
        /// <para>Runs with or without an edit context; on a plate that declares no query controls the filter is empty,
        /// which matches everything. The sort is always the bar's own — a chip press no longer reverts
        /// it to default.</para>
        /// </summary>
        private void ApplyQuery()
        {
            CardFilter filter = _query.Filter;
            if (EditingDeck)
                filter.Classes = _chipClasses.Count > 0
                    ? new List<CardClass>(_chipClasses)
                    : null;

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
        /// Keeps the wall's empty state in sync with every path that changes what the wall shows.
        /// </summary>
        private void UpdateWallLabels()
        {
            int shown = _wall.Visible.Count;

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
            if (!EditingDeck) return;
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
            if (_curve == null || !EditingDeck) return;

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
            if (!EditingDeck) return;
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
