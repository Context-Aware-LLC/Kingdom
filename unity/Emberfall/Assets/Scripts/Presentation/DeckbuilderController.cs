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
    /// standalone in the Editor). There is no character switcher yet — S6's sidebar is separate,
    /// unbuilt scope — so today this is always the one character's own deck.</para>
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
            var nameField = _root.Q<TextField>("deck-name");
            if (nameField != null)
            {
                nameField.value = _deck.Name;
                nameField.RegisterValueChangedCallback(e => _deck.Name = e.newValue);
            }
            _saveButton?.RegisterCallback<ClickEvent>(_ => SaveDeck());
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ => Go("MainMenu"));
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
