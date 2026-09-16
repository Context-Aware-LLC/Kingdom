using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Collection;
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
    /// <para><see cref="PendingDeckId"/> is only a navigation convenience for an existing roster
    /// doorway. It requests that the drawer open after the Collection shell is live; it does not
    /// decide the screen's shape. It is consumed once.</para>
    ///
    /// <para><b>It shows what you own when the account knows, and the whole pool when it doesn't.</b>
    /// Ownership has three states, not two:</para>
    ///
    /// <list type="bullet">
    /// <item>No ownership record (<see cref="PlayerAccount.OwnedCards"/> returns null) — nobody has told
    /// this account what it owns. The wall shows the full collectible pool and the scope toggle stays
    /// hidden. True for a machine that has never synced (<see cref="Session.CollectionGateway"/>,
    /// landed 2026-09-01) — a stale build predating that sync will never leave this state.</item>
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
    /// <para><b>Deck editing defaults ownership scope to Owned, same as browse</b> (#353) — All is a
    /// manual widen inside the editor, not the entry state. #150's visible-but-spent dimming for
    /// unowned-but-eligible cards still applies once the player switches to All by hand. Closing the
    /// editor restores the scope the player had before opening it. The query object itself stays
    /// mounted, so search, sort and facet selections survive the round trip.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// CollectionScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CollectionController : MonoBehaviour
    {
        /// <summary>Where the persistent screen's Back control returns. Set by the caller.</summary>
        public static string ReturnScene = ScreenNav.MainMenu;

        /// <summary>Which saved deck to open, or empty to browse. Consumed on read.
        ///
        /// <para>A <c>PendingCharacterId</c> sat beside this until ADR-0009 and was the one that
        /// mattered: the deckbuilder reached a decklist only THROUGH a character, so this field was
        /// the optional half. A deck is reachable on its own now.</para></summary>
        public static string PendingDeckId;

        private PlayerAccount _account;
        private ICardDatabase _db;
        private CardFaceBaker _faceBaker;
        private IUiSpriteProvider _uiSprites;
        [SerializeField] private VisualTreeAsset cardRowTemplate;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();

        /// <summary>The optional deck-edit context. Null means the drawer is browsing deck choices.
        ///
        /// <para>A <c>_character</c> sat beside this until ADR-0009 and supplied the class pairing
        /// every pool question below is asked against. The deck supplies it now, so the pair of
        /// fields that could disagree is one field.</para></summary>
        private DeckList _deck;

        /// <summary>
        /// This account's ownership record, or null when it has none — the full-pool alpha, which is
        /// every machine until a grant path writes one. Read once per visit: nothing opens a pack from
        /// inside this screen, so it cannot change while it is up.
        /// </summary>
        private IOwnedCardCounts _owned;

        /// <summary>Which pool the browse wall is showing, and the control that says so.
        /// See <see cref="CollectionScopeToggle"/> — it owns the scope, the two chips, and the
        /// three-state ownership rule.</summary>
        private readonly CollectionScopeToggle _scope = new CollectionScopeToggle();

        /// <summary>The class filter — the nine-tab strip and the pairing chips, which are one axis
        /// with two affordances. See <see cref="CollectionClassAxis"/>.</summary>
        private readonly CollectionClassAxis _classAxis = new CollectionClassAxis();

        // Cached elements. The collection shell is permanent; only the drawer's chooser/editor panes
        // trade visibility.
        private VisualElement _root;
        private VisualElement _plate;
        private ListView _collectionList;
        private CardWall _wall;
        private CardQueryBar _query;
        private Label _emptyLabel;
        private ListView _deckList;
        private VisualElement _curve;
        private Label _deckCountLabel;
        private VisualElement _queryScrim;
        private VisualElement _queryDrawer;
        private Button _queryToggleButton;
        private VisualElement _drawer;
        private VisualElement _deckDrawerScrim;
        // The FRAME, not the ScrollView. The chooser's recessed plate and its bottom scroll
        // fade live on the frame, and hiding only the ScrollView would leave an empty plate
        // beside the editor with both claiming flex-grow: 1.
        private VisualElement _deckChooserFrame;
        private VisualElement _deckChooserList;
        private Label _deckChooserEmpty;
        private VisualElement _deckEditor;
        private Label _drawerTitle;
        private Label _drawerSubtitle;
        private Button _manageDecksButton;
        private Button _backToDecksButton;
        private Button _decksButton;
        private Label _deckCharacterName;
        private Label _deckName;
        private bool _queryDrawerOpen;
        private bool _drawerOpen;
        private bool _scopeBeforeEditing;

        private bool EditingDeck => _deck != null;

        /// <summary>The crafting modal. Declared on the same plate in both modes; see <see cref="BuildCraftModal"/>.</summary>
        private CraftModal _craftModal;

        /// <summary>True when the wall is scoped to what this account owns. Delegates to the scope
        /// control so the pool and the chips can never disagree about which pool is on screen.</summary>
        private bool OwnedOnly => _scope.OwnedOnly;

        /// <summary>Seconds before the loading plate lifts whether or not the wall reports itself
        /// done. Generous — it is a deadlock guard, not a pacing knob, and lifting early on a slow
        /// device would show the exact fill-in the plate exists to hide.</summary>
        private const float LoadingPlateTimeout = 20f;

        private LoadingOverlay _loading;
        private float _loadingSince = -1f;

        /// <summary>
        /// The collection's loading art, rotated one step per visit: entering the screen twice in
        /// a row shows two different plates (Sam, 2026-09-01). Each row is a plate and the orb
        /// authored to sit on it, so the two always travel together.
        ///
        /// <para><b>Not keyed to the character, deliberately.</b> An earlier pass picked by class
        /// pairing, which reads well and is wrong for what this art is: a player who mains one
        /// character would then see one plate forever, and the art would be least visible for the
        /// people who use the screen most.</para>
        ///
        /// <para>Adding a plate is a row here plus two .png files under
        /// <c>Assets/Art/Backgrounds/Loading/</c> and <c>Assets/Art/Orbs/</c> — no code path
        /// changes, and the folder decides the import settings. An empty roster falls back to the
        /// brand plate rather than showing nothing.</para>
        /// </summary>
        private static readonly (string Plate, string Orb)[] PlateRotation =
        {
            ("LoadingPlate_Master",             "Orb_StormGlass"),
            ("LoadingPlate_TempestStonewarden", "Orb_Stormstone"),
            ("LoadingPlate_VerdantGravekeep",   "Orb_Lifeblight"),
            ("LoadingPlate_NightveilClockwright", "Orb_Shadowgear"),
        };

        /// <summary>Where the rotation is up to. STATIC, so it survives the screen being closed and
        /// reopened — a per-instance field would reset on every visit and pin the rotation to its
        /// first entry forever, which is the exact opposite of the intent. Resets on app restart,
        /// which is fine: the point is that consecutive visits differ, not that the sequence is
        /// remembered across launches.</summary>
        private static int _plateRotation;

        private void OnEnable()
        {
            _craftCancel = new CancellationTokenSource();
            _craftBusy = false;
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Collection] No rootVisualElement — is the UXML assigned?"); return; }

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
                _uiSprites = CardBackCatalog.NewProvider();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Collection] could not open this device's collection: {ex.Message}");
                return;
            }

            // OnEnable can run again on the same controller after deck editing parked browse on All.
            // Every fresh collection entry starts from the player's cards; All remains opt-in.
            _scope.SetSilently(true);
            LoadOwnership();
            // Before the first BuildPool, which asks OwnedOnly — see SetOwnershipRecord.
            _scope.SetOwnershipRecord(_owned != null);

            _collectionList = _root.Q<ListView>("collection-list");
            if (_collectionList == null)
            {
                Debug.LogError("[Collection] 'collection-list' not found in the UXML. If the whole " +
                               "screen is empty, check the UXML for a double dash inside a comment " +
                               "— that fails the import silently.");
                return;
            }
            if (cardRowTemplate == null)
            {
                Debug.LogError("[Collection] CardRowTemplate.uxml is not assigned on CollectionController.");
                return;
            }

            CacheElements();
            BuildPool();
            InitFaceBaker();
            BuildCraftModal();   // before the wall: a tile click can open it, so it must exist first
            ShowLoadingPlate();  // before the wall: SetPool can complete a warm bake pass inline
            BuildWall();
            WireQueryBar();
            WireClassAxis();
            WireScopeToggle();
            WireDeckList();
            WireChrome();

            ApplyQuery();
            WireCollectionScroll();
            OpenPendingDeckIfAny();
        }

        /// <summary>
        /// Consume the roster handoff after the Collection shell is fully live. A broken id leaves
        /// the player on the truthful collection wall rather than producing a half-open drawer.
        ///
        /// <para>It took a character id AND a deck id until ADR-0009, and resolved the character
        /// first because a deck could only be reached through one. The deck id is the whole handoff
        /// now, and a deck that does not resolve is the only failure left — there is no second
        /// lookup that can succeed while the first one fails.</para>
        /// </summary>
        private void OpenPendingDeckIfAny()
        {
            string pendingDeck = PendingDeckId;
            PendingDeckId = "";
            if (string.IsNullOrEmpty(pendingDeck)) return;

            DeckList deck = FindDeck(_account.DeckRows(_db), pendingDeck);
            if (deck == null)
            {
                Debug.LogWarning($"[Collection] no deck '{pendingDeck}' on this account; " +
                                 "opening the deck chooser instead.");
                OpenDeckDrawer();
                return;
            }

            OpenDeckDrawer();
            OpenDeckEditor(deck);
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
        /// <para><b>Editing</b> — the character's pairing plus neutral, scoped to Owned by default
        /// (#353) same as browse. If the player widens to All, an unowned card stays on the wall and
        /// <see cref="AddCopy"/> refuses it at the copy cap, because the copy limit is already
        /// <c>min(MaxCopies, copies owned)</c>. Removing it from the pool instead would make a card
        /// you nearly own invisible, and #150 wants it dimmed.</para>
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
            var available = new HashSet<CardClass>();
            foreach (var def in _db.AllCollectible())
            {
                if (EditingDeck && !_deck.AllowsClass(def.Class)) continue;
                if (OwnedOnly && _owned.CopiesOwned(def.Id) <= 0) continue;

                _pool.Add(def);
                available.Add(def.Class);
            }
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));

            // The strip is a read of the pool, so it is re-derived at the site that decides the pool
            // and nowhere else — every path that narrows what the player is looking at (scope, opening
            // a deck, closing one) already comes through here.
            _classAxis.SetAvailable(available);
        }

        private void CacheElements()
        {
            _plate           = _root.Q<VisualElement>("root");
            _emptyLabel      = _root.Q<Label>("collection-empty");
            _deckList        = _root.Q<ListView>("deck-list");
            _curve           = _root.Q<VisualElement>("curve-strip");
            _deckCountLabel  = _root.Q<Label>("count-label");
            _queryScrim          = _root.Q<VisualElement>("query-scrim");
            _queryDrawer         = _root.Q<VisualElement>("query-bar");
            _queryToggleButton   = _root.Q<Button>("query-toggle");
            _drawer              = _root.Q<VisualElement>("deck-drawer");
            _deckDrawerScrim     = _root.Q<VisualElement>("deck-drawer-scrim");
            _deckChooserFrame    = _root.Q<VisualElement>("deck-chooser-frame");
            _deckChooserList     = _root.Q<VisualElement>("deck-chooser-list");
            _deckChooserEmpty    = _root.Q<Label>("deck-chooser-empty");
            _deckEditor          = _root.Q<VisualElement>("deck-editor");
            _drawerTitle         = _root.Q<Label>("deck-drawer-title");
            _drawerSubtitle      = _root.Q<Label>("deck-drawer-subtitle");
            _deckCharacterName   = _root.Q<Label>("deck-character-name");
            _deckName            = _root.Q<Label>("deck-name");
            _manageDecksButton   = _root.Q<Button>("manage-decks-button");
            _backToDecksButton   = _root.Q<Button>("choose-another-deck");
            _decksButton         = _root.Q<Button>("decks-button");
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

        // Row batching, ListView recycling, baking and responsive tile geometry all live in CardWall.
        // What stays here is genuinely about a deck: what a tap does and which tiles are at their cap.
        private void BuildWall()
        {
            _wall = new CardWall(_collectionList, cardRowTemplate, _faceBaker);

            // Subscribed BEFORE SetPool, which is what starts the first bake pass. A cache-warm
            // wall can complete that pass in the same frame it begins it.
            _wall.BakePassCompleted += OnFirstBakeCompleted;

            // Both modes now, where browse used to have no handler at all: "a tap does nothing until
            // there is something for it to do" was true right up until crafting gave it something.
            _wall.TileClicked += OnTileClicked;

            _wall.TileDecorator = DecorateTile;
            _wall.SetPool(_pool);
        }

        /// <summary>
        /// Hold the loading plate over the screen until the wall has actually painted.
        ///
        /// <para><b>Why this screen earns an overlay when the others do not.</b> The bake queue is
        /// strictly serial and every card blocks on its own art load, so the whole pool arrives over
        /// seconds rather than frames. Without the plate the player watches several hundred empty
        /// rectangles fill in one at a time, which reads as a broken screen rather than a loading
        /// one.</para>
        ///
        /// <para>Created here rather than in <c>Awake</c> so the early returns above — no root, no
        /// account, no card database — leave the screen visible and its error on the console. An
        /// overlay that outlives a failed load is a hang with no message.</para>
        /// </summary>
        private void ShowLoadingPlate()
        {
            (string plate, string orb) = NextPlate();
            _loading = LoadingOverlay.Create(Camera.main, plate, orb);
            if (_loading == null) return;   // no camera: the wall just paints in as it bakes
            _loading.OnFadedOut += RevealScreen;
            _loadingSince = Time.realtimeSinceStartup;

            // THE PLATE IS BEHIND THIS UI, ALWAYS. LoadingOverlay is world-space geometry parented
            // to the camera, and this screen's panel is ScreenSpaceOverlay — which draws on top of
            // whatever the camera rendered, unconditionally. So the plate cannot cover the screen;
            // the screen has to stop painting instead. Measured 2026-09-01: with the panel's
            // clearColor still on, the plate was ERASED before the UI drew and never appeared at
            // all; with it off, the plate appears only for the frames after the UIDocument disables
            // on the way OUT of the screen, which is exactly what "it works when I leave" looks
            // like. Both had to change, and neither alone does anything visible.
            //
            // OPACITY, NOT VISIBILITY OR DISPLAY. `display: none` takes the subtree out of layout,
            // and the wall solves its tile size from the live scroll viewport — a viewport that
            // resolves to NaN while hidden would feed the solve and bake the whole pool at the
            // wrong size, or not at all. `visibility: hidden` keeps layout, but it is inherited:
            // CardWall stamps Visibility.Visible on every populated tile, so each card opts itself
            // back into painting over the world-space plate. Parent opacity is composited across
            // the whole subtree and descendants cannot override it. Disable the tree as well so
            // the transparent controls cannot receive input while the wall bakes underneath.
            _root.SetEnabled(false);
            _root.style.opacity = 0f;
        }

        /// <summary>Paint this screen again. Idempotent, and safe when the plate was never shown —
        /// every exit from the loading state routes through here rather than setting the style at
        /// each call site, so a new exit path cannot forget it and leave the screen invisible.</summary>
        private void RevealScreen()
        {
            if (_root == null) return;
            _root.style.opacity = 1f;
            _root.SetEnabled(true);
        }

        /// <summary>
        /// The next plate in the rotation, advanced as a side effect — one step per call, and this
        /// is called exactly once per visit from <see cref="ShowLoadingPlate"/>. Keep it that way:
        /// a second caller would double-step and turn "different every time" into "every other one
        /// every time".
        /// </summary>
        private static (string Plate, string Orb) NextPlate()
        {
            if (PlateRotation.Length == 0) return (LoadingOverlay.DefaultPlate, null);
            var row = PlateRotation[_plateRotation % PlateRotation.Length];
            _plateRotation = (_plateRotation + 1) % PlateRotation.Length;
            return row;
        }

        private void OnFirstBakeCompleted()
        {
            if (_wall != null) _wall.BakePassCompleted -= OnFirstBakeCompleted;
            // The wall is ready underneath, but Screen Space Overlay UI paints above this
            // world-space plate. Keep it hidden until OnFadedOut or the first rows cover the fade.
            _loading?.NotifyReady();
        }

        /// <summary>
        /// The plate covers the whole screen, so anything that can stop
        /// <see cref="CardWall.BakePassCompleted"/> arriving is a hard lock with no message and no
        /// way out. A bake that throws is already counted, but a baker that never gets a rig — no
        /// board prefab, a stripped shader — delivers nothing at all. Lift on time regardless and
        /// say why: a wall filling in visibly is a worse screen, not a broken one.
        /// </summary>
        private void Update()
        {
            if (_loading == null || _loadingSince < 0f) return;
            if (Time.realtimeSinceStartup - _loadingSince < LoadingPlateTimeout) return;
            _loadingSince = -1f;
            Debug.LogWarning($"[Collection] wall did not finish baking within {LoadingPlateTimeout:F0}s — "
                             + "lifting the loading plate anyway; tiles will fill in as they bake.");
            _loading.NotifyReady();
        }

        private void OnDisable()
        {
            _craftVersion++;
            _craftCancel?.Cancel();
            _craftCancel?.Dispose();
            _craftCancel = null;
            if (_wall != null) _wall.BakePassCompleted -= OnFirstBakeCompleted;
            // Parented to the camera, so leaving the scene takes it. Leaving only the SCREEN —
            // the document disabled under a live camera — would otherwise strand it on top.
            if (_loading != null)
            {
                _loading.OnFadedOut -= RevealScreen;
                Destroy(_loading.gameObject);
            }
            _loading = null;
            _loadingSince = -1f;
            // The document is reused across visits, so an invisible root would persist into the
            // next one — and the next visit's plate cannot be what reveals it, because a visit with
            // no camera never creates one.
            RevealScreen();
        }

        /// <summary>
        /// What a tap on a wall tile means, which differs by mode because the modes have different
        /// primary verbs.
        ///
        /// <para><b>Browse</b> — crafting. Nothing competes for the gesture: there is no deck to add
        /// to, and in-match inspect was cut (board layout rulings, 2026-07-23).</para>
        ///
        /// <para><b>Deckbuild</b> — adding a copy, which stays the primary verb and must not be taken
        /// away from the tap. It falls through to crafting in exactly one case: the account owns NO
        /// copy of the card. That is the case where <see cref="AddCopy"/> is a guaranteed silent
        /// no-op (L6 puts the copy limit at 0), so the tap is otherwise dead, and it is the case where
        /// what the player wants is for a copy to exist at all. Every other refusal — deck full,
        /// off-class, already holding every copy — leaves the tap doing nothing, as before.</para>
        ///
        /// <para>A card you own SOME but not all of is therefore reachable only from browse today.
        /// Crafting a second copy from inside the deckbuilder wants a gesture of its own — a hold, or
        /// a right-click — and that is deliberately not invented here: this modal is groundwork and a
        /// new input gesture is a real decision with a verify check of its own
        /// (<c>InputGestureVerify</c>).</para>
        ///
        /// <para><b>RETIREMENT TRIGGER — drag-to-add/remove in the deckbuilder (Sam, 2026-08-12,
        /// "pretty soon").</b> Drag takes over add and remove, which frees the tap completely. When
        /// that lands, DELETE the owns-zero fall-through below and make the tap mean crafting in both
        /// modes unconditionally: the special case exists only because add currently owns the gesture,
        /// and once it does not, the case is not a simplification to notice later — it is dead weight
        /// that makes the deckbuilder's tap mean two different things for no reason. The paragraph
        /// above about "a gesture of its own" retires with it.</para>
        /// </summary>
        private void OnTileClicked(CardDefinition def)
        {
            if (!EditingDeck)
            {
                OpenCraft(def);
                return;
            }

            if (_owned != null && _owned.CopiesOwned(def.Id) <= 0)
            {
                OpenCraft(def);
                return;
            }

            AddCopy(def);
        }

        /// <summary>
        /// The per-tile treatments. Two of them, and keeping them apart is the whole point — see
        /// <see cref="BuildWall"/> for why there is a decorator at all.
        ///
        /// <para><b>Neither one asks <see cref="DeckValidator.CanAdd"/>.</b> CanAdd is false for L1
        /// (deck at 30) and L4 (off-class) too, and keying a fade on it is exactly the bug removed in
        /// c31da55: a deck at 30 refuses every card, so every tile faded and the wall the screen exists
        /// to show went dark at the moment the player stopped building and started browsing. Every
        /// treatment here reads a per-CARD fact, never the wall's state — which is the line the
        /// no-dimming ruling actually draws (Sam, 2026-08-07).</para>
        /// </summary>
        private void DecorateTile(CardDefinition def, VisualElement tile)
        {
            // "You own none of this." A per-card fact, true in BOTH modes, and the one thing that makes
            // a card you might want to craft findable at all.
            //
            // Until 2026-08-12 this had no treatment of its own and was absorbed by the copy-cap fade
            // below, on the reasoning that "you hold every copy you are allowed" is the same sentence
            // whether the limit came from the format or from your collection. It is the same sentence
            // and it was still wrong, because the two want opposite pixels: a spent card is finished
            // with and fades to nearly nothing, while an unowned card is the one you are shopping for.
            // Owning none put CopyLimit at 0, `0 >= 0` was true on arrival, and every unowned card in
            // the deckbuilder sat at the spent fade's 0.1 from the moment the screen opened — present
            // in the pool, invisible on the glass, and the reason crafting had nothing to click.
            bool unowned = _owned != null && _owned.CopiesOwned(def.Id) <= 0;
            tile.EnableInClassList("card-tile--unowned", unowned);

            // The copy-cap fade — "you already hold every copy of THIS card you are allowed", so it
            // reads the copy limit directly. Deckbuild only: it is a fact about a DECK, and browse mode
            // has none (_deck is null there, which the mode check below is what guards).
            //
            // CopyLimit is min(MaxCopies, copies owned) (NOR-238), and MaxCopies is rarity-dependent
            // (L3 Legendary = 1, L2 everything else = 2), so a Legendary fades at one copy, not two.
            //
            // The `!unowned` term is what keeps the two treatments from stacking. Without it an unowned
            // card is trivially "at its limit" and takes both classes, and the 0.1 wins — which is the
            // exact bug described above. An unowned card is never ALSO spent: there was nothing to
            // spend.
            tile.EnableInClassList("card-tile--maxed",
                EditingDeck && !unowned &&
                _deck.CountOf(def.Id) >= DeckValidator.CopyLimit(def.Rarity, _owned, def.Id));
        }

        private void WireQueryBar()
        {
            _query = new CardQueryBar(_root);
            _query.Changed += ApplyQuery;

            // The class axis lives here rather than in the bar, so Reset has to clear it explicitly
            // — the bar's own Reset cannot reach a facet it does not hold, and a Reset that leaves a
            // class ticked is worse than no Reset. Cleared before _query.Reset(), whose Changed fires
            // the single ApplyQuery that writes both halves at once.
            var reset = _root.Q<Button>("query-reset");
            if (reset != null)
                reset.clicked += () =>
                {
                    _classAxis.Clear();
                    _query.Reset();
                };
        }

        /// <summary>Bind the class strip and the pairing chips — one axis, two affordances. See
        /// <see cref="CollectionClassAxis"/>, which owns the set and both control surfaces.</summary>
        private void WireClassAxis()
        {
            _classAxis.Changed += ApplyQuery;
            _classAxis.BindChips(_root.Q<Button>("filter-primary"),
                                 _root.Q<Button>("filter-secondary"),
                                 _root.Q<Button>("filter-neutral"));
            _classAxis.BindStrip(_root);
        }

        /// <summary>Bind the Owned/All scope. Switching pools REBUILDS rather than re-queries —
        /// see <see cref="CollectionScopeToggle"/> for why ownership is not a card facet.
        ///
        /// <para><c>CardWall.SetPool</c> scrolls the wall back to the top, which is the behaviour you
        /// want here: the set under the cursor is not the set that was there.</para></summary>
        private void WireScopeToggle()
        {
            _scope.Changed += () =>
            {
                BuildPool();
                _wall.SetPool(_pool);     // keeps the wall's current filter and sort
                UpdateWallLabels();
            };
            _scope.Bind(_root.Q<VisualElement>("scope-row"),
                        _root.Q<Button>("scope-owned"),
                        _root.Q<Button>("scope-all"));
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
            if (_decksButton != null) _decksButton.style.display = DisplayStyle.None;
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
            _deckDrawerScrim?.RemoveFromClassList("deck-drawer-scrim--open");
            if (_decksButton != null) _decksButton.style.display = DisplayStyle.Flex;
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
            // Editing uses the wall beside the drawer. A full-screen dismiss layer here
            // steals both card clicks and wheel events before they can reach the wall.
            _deckDrawerScrim?.EnableInClassList("deck-drawer-scrim--open", !editor);
            _deckChooserFrame?.EnableInClassList("deck-chooser--hidden", editor);
            _deckEditor?.EnableInClassList("deck-editor--hidden", !editor);
            // "Manage Decks" leaves the collection entirely, so it belongs to the
            // CHOOSER pane, not to editing one deck's thirty. Editing already has two ways out
            // ("<- All decks" and Close), and on the 411-unit landscape panel this was authored
            // against (pre-#349) the button plus its margin was 68 -- most of the height the deck
            // list itself was missing. The panel is 1080 now and that pressure is off; the button
            // still belongs to the chooser, which is the reason that did not depend on the unit.
            if (_manageDecksButton != null)
                _manageDecksButton.style.display = editor ? DisplayStyle.None : DisplayStyle.Flex;
            // Moved into the header next to Close (Sam, 2026-09-07) so both exits sit together
            // instead of one hiding at the top of the editor pane and one at the bottom of the
            // chooser. It has no home in the chooser pane -- there is no open deck to leave.
            if (_backToDecksButton != null)
                _backToDecksButton.style.display = editor ? DisplayStyle.Flex : DisplayStyle.None;
            if (_drawerTitle != null) _drawerTitle.text = editor ? "Editing Deck" : "Decks";
            if (_drawerSubtitle != null)
                _drawerSubtitle.text = editor ? "Tap a card to add it" : "Choose a deck to edit";
        }

        private void RebuildDeckChooser()
        {
            if (_deckChooserList == null) return;
            _deckChooserList.Clear();

            // ONE FLAT LIST (ADR-0009). Each deck's ordered pair is now the button image itself;
            // names, counts and class labels deliberately stay out of the visible chooser row.
            IReadOnlyList<DeckSummary> rows = _account.DeckRows(_db);

            foreach (DeckSummary summary in rows)
            {
                DeckList capturedDeck = summary.Deck;

                var group = new VisualElement();
                group.AddToClassList("deck-choice-group");

                var button = new Button(() => OpenDeckEditor(capturedDeck));
                button.AddToClassList("deck-choice");
                button.AddToClassList(CardClassTokens.Edge(capturedDeck.PrimaryClass));
                DeckClassPortrait.Mount(button, _uiSprites,
                    capturedDeck.PrimaryClass, capturedDeck.SecondaryClass,
                    "deck-class-portrait--drawer");
                button.tooltip =
                    $"{capturedDeck.Name} · " +
                    $"{CardClassTokens.Label(capturedDeck.PrimaryClass)} → " +
                    $"{CardClassTokens.Label(capturedDeck.SecondaryClass)} · " +
                    $"{summary.TotalCount}/{DeckValidator.DeckSize}";
                group.Add(button);

                _deckChooserList.Add(group);
            }

            if (_deckChooserEmpty != null)
            {
                _deckChooserEmpty.text = rows.Count == 0
                    ? "No decks yet — manage your decks to create one."
                    : "";
                _deckChooserEmpty.EnableInClassList("deck-chooser__empty--on", rows.Count == 0);
            }
        }

        private void OpenDeckEditor(DeckList deck)
        {
            if (deck == null) return;
            if (EditingDeck) SaveDeck();

            _scopeBeforeEditing = _scope.RawScope;
            // #353: the editor defaults to Owned, same as browse — All stays a manual widen, not the
            // entry state. #150's visible-but-spent dimming for unowned cards still applies once the
            // player switches to All. Silent because this method rebuilds the pool itself below.
            _scope.SetSilently(true);

            _deck = deck;
            // Parks the browse class selection and adopts the deck's pairing — see
            // CollectionClassAxis.BeginEditing for why parking rather than dropping.
            _classAxis.BeginEditing(deck.PrimaryClass, deck.SecondaryClass);

            // The character's name led this line until ADR-0009. What is left is the pairing, which
            // is what the line was actually FOR — it tells the player which pool the wall beside it
            // is showing.
            if (_deckCharacterName != null)
                _deckCharacterName.text = $"{CardClassTokens.Label(deck.PrimaryClass)} + " +
                                          CardClassTokens.Label(deck.SecondaryClass);
            if (_deckName != null) _deckName.text = deck.Name;

            StampEditing();
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

            _deck = null;
            _classAxis.EndEditing();          // restores the browse selection parked on entry
            _scope.SetSilently(_scopeBeforeEditing);
            StampEditing();

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

        private void ManageDecks()
        {
            if (EditingDeck) SaveDeck();
            CharacterRosterController.ReturnScene = ScreenNav.Collection;
            ScreenNav.Go(ScreenNav.CharacterRoster);
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
            // set from UXML like a standalone ScrollView's.
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

            CardClassTokens.SwapPrefixed(row, CardClassTokens.AllEdge(), CardClassTokens.Edge(def.Class));
        }

        // ---- Crafting (GH#157 Render / GH#158 Reduce) ----

        private CancellationTokenSource _craftCancel;
        private int _craftVersion;
        private bool _craftBusy;

        private async void OpenCraft(CardDefinition def)
        {
            if (_craftModal == null || !_craftModal.IsBound || def == null || _craftBusy) return;
            int version = ++_craftVersion;
            CancellationToken ct = _craftCancel.Token;
            _craftModal.Open(def, Crafting.OfferRender(def, 0, 0), Crafting.OfferReduce(def, 0), 0);
            _craftModal.SetWorking("Loading...", unknown: true);
            try
            {
                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!CraftIsCurrent(def, version, ct)) return;
                CraftingReply reply = await CraftingGateway.ReadAsync(def.Id, accessToken, ct);
                if (!CraftIsCurrent(def, version, ct)) return;
                if (!reply.HasSnapshot)
                {
                    _craftModal.ShowError(CraftError(reply.Status));
                    return;
                }
                bool synced = await RefreshCraftCollection(accessToken, ct);
                if (!CraftIsCurrent(def, version, ct)) return;
                ShowCraft(def, reply);
                if (!synced) _craftModal.ShowError("Collection refresh failed.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Craft] read failed: {ex.GetType().Name}");
                if (CraftIsCurrent(def, version, ct)) _craftModal.ShowError("Could not load crafting balances.");
            }
        }

        private bool CraftIsCurrent(CardDefinition def, int version, CancellationToken ct)
            => this != null && isActiveAndEnabled && !ct.IsCancellationRequested &&
               version == _craftVersion && ReferenceEquals(_account, PlayerAccount.Local) &&
               _craftModal.Card == def;

        private void ShowCraft(CardDefinition def, CraftingReply reply)
        {
            _craftModal.Open(def, Crafting.OfferRender(def, reply.CopiesOwned, reply.Shards),
                Crafting.OfferReduce(def, reply.CopiesOwned), reply.Shards);
        }

        private async Task<bool> RefreshCraftCollection(string accessToken, CancellationToken ct)
        {
            CollectionSyncStatus status = await CollectionGateway.SyncAsync(accessToken, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_account, PlayerAccount.Local)) return false;
            if (status != CollectionSyncStatus.Synced && status != CollectionSyncStatus.Unconstrained) return false;
            LoadOwnership();
            _scope.SetOwnershipRecord(_owned != null);
            BuildPool();
            _wall.SetPool(_pool);
            ApplyQuery();
            Refresh();
            return true;
        }

        private async void CommitCraft(CardDefinition def, bool render)
        {
            if (_craftBusy || def == null) return;
            _craftBusy = true;
            int version = _craftVersion;
            CancellationToken ct = _craftCancel.Token;
            try
            {
                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!CraftIsCurrent(def, version, ct)) return;
                CraftingReply reply = render
                    ? await CraftingGateway.RenderAsync(def.Id, accessToken, ct)
                    : await CraftingGateway.ReduceAsync(def.Id, accessToken, ct);
                if (!CraftIsCurrent(def, version, ct)) return;
                bool synced = await RefreshCraftCollection(accessToken, ct);
                if (!CraftIsCurrent(def, version, ct)) return;
                if (reply.Applied && synced)
                {
                    await _craftModal.PlayConfirmedCraftAsync(render, ct);
                    if (!CraftIsCurrent(def, version, ct)) return;
                    ShowCraft(def, reply);
                    _craftModal.Close();
                    return;
                }
                if (reply.HasSnapshot) ShowCraft(def, reply);
                // A read can reconcile an ambiguous write; repeating that write could spend twice.
                if (!reply.HasSnapshot)
                {
                    CraftingReply fresh = await CraftingGateway.ReadAsync(def.Id, accessToken, ct);
                    if (!CraftIsCurrent(def, version, ct)) return;
                    if (fresh.HasSnapshot) ShowCraft(def, fresh);
                }
                _craftModal.ShowError(reply.Applied ? "Craft complete. Collection refresh failed." : CraftError(reply.Status));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Craft] request failed: {ex.GetType().Name}");
                if (CraftIsCurrent(def, version, ct)) _craftModal.ShowError("Craft result is unconfirmed.");
            }
            finally
            {
                if (version == _craftVersion) _craftBusy = false;
            }
        }

        private static string CraftError(string status)
        {
            switch (status)
            {
                case "unauthenticated": return "Sign in to craft cards.";
                case "unavailable": return "Crafting is unavailable on this server.";
                case "insufficientshards": return "Not enough shards.";
                case "atcopycap": return "Crafting limit reached for this card.";
                case "ownsnone": return "No copies available to reduce.";
                case "invalidcard":
                case "notcraftable":
                case "notreducible": return "This card cannot be crafted.";
                default: return "Could not confirm the crafting result.";
            }
        }

        private void BuildCraftModal()
        {
            _craftModal = new CraftModal(_root, _faceBaker);

            if (!_craftModal.IsBound)
            {
                Debug.LogError("[Collection] the crafting modal is missing elements on the plate, so " +
                               "tapping a card will do nothing. Check CollectionScreen.uxml for the " +
                               "craft block — and for a double dash inside a comment, which fails the " +
                               "whole import silently.");
                return;
            }

            _craftModal.RenderRequested += def => CommitCraft(def, render: true);
            _craftModal.ReduceRequested += def => CommitCraft(def, render: false);
            _craftModal.RefreshRequested += OpenCraft;
        }

        // ---- Mutations ----
        private void AddCopy(CardDefinition def)
        {
            if (!EditingDeck) return;
            // L1 ceiling / L2/L3 cap / L4 off-class / L6 you don't own another copy.
            if (!DeckValidator.CanAdd(_deck, def, _owned)) return;
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
        // The DEVELOPMENT_BUILD block logs the data the issue asks for: scroll offset, list content
        // height, and layout call count sampled every 500ms. On a loop-free build the layout count
        // should only tick up on resize or filter changes, not while idle or during a scroll gesture.
        private void WireCollectionScroll()
        {
            _root.RegisterCallback<NavigationCancelEvent>(_ => HandleBack());

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var scroll = _collectionList?.Q<ScrollView>();
            if (scroll != null && _wall != null)
            {
                scroll.schedule.Execute(() =>
                {
                    Debug.Log($"[NOR-360] offset={scroll.scrollOffset.y:F1} " +
                              $"content.h={scroll.contentContainer.resolvedStyle.height:F1} " +
                              $"layouts={_wall.LayoutCount}");
                }).Every(500);
            }
#endif
        }

        // ---- Chrome (buttons) ----
        //
        // One persistent Back: dismiss the top query drawer, then the deck drawer, then leave Collection.
        private void WireChrome()
        {
            SetQueryDrawerOpen(false);
            _queryToggleButton?.RegisterCallback<ClickEvent>(_ =>
                SetQueryDrawerOpen(!_queryDrawerOpen));
            _queryScrim?.RegisterCallback<ClickEvent>(_ => SetQueryDrawerOpen(false));
            _root.Q<Button>("decks-button")?.RegisterCallback<ClickEvent>(_ => OpenDeckDrawer());
            _root.Q<Button>("back-button")?.RegisterCallback<ClickEvent>(_ => HandleBack());
            // No more "Close" button in the drawer header (Sam, 2026-09-07) -- an outside tap does it.
            _deckDrawerScrim?.RegisterCallback<ClickEvent>(_ => CloseDeckDrawer());
            _backToDecksButton?.RegisterCallback<ClickEvent>(_ => ShowDeckChooser());
            _root.Q<Button>("manage-decks-button")?.RegisterCallback<ClickEvent>(_ => ManageDecks());
        }

        private void HandleBack()
        {
            if (_queryDrawerOpen)
            {
                SetQueryDrawerOpen(false);
                return;
            }

            if (_drawerOpen)
            {
                CloseDeckDrawer();
                return;
            }
            Exit();
        }

        private void SetQueryDrawerOpen(bool open)
        {
            if (_queryDrawer == null) return;

            _queryDrawerOpen = open;
            _queryDrawer.EnableInClassList("col-query--open", open);
            _queryDrawer.pickingMode = open ? PickingMode.Position : PickingMode.Ignore;
            _queryDrawer.SetEnabled(open);
            _queryScrim?.EnableInClassList("col-query-scrim--open", open);
            if (_queryScrim != null)
                _queryScrim.pickingMode = open ? PickingMode.Position : PickingMode.Ignore;
            _queryToggleButton?.EnableInClassList("col-button--filters-on", open);
        }

        private void Exit()
        {
            if (EditingDeck) SaveDeck();
            ScreenNav.Go(ReturnScene);
        }

        /// <summary>Persist the deck as-is. Never legality-gated (collection-deckbuilder-spec §1,
        /// Sam 2026-07-26): an illegal deck is a savable draft, not a blocked action — only queue
        /// eligibility (computed fresh on load, never stored) refuses it a match.</summary>
        private void SaveDeck()
        {
            if (!EditingDeck) return;
            _account.Save(_deck);
            Debug.Log($"[Collection] Saved '{_deck.Name}' ({_deck.TotalCount} cards).");
        }

        /// <summary>
        /// The one site that writes the wall query. Composes the two filter surfaces — the query bar
        /// and (while editing) the pairing chips — into a single <see cref="CardFilter"/>, so neither
        /// clobbers the other the way two separate <c>SetQuery</c> writers did before #160.
        ///
        /// <para>The class axis is one set with two controls on it — the left strip and, while
        /// editing, the pairing chips — so there is nothing to suppress or reconcile here; it applies
        /// the same way in both states. The browse selection is parked across an edit and restored on
        /// close, the same shape as the ownership scope.</para>
        ///
        /// <para>Runs with or without an edit context; on a plate that declares no query controls the filter is empty,
        /// which matches everything. The sort is always the bar's own — a chip press no longer reverts
        /// it to default.</para>
        /// </summary>
        private void ApplyQuery()
        {
            CardFilter filter = _query.Filter;
            if (_classAxis.IsConstrained)
                filter.Classes = new List<CardClass>(_classAxis.Selected);

            _wall.SetQuery(filter, _query.Sort);
            UpdateWallLabels();
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
            ManaCurveView.Rebuild(_curve, _deck, _db);

            if (_deckCountLabel != null)
                _deckCountLabel.text = $"{_deck.TotalCount} / {DeckValidator.DeckSize}";
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
                    if (DeckValidator.CanAdd(_deck, def, _owned)) { AddCopy(def); progressed = true; }
                }
            }
        }
#endif

    }
}
