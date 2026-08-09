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
    /// <para><b>One deck, for the whole visit.</b> Every deck-management control moved to the
    /// CharacterRoster scene (Sam, 2026-08-07) — the switcher bar, the "+", the name field, Set
    /// Default and Delete Deck. That screen is where a deck is chosen, created, renamed, given a
    /// card back, made default and destroyed. What is left here is the editing of the 30: this
    /// screen cannot change which deck it is on, and <c>Back</c> saves and returns to the roster.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// DeckbuilderScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DeckbuilderController : MonoBehaviour
    {
        /// <summary>Which character to open, set by the caller before loading this scene.</summary>
        public static string PendingCharacterId;

        /// <summary>Which saved deck to open, or empty to use that character's normal opening deck.</summary>
        public static string PendingDeckId;

        private PlayerAccount _account;
        private Character _character;
        private ICardDatabase _db;
        private DeckList _deck;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private CardFaceBaker _faceBaker;

        /// <summary>
        /// This account's ownership record (L6), or null when it has none — the full-pool alpha, which
        /// is every machine until a grant path writes one. Read once per visit: nothing opens a pack
        /// from inside the deckbuilder, so it cannot change while this screen is up.
        /// </summary>
        private IOwnedCardCounts _owned;

        // Cached elements.
        private VisualElement _root;
        private VisualElement _collectionGrid;
        private CardWall _wall;
        private ListView _deckList;
        private VisualElement _curve;
        private Label _countLabel;
        private Label _legalityPill;
        private Button _filterAllButton;
        private Button _filterClassButton;
        private Button _filterNeutralButton;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Deckbuilder] No rootVisualElement — is the UXML assigned?"); return; }

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
                _owned = _account.OwnedCards();
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

            CharacterDecks characterDecks = _account.DecksOf(_character, _db);
            _deck = FindPendingDeck(characterDecks.Decks) ?? characterDecks.Opening?.Deck;
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

        private static DeckList FindPendingDeck(IReadOnlyList<DeckSummary> decks)
        {
            string pending = PendingDeckId;
            PendingDeckId = "";
            if (string.IsNullOrEmpty(pending)) return null;
            for (int i = 0; i < decks.Count; i++)
                if (decks[i].Deck.Id == pending) return decks[i].Deck;
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
                    if (DeckValidator.CanAdd(_deck, _character, def, _owned)) { AddCopy(def); progressed = true; }
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
            _deckList       = _root.Q<ListView>("deck-list");
            _curve          = _root.Q<VisualElement>("curve-strip");
            _countLabel     = _root.Q<Label>("count-label");
            // _legalityPill   = _root.Q<Label>("legality-pill");
            _filterAllButton     = _root.Q<Button>("filter-all");
            _filterClassButton   = _root.Q<Button>("filter-class");
            _filterNeutralButton = _root.Q<Button>("filter-neutral");
        }

        // ---- Collection wall: now a shared CardWall (Sam, 2026-08-07) -------------------------
        //
        // The tile building, the bake, the per-row gutter solve and the Yoga-safe deferred layout
        // all moved to CardWall so the Collection screen renders the same wall instead of a second
        // copy. Everything this screen still owns is the part that is genuinely about a DECK: what
        // a click does, and which tiles are at their copy cap.
        private void BuildCollectionGrid()
        {
            _wall = new CardWall(_collectionGrid, _faceBaker);
            _wall.TileClicked += AddCopy;

            // The copy-cap fade — the ONLY per-tile deck-state treatment on the wall, and the
            // reason CardWall exposes a decorator rather than owning this itself: "you already hold
            // every copy" is a fact about a deck, and the Collection screen has none.
            //
            // This deliberately does NOT ask DeckValidator.CanAdd. CanAdd is false for L1 (deck at
            // 30) and L4 (off-class) too, and keying the fade on it is exactly the bug removed in
            // c31da55: a deck at 30 refuses every card, so every tile faded and the wall the screen
            // exists to show went dark at the moment the player stopped building and started
            // browsing. The fade means one thing only — "you already hold every copy of THIS card
            // you are allowed" — so it reads the copy cap directly. It can never take more than a
            // handful of tiles, and clears the moment a copy comes back out.
            //
            // MaxCopies is rarity-dependent (L3 Legendary = 1, L2 everything else = 2), so a
            // Legendary fades at one copy, not two.
            //
            // NOR-238 replaced MaxCopies with CopyLimit — min(MaxCopies, copies you own), or MaxCopies
            // alone when this account has no ownership record. The fade's ONE meaning is unchanged and
            // that is why it could absorb L6 rather than needing a treatment of its own: "you already
            // hold every copy of THIS card you are allowed" is the same sentence whether the limit came
            // from the format or from your collection. A card you own none of therefore fades at zero
            // copies in the deck, which is exactly what AddCopy will refuse.
            _wall.TileDecorator = (def, tile) =>
                tile.EnableInClassList("card-tile--maxed",
                    _deck.CountOf(def.Id) >= DeckValidator.CopyLimit(def.Rarity, _owned, def.Id));

            _wall.SetPool(_pool);
        }

        // ---- Collection filter chips (All / Class / Neutral) ----------------------------------
        // Class = either half of the character's pairing, excluding neutral; Neutral = NEUTRAL
        // only; All = no constraint. These three predate CardQueryBar and stay as they are for now
        // — replacing them with the shared bar is a separate change, and the wall does not care
        // which of the two feeds it a filter.
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
            _filterAllButton.EnableInClassList("db-chip--on", active == _filterAllButton);
            _filterClassButton.EnableInClassList("db-chip--on", active == _filterClassButton);
            _filterNeutralButton.EnableInClassList("db-chip--on", active == _filterNeutralButton);
            _wall.SetQuery(filter, CardSort.Default);
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

        // ---- Chrome (buttons) ----
        private void WireChrome()
        {
            // The only way out, and it saves. The alternative is a confirm dialog on a screen whose
            // whole model is "an illegal deck is a savable draft" — there is nothing to lose by
            // writing it. There is deliberately no second Save button beside it (Sam, 2026-08-07):
            // two controls performing one write is two things to explain and one of them redundant.
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ =>
            {
                SaveDeck();
                Go("CharacterRoster");
            });
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
            _deckList.itemsSource = _deck.Entries;
            _deckList.RefreshItems();
            _wall.RefreshTiles();
            RebuildCurve();

            _countLabel.text = $"{_deck.TotalCount} / {DeckValidator.DeckSize}";

            // _legalityPill.text = result.IsValid ? "Legal" : "Incomplete";
            // _legalityPill.EnableInClassList("db-pill--valid", result.IsValid);
            // _legalityPill.EnableInClassList("db-pill--invalid", !result.IsValid);
            // (restore the DeckValidator.Validate call above if this pill ever comes back)
        }

        // Bar height is pixels, not Length.Percent — curve-col is content-sized (no explicit
        // height, since .db-curve uses align-items: flex-end rather than stretch), so a percent
        // height has no parent size to resolve against and silently collapses to 0. This is why
        // the curve never showed anything. maxBarPx is db-curve's 56px strip minus the cost
        // label's line height + margin below each bar.
        private const float CurveMaxBarPx = 36f;

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
                bar.style.height = CurveMaxBarPx * counts[i] / peak;
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
    }
}
