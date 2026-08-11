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
    /// this account what it owns. The wall shows the full collectible pool and the label says "the
    /// game". This is every machine today.</item>
    /// <item>A record — the wall shows only cards with at least one copy, and the label says "yours".</item>
    /// </list>
    ///
    /// <para><b>So the player-facing copy rule holds in the first state:</b> do not describe this
    /// screen as a collection of owned cards while it is showing the pool. The label is the only thing
    /// that says which of the two you are looking at, so it is not decoration — read
    /// <see cref="ApplyQuery"/> before changing its wording.</para>
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
        private Button _filterAllButton;
        private Button _filterClassButton;
        private Button _filterNeutralButton;

        /// <summary>True when this visit is editing a deck. The one predicate every mode-dependent
        /// branch below reads, so the mode is never re-derived from something correlated with it.</summary>
        private bool Deckbuilding => _character != null;

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
            WireDeckList();
            WireChrome();

            ApplyQuery();
            if (Deckbuilding) Refresh();
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
        /// record, every collectible card in the game. These two pools look identical on screen and
        /// mean completely different things, which is why the count label is load-bearing.</para>
        ///
        /// <para>A card recorded at 0 copies is NOT owned. Zero is a real recorded value — every copy
        /// dusted, or a refund — and it is exactly the case a truthiness check on "is it in the
        /// record" would get wrong, so this asks for the count.</para>
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
                else if (_owned != null && _owned.CopiesOwned(def.Id) <= 0) continue;

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
            _filterAllButton     = _root.Q<Button>("filter-all");
            _filterClassButton   = _root.Q<Button>("filter-class");
            _filterNeutralButton = _root.Q<Button>("filter-neutral");
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

        // ---- Collection filter chips (All / Class / Neutral) ----------------------------------
        // Class = either half of the character's pairing, excluding neutral; Neutral = NEUTRAL only;
        // All = no constraint. Meaningless without a pairing, so the plate hides them while browsing
        // and this returns early in that mode — two guards for one fact, deliberately: the class is
        // what the player sees and the early return is what stops us wiring a callback onto a chip
        // that can never be pressed.
        //
        // These three predate CardQueryBar and the wall does not care which of the two feeds it a
        // filter. Giving deckbuild the full query bar instead is a real improvement and a real
        // decision — the bar's class chips are generated from all nine classes and a deckbuilding
        // pool holds three of them — so it is its own change, not a tidy-up to fold in here.
        private void WireCollectionFilters()
        {
            if (!Deckbuilding || _filterAllButton == null) return;

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
            UpdateWallLabels();
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

        /// <summary>Push the query bar's facets at the wall. Runs in both modes; on a plate that
        /// declares no query controls the filter is empty, which matches everything.</summary>
        private void ApplyQuery()
        {
            _wall.SetQuery(_query.Filter, _query.Sort);
            UpdateWallLabels();
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
            // the game" — the wall is identical either way (see the class remarks). "in your
            // collection" rather than a bare count whenever the account has a record, so nobody has
            // to be told which screen they are on.
            if (_countLabel != null)
            {
                string noun = _owned != null ? "in your collection" : "cards";
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
            if (_emptyLabel != null)
            {
                _emptyLabel.text = _pool.Count == 0 && _owned != null
                    ? "You don't own any cards yet."
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
