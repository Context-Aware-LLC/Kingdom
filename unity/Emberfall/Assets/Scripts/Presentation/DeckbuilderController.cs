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
    /// The S7 deckbuilder slice (dev-docs/ui/client-surfaces.md:9), grown out of the C-G0-5 spike:
    /// ListView virtualization of the full card pool, live L1–L5 legality (via
    /// <see cref="DeckValidator"/>) driving the legality pill, single-source theme.uss token
    /// theming, the 22px text floor, and the responsive split view.
    ///
    /// <para>Opens one real <see cref="Character"/>'s deck from <see cref="PlayerAccount.Local"/> —
    /// <see cref="PendingCharacterId"/>, set by <c>MainMenuController.OnDeckBuilder</c> before this
    /// scene loads, or the roster's first character when that is empty (so the scene still opens
    /// standalone in the Editor).</para>
    ///
    /// <para>All of <b>that character's</b> decks are reachable from the switcher bar — open, create,
    /// delete and set-default (EF-046). Switching <b>character</b> is still not possible from here:
    /// that is the roster screen, which does not exist yet, and until it does <c>Done</c> returns to
    /// MainMenu. RETIREMENT TRIGGER: when the roster screen lands, point <c>Done</c> at it and update
    /// this paragraph.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// DeckbuilderScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DeckbuilderController : MonoBehaviour
    {
        /// <summary>Which character to open, set by the caller before loading this scene.</summary>
        public static string PendingCharacterId;

        private PlayerAccount _account;
        private Character _character;
        private ICardDatabase _db;
        private DeckList _deck;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private CardFaceBaker _faceBaker;

        // Cached elements.
        private VisualElement _root;
        private VisualElement _collectionGrid;
        private readonly Dictionary<string, VisualElement> _collectionTiles = new Dictionary<string, VisualElement>();
        private ListView _deckList;
        private VisualElement _curve;
        private VisualElement _legalityBox;
        private Label _countLabel;
        private Label _legalityPill;
        private Label _heroLabel;
        private Button _saveButton;
        private Button _filterAllButton;
        private Button _filterClassButton;
        private Button _filterNeutralButton;
        private CardFilter _activeFilter;   // null = All (no constraint)
        private VisualElement _deckBar;
        private Button _openChip;            // the chip for _deck, retitled in place while renaming
        private bool _openChipIsDefault;
        private TextField _nameField;
        private Label _defaultHint;
        private Button _setDefaultButton;
        private Button _deleteDeckButton;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Deckbuilder] No rootVisualElement — is the UXML assigned?"); return; }

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Deckbuilder] could not open this device's collection: {ex.Message}");
                return;
            }

            IReadOnlyList<Character> roster = _account.Roster();
            _character = FindPending(roster) ?? (roster.Count > 0 ? roster[0] : null);
            if (_character == null)
            {
                Debug.LogError("[Deckbuilder] No character on this account — Main Menu should have " +
                                "gated this scene behind character creation.");
                return;
            }

            _deck = _account.DecksOf(_character, _db).Opening?.Deck;
            if (_deck == null)
            {
                Debug.LogError($"[Deckbuilder] '{_character.Name}' has no deck to open.");
                return;
            }

            BuildPool();
            CacheElements();
            InitFaceBaker();
            BuildCollectionGrid();
            WireDeckList();
            WireCollectionFilters();
            WireChrome();
            RebuildDeckBar();
            Refresh();
        }

        // The real card, baked to a texture (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.9) — one
        // card front implementation (BoardController.CreatePresenter), photographed, never a
        // second UI Toolkit re-implementation of the card face drifting from it.
        private void InitFaceBaker()
        {
            var bakerGo = new GameObject("CardFaceBaker");
            bakerGo.transform.SetParent(transform, false);
            _faceBaker = bakerGo.AddComponent<CardFaceBaker>();
            _faceBaker.Initialize(_db);
        }

        private Character FindPending(IReadOnlyList<Character> roster)
        {
            if (string.IsNullOrEmpty(PendingCharacterId)) return null;
            foreach (var c in roster)
                if (c.Id == PendingCharacterId) return c;
            return null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor/spike-only: fill the deck toward <paramref name="target"/> cards from the pool,
        /// respecting L1–L5 via the real add path, to exercise the live legality loop for verification.
        /// </summary>
        public void DevFill(int target)
        {
            bool progressed = true;
            while (_deck.TotalCount < target && progressed)
            {
                progressed = false;
                foreach (var def in _pool)
                {
                    if (_deck.TotalCount >= target) break;
                    if (DeckValidator.CanAdd(_deck, _character, def)) { AddCopy(def); progressed = true; }
                }
            }
        }
#endif

        // ---- Pool: character's pairing ∪ neutral, sorted by cost then name (§2 left list) ----
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
                if (_character.AllowsClass(def.Class))
                    _pool.Add(def);
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));
        }

        private void CacheElements()
        {
            _collectionGrid = _root.Q<VisualElement>("collection-grid");
            _collectionGrid.RegisterCallback<GeometryChangedEvent>(QueueCollectionLayout);
            _deckList       = _root.Q<ListView>("deck-list");
            _curve          = _root.Q<VisualElement>("curve-strip");
            _legalityBox    = _root.Q<VisualElement>("legality-box");
            _countLabel     = _root.Q<Label>("count-label");
            _legalityPill   = _root.Q<Label>("legality-pill");
            _heroLabel      = _root.Q<Label>("hero-label");
            _saveButton     = _root.Q<Button>("save-button");
            _filterAllButton     = _root.Q<Button>("filter-all");
            _filterClassButton   = _root.Q<Button>("filter-class");
            _filterNeutralButton = _root.Q<Button>("filter-neutral");
            _deckBar          = _root.Q<VisualElement>("deck-bar");
            _nameField        = _root.Q<TextField>("deck-name");
            _defaultHint      = _root.Q<Label>("default-hint");
            _setDefaultButton = _root.Q<Button>("set-default-button");
            _deleteDeckButton = _root.Q<Button>("delete-deck-button");

            if (_heroLabel != null)
            {
                _heroLabel.text = $"{_character.Name} · {Pretty(_character.PrimaryClass)}/{Pretty(_character.SecondaryClass)}";
                _heroLabel.style.color = ClassColorToken(_character.PrimaryClass);
            }
        }

        // ---- Collection wall (§2.9): one persistent tile per pool card, built once. Not
        // virtualized — ~90 cards of baked (cached) textures is cheap next to the 13-renderer
        // hierarchy a virtualized ListView of live cards would need instead. ----
        private void BuildCollectionGrid()
        {
            _collectionGrid.Clear();
            _collectionTiles.Clear();

            foreach (var def in _pool)
            {
                var tile = new VisualElement();
                tile.AddToClassList("card-tile");
                tile.userData = def;

                tile.RegisterCallback<ClickEvent>(_ => AddCopy(def));

                string defId = def.Id;
                _faceBaker.Bake(def, tex =>
                {
                    if (tex == null) return;
                    tile.style.backgroundImage = new StyleBackground(Background.FromRenderTexture((RenderTexture)tex));
                });

                _collectionGrid.Add(tile);
                _collectionTiles[def.Id] = tile;
            }

            QueueCollectionLayout();
        }

        // ---- Collection grid gutter (Sam, 2026-08-02): tile size is fixed — a bake-cache wall,
        // never stretched to fill space — so the only lever is the gutter between tiles. Recomputed
        // per row on every resize (deck panel opening/closing, window resize) so full rows always
        // span the panel edge to edge instead of packing left with dead space on the right; a
        // partial last row stays left-aligned at the same gutter rather than spreading apart. This
        // is the manual equivalent of CSS Grid's `repeat(auto-fill, TileWidth)` + `justify-content:
        // space-between`, which UI Toolkit's flexbox-only layout has no direct way to express.
        private const float CardTileWidth = 276f;  // must match .card-tile width in deckbuilder.uss
        private const float MinGutter = 16f;        // floor == --space-4; never squeeze tighter than that token
        private bool _collectionLayoutQueued;

        /// <summary>Defers to the next scheduler tick instead of running inline. The grid's own
        /// GeometryChangedEvent is the trigger, and Yoga fires that mid-layout-pass — mutating
        /// margins synchronously from inside it edits the tree while Yoga is still walking it, so
        /// only whichever tiles it hasn't visited yet pick up the new values. That tears a single
        /// pass into two different geometries across rows and never converges, since the mutation
        /// re-triggers the same event. Coalesces bursts (resize + filter click) into one pass.</summary>
        private void QueueCollectionLayout(GeometryChangedEvent evt = default)
        {
            if (_collectionLayoutQueued) return;
            _collectionLayoutQueued = true;
            _collectionGrid.schedule.Execute(() =>
            {
                _collectionLayoutQueued = false;
                LayoutCollectionGrid();
            });
        }

        // A row solved to land EXACTLY on the available width is a floating-point tie: the tile's
        // real resolved width (measured live: 275.9598px, not the authored 276px) never matches
        // CardTileWidth to the decimal, so the row Yoga actually lays out overshoots by a hair and
        // it silently wraps one tile early — our margins still assumed the wider row, so the tile
        // that should've been last-in-row lands first-in-next-row with its trailing margin zeroed,
        // flush against its new neighbour. This margin keeps every row comfortably under budget.
        private const float RowSafetyMargin = 2f;

        private void LayoutCollectionGrid()
        {
            float available = _collectionGrid.resolvedStyle.width - RowSafetyMargin;
            if (available <= 0f) return;

            int columns = Mathf.Max(1, Mathf.FloorToInt((available + MinGutter) / (CardTileWidth + MinGutter)));
            float gutter = columns > 1 ? (available - columns * CardTileWidth) / (columns - 1) : 0f;

            // Visibility and spacing are computed together: a filtered-out tile takes no row slot,
            // so the column math below must walk only the tiles the active filter keeps.
            var visible = new List<VisualElement>(_pool.Count);
            foreach (var def in _pool)
            {
                if (!_collectionTiles.TryGetValue(def.Id, out var tile)) continue;
                bool show = CardQuery.Matches(def, _activeFilter);
                tile.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (show) visible.Add(tile);
            }

            for (int i = 0; i < visible.Count; i++)
            {
                bool endOfRow = (i % columns) == columns - 1 || i == visible.Count - 1;
                visible[i].style.marginLeft = 0f;
                visible[i].style.marginRight = endOfRow ? 0f : gutter;
            }
        }

        // ---- Collection filter chips (All / Class / Neutral): the three that already exist in
        // the UXML, previously unwired — clicking them did nothing. Class = either half of the
        // character's pairing, excluding neutral; Neutral = CardClass.NEUTRAL only; All = no
        // constraint. Filtering itself lives in CardQuery (EF-035, .NET-side and testable) — this
        // just toggles per-tile visibility and re-runs the grid layout pass.
        private void WireCollectionFilters()
        {
            _filterAllButton.RegisterCallback<ClickEvent>(_ => ApplyCollectionFilter(null, _filterAllButton));
            _filterClassButton.RegisterCallback<ClickEvent>(_ => ApplyCollectionFilter(
                new CardFilter { Classes = new[] { _character.PrimaryClass, _character.SecondaryClass } },
                _filterClassButton));
            _filterNeutralButton.RegisterCallback<ClickEvent>(_ => ApplyCollectionFilter(
                new CardFilter { Classes = new[] { CardClass.NEUTRAL } },
                _filterNeutralButton));
        }

        private void ApplyCollectionFilter(CardFilter filter, Button active)
        {
            _activeFilter = filter;
            _filterAllButton.EnableInClassList("db-chip--on", active == _filterAllButton);
            _filterClassButton.EnableInClassList("db-chip--on", active == _filterClassButton);
            _filterNeutralButton.EnableInClassList("db-chip--on", active == _filterNeutralButton);
            QueueCollectionLayout();
        }

        private void RefreshCollectionTiles()
        {
            foreach (var def in _pool)
            {
                if (!_collectionTiles.TryGetValue(def.Id, out VisualElement tile)) continue;

                tile.EnableInClassList("card-tile--disabled", !DeckValidator.CanAdd(_deck, _character, def));
            }
        }

        // ---- Deck ListView ----
        private void WireDeckList()
        {
            _deckList.fixedItemHeight = 54;
            _deckList.selectionType = SelectionType.None;
            _deckList.itemsSource = _deck.Entries;
            _deckList.makeItem = MakeCardRow;
            _deckList.bindItem = BindDeckRow;
            _deckList.RegisterCallback<ClickEvent>(OnDeckClick);

            // No visible scrollbars anywhere (Sam, 2026-08-01 ruling — see [[no-visible-scrollbars-ruling]]
            // in memory). ListView doesn't expose verticalScrollerVisibility itself — only its
            // internal ScrollView does — so this can't be set from UXML like collection-scroll's.
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
            var cost = new Label { name = "cost" };   cost.AddToClassList("card-cost");
            // var dot  = new VisualElement { name = "dot" }; dot.AddToClassList("card-rarity-dot");
            var name = new Label { name = "name" };   name.AddToClassList("card-name");
            //  row.add(dot); removed from the line below for offending someones sensibilities
            row.Add(cost); row.Add(name);
            return row;
        }

        private static void SetCardRow(VisualElement row, CardDefinition def)
        {
            row.Q<Label>("cost").text = def.Cost.ToString();
            row.Q<Label>("name").text = def.Name;

            SwapPrefixedClass(row, "edge--", ClassEdgeClass(def.Class));
        }

        // ---- Mutations ----
        private void AddCopy(CardDefinition def)
        {
            if (!DeckValidator.CanAdd(_deck, _character, def)) return;   // L1 ceiling / L2/L3 cap / L4 off-class
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

        // ---- Chrome (name field, buttons) ----
        private void WireChrome()
        {
            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify(_deck.Name);
                // Rename is a deck edit like any other: it lands in memory here and reaches disk
                // through Save, the switcher's save-on-leave, or Done. It is never a separate write.
                // Retitles the open chip in place rather than rebuilding the bar: RebuildDeckBar
                // re-reads every deck from the store, and per keystroke that is a file read per
                // character typed.
                _nameField.RegisterValueChangedCallback(e =>
                {
                    _deck.Name = e.newValue;
                    if (_openChip != null) _openChip.text = ChipLabel(_deck.Name, _openChipIsDefault);
                });
            }
            _setDefaultButton?.RegisterCallback<ClickEvent>(_ => SetDefaultDeck());
            _deleteDeckButton?.RegisterCallback<ClickEvent>(_ => DeleteOpenDeck());
            _saveButton?.RegisterCallback<ClickEvent>(_ => SaveDeck());

            // Done saves. The alternative is a confirm dialog on a screen whose whole model is
            // "an illegal deck is a savable draft" — there is nothing to lose by writing it.
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ =>
            {
                SaveDeck();
                Go("MainMenu");
            });
        }

        // ---- Deck switcher and lifecycle (collection-deckbuilder-spec §2.6) ----

        /// <summary>
        /// Rebuild the switcher from disk, default first — <see cref="DeckQuery.ForCharacter"/>'s
        /// order, so the bar and the queue never disagree about which deck is "the" deck.
        ///
        /// <para>Reads the store rather than caching a list: create, delete and set-default all
        /// write, and a cached switcher is the copy that goes stale without saying so.</para>
        /// </summary>
        private void RebuildDeckBar()
        {
            if (_deckBar == null) return;
            _deckBar.Clear();

            CharacterDecks decks = _account.DecksOf(_character, _db);

            _openChip = null;
            foreach (var summary in decks.Decks)
            {
                DeckList deck = summary.Deck;
                bool open = deck.Id == _deck.Id;

                // The open deck's name lives in _deck (the player may be mid-rename); every other
                // chip reads the stored copy.
                var chip = new Button { text = ChipLabel(open ? _deck.Name : deck.Name, summary.IsDefault) };
                chip.AddToClassList("db-deck-chip");
                chip.EnableInClassList("db-deck-chip--on", open);
                chip.EnableInClassList("db-deck-chip--draft", summary.IsDraft);
                chip.clicked += () => SwitchTo(deck.Id);
                _deckBar.Add(chip);

                if (open) { _openChip = chip; _openChipIsDefault = summary.IsDefault; }
            }

            var add = new Button { text = "+" };
            add.AddToClassList("db-deck-chip");
            add.AddToClassList("db-deck-chip--new");
            add.clicked += NewDeck;
            _deckBar.Add(add);

            // A character with zero decks is a screen that will not load: OnEnable opens
            // DecksOf(...).Opening and bails when it is null. So the floor is one deck, enforced
            // here rather than confirmed in a dialog — and "+" is right beside it.
            _deleteDeckButton?.SetEnabled(decks.Count > 1);
            _setDefaultButton?.SetEnabled(_character.DefaultDeckId != _deck.Id);

            // A DefaultDeckId naming a deleted deck is left dangling on purpose (Character.cs:
            // "surfaced, never repaired"), so this screen is what does the surfacing.
            bool dangling = !decks.DefaultResolved && !string.IsNullOrEmpty(_character.DefaultDeckId);
            if (_defaultHint != null)
            {
                _defaultHint.text = dangling
                    ? $"{_character.Name} has no default deck — the one it pointed at was deleted."
                    : "";
                _defaultHint.style.display = dangling ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// Open another of this character's decks, persisting the outgoing one first.
        ///
        /// <para>Saving on the way out is not a convenience: <c>_deck</c> is the only copy of the
        /// edits made since the last write, and repointing it would drop them with no undo and no
        /// prompt. Save is never legality-gated (see <see cref="SaveDeck"/>), so a half-built draft
        /// survives the switch exactly as it stands.</para>
        /// </summary>
        private void SwitchTo(string deckId)
        {
            if (_deck != null && deckId == _deck.Id) return;
            _account.Save(_deck);

            DeckList next = null;
            foreach (var s in _account.DecksOf(_character, _db).Decks)
                if (s.Deck.Id == deckId) { next = s.Deck; break; }

            if (next == null)
            {
                Debug.LogError($"[Deckbuilder] Deck '{deckId}' is no longer on disk; staying on '{_deck.Name}'.");
                RebuildDeckBar();
                return;
            }

            OpenDeck(next);
        }

        /// <summary>Default decks lead with a marker. Text, not an icon: there is no icon set on this
        /// screen yet, and a coloured chip alone would collide with the open-deck highlight.</summary>
        private static string ChipLabel(string name, bool isDefault) => isDefault ? "* " + name : name;

        /// <summary>Point the screen at a deck already known to exist, and repaint everything that
        /// reads from it. The one place <c>_deck</c> is reassigned after load.</summary>
        private void OpenDeck(DeckList deck)
        {
            _deck = deck;
            _nameField?.SetValueWithoutNotify(_deck.Name);
            RebuildDeckBar();
            Refresh();
        }

        /// <summary>
        /// Create an empty deck for this character and open it.
        ///
        /// <para><b>Empty, not generated.</b> <see cref="DefaultDeckGenerator"/> exists to hand a
        /// brand-new character something playable on day one; running it here would hand a player
        /// who asked for a blank deck 30 cards to dismantle first.</para>
        /// </summary>
        private void NewDeck()
        {
            _account.Save(_deck);

            var deck = new DeckList
            {
                Id = PlayerAccount.NewId(),
                CharacterId = _character.Id,     // L5: bound at birth, never inferred later
                Name = UnusedDeckName(),
            };
            _account.Save(deck);                 // on disk before it is open, so the switcher can see it

            OpenDeck(deck);
            Debug.Log($"[Deckbuilder] New deck '{_deck.Name}' for {_character.Name}.");
        }

        /// <summary>"New Deck", then "New Deck 2" — a duplicate name is legal but indistinguishable
        /// in the switcher, which is the only place a deck is chosen.</summary>
        private string UnusedDeckName()
        {
            const string root = "New Deck";
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _account.DecksOf(_character, _db).Decks) taken.Add(s.Deck.Name);

            if (!taken.Contains(root)) return root;
            for (int n = 2; ; n++)
            {
                string candidate = root + " " + n;
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Delete the open deck and fall back to this character's default.
        ///
        /// <para><b>The dangling default is left dangling.</b> Deleting the deck a character
        /// defaults to does NOT clear <see cref="Character.DefaultDeckId"/> — that is the documented
        /// contract on the field itself ("a dangling id is surfaced, never repaired"), because
        /// silently re-pointing it means the character opens on a deck the player never chose.
        /// <see cref="RebuildDeckBar"/> surfaces it instead.</para>
        /// </summary>
        private void DeleteOpenDeck()
        {
            CharacterDecks before = _account.DecksOf(_character, _db);
            if (before.Count <= 1)
            {
                Debug.LogWarning($"[Deckbuilder] '{_character.Name}' has only this deck, and a " +
                                 "character needs one to open the deckbuilder at all. Not deleting.");
                return;
            }

            string goneId = _deck.Id, goneName = _deck.Name;
            _account.DeleteDeck(goneId);

            DeckList next = _account.DecksOf(_character, _db).Opening?.Deck;
            if (next == null)
            {
                // Unreachable via the count guard above; if the store ever disagrees, say so rather
                // than dereference null on the next repaint.
                Debug.LogError($"[Deckbuilder] Deleted '{goneName}' and {_character.Name} now has no " +
                               "deck to open. Reopen the deckbuilder.");
                return;
            }

            OpenDeck(next);
            Debug.Log($"[Deckbuilder] Deleted '{goneName}'; opened '{_deck.Name}'.");
        }

        /// <summary>Make the open deck this character's default — what the roster shows and what
        /// the queue reaches for. Writes the deck before the pointer, so the id can never name
        /// something that is not on disk yet.</summary>
        private void SetDefaultDeck()
        {
            if (_character.DefaultDeckId == _deck.Id) return;

            _account.Save(_deck);
            _character.DefaultDeckId = _deck.Id;
            _account.Save(_character);

            RebuildDeckBar();
            Debug.Log($"[Deckbuilder] '{_deck.Name}' is now {_character.Name}'s default deck.");
        }

        /// <summary>Persist the deck as-is. Never legality-gated (collection-deckbuilder-spec §1,
        /// Sam 2026-07-26): an illegal deck is a savable draft, not a blocked action — only queue
        /// eligibility (computed fresh on load, never stored) refuses it a match.</summary>
        private void SaveDeck()
        {
            _account.Save(_deck);
            Debug.Log($"[Deckbuilder] Saved '{_deck.Name}' ({_deck.TotalCount} cards) for {_character.Name}.");
        }

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the Editor).
        /// Mirrors <c>MainMenuController.Go</c> — see there for why <c>CanStreamedLevelBeLoaded</c>
        /// over <c>GetBuildIndexByScenePath</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[Deckbuilder] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        // ---- The live legality loop: single source, runs on every mutation ----
        private void Refresh()
        {
            RefreshCollectionTiles();
            _deckList.itemsSource = _deck.Entries;
            _deckList.RefreshItems();
            RebuildCurve();

            var result = DeckValidator.Validate(_deck, _character, _db);

            _countLabel.text = $"{_deck.TotalCount} / {DeckValidator.DeckSize}";

            _legalityPill.text = result.IsValid ? "Legal" : "Incomplete";
            _legalityPill.EnableInClassList("db-pill--valid", result.IsValid);
            _legalityPill.EnableInClassList("db-pill--invalid", !result.IsValid);

            _legalityBox.Clear();
            foreach (var v in result.Violations)
            {
                var msg = new Label(v.Message);
                msg.AddToClassList("legality-msg");
                _legalityBox.Add(msg);
            }
        }

        private void RebuildCurve()
        {
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
                bar.style.height = Length.Percent(100f * counts[i] / peak);
                var label = new Label(i < buckets - 1 ? i.ToString() : "7+");
                label.AddToClassList("curve-label");
                col.Add(bar); col.Add(label);
                _curve.Add(col);
            }
        }

        // ---- token helpers (class names mirror deckbuilder.uss) ----
        private static readonly string[] AllRarity = { "rarity--common", "rarity--rare", "rarity--epic", "rarity--legendary" };
        private static readonly string[] AllEdge = { "edge--neutral", "edge--emberward", "edge--tidecaller", "edge--gravekeep", "edge--verdant" };

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
            _ => "edge--neutral",
        };

        private static void SwapPrefixedClass(VisualElement e, string prefix, string keep)
        {
            var all = prefix == "rarity--" ? AllRarity : AllEdge;
            foreach (var c in all) e.EnableInClassList(c, c == keep);
        }

        /// <summary>Enum name as a player reads it: EMBERWARD → Emberward.</summary>
        private static string Pretty(CardClass c)
        {
            string raw = c.ToString();
            return raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
        }

        private static Color ClassColorToken(CardClass c) => c switch
        {
            CardClass.EMBERWARD => new Color(229 / 255f, 96 / 255f, 42 / 255f),
            CardClass.TIDECALLER => new Color(31 / 255f, 138 / 255f, 143 / 255f),
            CardClass.GRAVEKEEP => new Color(126 / 255f, 107 / 255f, 158 / 255f),
            CardClass.VERDANT => new Color(75 / 255f, 154 / 255f, 84 / 255f),
            _ => new Color(138 / 255f, 143 / 255f, 152 / 255f),
        };
    }
}
