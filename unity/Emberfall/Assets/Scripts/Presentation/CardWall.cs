using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The card wall: one persistent baked tile per pool card, laid out in a centred grid whose
    /// responsive tile tier, gutter and outer breathing room are solved from the live viewport.
    /// Extracted from the old <c>DeckbuilderController</c> (Sam, 2026-08-07)
    /// so browsing and deckbuilding render the same wall rather than two copies that drift — the
    /// failure the four card frames already demonstrate, where four independently maintained versions
    /// of one thing ended up 11% different in size. Those two screens are now one
    /// <see cref="CollectionController"/> in two modes (#152), which is the same argument carried to
    /// its conclusion.
    ///
    /// <para><b>Not a MonoBehaviour, deliberately.</b> It owns no scene state and needs no lifecycle;
    /// a host component constructs it with a grid element and a baker. Same shape as
    /// <c>DragTargetingModel</c> (EF-048), for the same reason: the layout arithmetic below is the
    /// part worth testing and a MonoBehaviour would put it out of reach.</para>
    ///
    /// <para><b>Not virtualized.</b> ~90 cards of baked (cached) textures is cheap next to the
    /// 13-renderer hierarchy a virtualized ListView of live cards would need instead. Tiles are built
    /// once; filtering toggles their display and sorting re-parents them, so neither ever re-bakes.</para>
    /// </summary>
    public sealed class CardWall
    {
        // THE TILE SIZE IS AUTHORED IN USS, NOT HERE. These two are only the fallback used before
        // any style has resolved, which is also what CardFaceBaker starts its RenderTextures at —
        // hence public. The live size comes from the custom properties --card-tile-width and
        // --card-tile-height on the grid element, declared per screen class in
        // Collection/collection.uss and read here through CustomStyleResolvedEvent, so the card
        // size lives beside every other size on the screen and a designer never opens this file to
        // change it. Layout may only SHRINK the authored number to make whole rows fit
        // (ResolveTileSize); it never grows it.
        //
        // THREE FIXED TIERS USED TO LIVE HERE — 340x477, 480x674 and 620x870 dp, promoted by a
        // TierFits gate wanting four columns and two complete rows. Deleted 2026-09-01 (Sam),
        // because that gate could not pass on any device that exists: the SMALLEST of the three
        // demanded 970dp of scroll viewport, the middle 1364dp and the largest 1756dp, while a
        // landscape phone panel is 411dp tall IN TOTAL and a 1080p desktop panel is 1080dp. The
        // wall had therefore never once left the derived path below, on any hardware. The table was
        // authored when an authored px was an arbitrary unit; since 99b179b1 it is one Android dp
        // and it never moved with the unit — the same failure that made #177/#179 a one-card-per-row
        // wall, measured at the device dp budget on 2026-08-25:
        //
        //     #collection-scroll   704.4 x 291.0      the viewport
        //     tile 0 :             340.2 x 477.0      TALLER than the viewport, half its width
        //     tiles per row: 1     #collection-grid resolved to 133480dp tall
        //
        // RETIREMENT TRIGGER: if the wall ever stops reading those two custom properties, these
        // stop being a fallback and become the size again — say so here and in collection.uss,
        // whose .card-tile block carries the matching note.
        public const float TileWidth = 114f;
        public const float TileHeight = 160f;

        /// <summary>USS custom property names for the authored tile size. Declared on the grid in
        /// Collection/collection.uss; unitless numbers, because a CustomStyleProperty&lt;float&gt;
        /// reads a number and not a length.</summary>
        private static readonly CustomStyleProperty<float> AuthoredWidthProp =
            new CustomStyleProperty<float>("--card-tile-width");
        private static readonly CustomStyleProperty<float> AuthoredHeightProp =
            new CustomStyleProperty<float>("--card-tile-height");

        /// <summary>Floor == --space-4; never squeeze tighter than that token.</summary>
        private const float MinGutter = 16f;

        /// <summary>Do not turn a four-card row into four islands just to touch both edges.</summary>
        private const float MaxGutter = 32f;

        /// <summary>Art-first desktop target; extra width becomes breathing room, not tiny cards.</summary>
        private const int MaxColumns = 5;

        /// <summary>The FEWEST complete rows the wall will show. It is a floor, not a target: a
        /// viewport tall enough for more rows at the authored size gets MORE ROWS, never a bigger
        /// card. That inversion is the whole reason the tier table above could not be replaced by
        /// simply deleting it — the old solve divided the viewport by exactly this number every
        /// time, so a taller screen only ever grew the cards.</summary>
        private const int MinVisibleRows = 2;

        /// <summary>The vertical space between two rows, and the FLOOR for the solved pitch.
        /// Must match <c>.card-tile</c>'s top+bottom margin in collection.uss (two times
        /// --space-1).</summary>
        private const float VerticalTileMargin = 8f;

        /// <summary>When the authored height does not fit, the solved height is quantised to this
        /// step. That is what keeps the bake count bounded: a continuous resize would otherwise
        /// invalidate and re-bake every RenderTexture on every window pixel. A phone resizing
        /// between 128 and 144 rebakes once, not on every pixel.</summary>
        private const float TileQuantum = 16f;

        /// <summary>Legibility floor for the solved height: the point below which the wall shows
        /// FEWER rows rather than unreadable ones.
        ///
        /// <para>Derived, not picked. EF-050 measured the collection tile's acuity margin at 265
        /// device px; at the Pixel 5a's 2.625x that is 100.95dp, and the floor is the first
        /// <see cref="TileQuantum"/> step at or above it — 112dp, which is 294 device px. Being a
        /// quantum multiple matters: every other solved height is one, so a floor that is not lands
        /// the clamp off the ladder every other value sits on.</para>
        ///
        /// <para>This was 96dp until 2026-09-01, which is 252 device px — BELOW the 265px margin the
        /// comment here cited as its own justification. It never bit, and could not have: the floor
        /// engages only under a 240dp wall viewport and a landscape phone gives ~351dp. Corrected
        /// because the next person to solve for a smaller surface would have inherited it.</para>
        ///
        /// <para>There is deliberately no minimum WIDTH. Width is <c>height * authored aspect</c>,
        /// so a height floor already floors it; a separate width constant could only ever be dead,
        /// or disagree.</para></summary>
        private const float MinTileHeight = 112f;

        // Pointer movement beyond this distance (in panel pixels) is treated as a scroll gesture,
        // not a tap. Keeps PointerUp from firing TileClicked after the player drags to scroll.
        private const float TileDragThreshold = 10f;

        // A row solved to land EXACTLY on the available width is a floating-point tie: the tile's
        // real resolved width (measured live: 275.9598px, not the authored 276px) never matches
        // TileWidth to the decimal, so the row Yoga actually lays out overshoots by a hair and it
        // silently wraps one tile early — our margins still assumed the wider row, so the tile that
        // should've been last-in-row lands first-in-next-row with its trailing margin zeroed, flush
        // against its new neighbour. This margin keeps every row comfortably under budget.
        private const float RowSafetyMargin = 2f;

        /// <summary>The same reserve, on the other axis, and it had no counterpart until
        /// 2026-09-01.
        ///
        /// <para><see cref="ResolveTileSize"/> spends its quantiser remainder as pitch so the rows
        /// fill the viewport TO THE PIXEL (#298) — which leaves the last row with nothing to round
        /// into, while UI Toolkit snaps every margin to a DEVICE pixel and the phone's scale is
        /// 2.625x. So the painted stack can exceed the viewport by a snap or two of accumulated
        /// error and clip the bottom row, for the same reason a row wrapped one tile early before
        /// <see cref="RowSafetyMargin"/> existed. Held out of the viewport in <see cref="Layout"/>
        /// rather than inside the solve, so the solve's own contract — its rows come to its budget
        /// exactly — is unchanged and still testable.</para>
        ///
        /// <para><b>This is NOT the row-2 clipping seen on device.</b> That is the Android-only
        /// ScrollView paint fault (#283/#285/#307): content painting into ~304dp of a 350.9dp
        /// viewport, ~47dp short, which no 2dp reserve addresses and which is not a geometry bug at
        /// all. Do not read a fix for it here.</para></summary>
        private const float LastRowSafetyMargin = 2f;

        private readonly VisualElement _grid;
        private readonly ScrollView _scroll;
        private readonly VisualElement _viewport;
        private readonly CardFaceBaker _baker;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private readonly Dictionary<int, VisualElement> _tiles = new Dictionary<int, VisualElement>();
        private readonly List<CardDefinition> _visible = new List<CardDefinition>();

        private CardFilter _filter;                       // null = no constraint
        private CardSort _sort = CardSort.Default;
        private bool _layoutQueued;
        private float _activeTileWidth = -1f;
        private float _activeTileHeight = -1f;
        private bool _tilesNeedBake;

        private int _bakeGeneration;
        private int _bakesOutstanding;

        // The size authored in USS, or the fallback until the first CustomStyleResolvedEvent.
        // Never -1: an unresolved value here would divide into the aspect solve.
        private float _authoredTileWidth = TileWidth;
        private float _authoredTileHeight = TileHeight;

        // Cache used by Layout() to break the GeometryChangedEvent feedback loop.
        // See Layout() for the full explanation.
        private float _lastLayoutAvailable = -1f;
        private float _lastLayoutViewportHeight = -1f;
        private int _lastLayoutVisibleCount = -1;

        /// <summary>Total <see cref="Layout"/> calls since construction. Read by NOR-360 instrumentation.</summary>
        public int LayoutCount { get; private set; }

        /// <summary>Raised when a tile is clicked. The wall itself never mutates anything.</summary>
        public event Action<CardDefinition> TileClicked;

        /// <summary>
        /// Raised when every card in the pool has been baked or has failed — the wall is as drawn
        /// as it will get. Raised again after any subsequent pass, so a listener that only wants
        /// the first one must unsubscribe itself.
        ///
        /// <para>Exists so <see cref="CollectionController"/> can hold the loading plate over the
        /// screen until the wall is actually painted. That is not cosmetic: the bake queue is
        /// strictly serial and each card blocks on its own art load, so the whole pool takes real
        /// time to come in and the player would otherwise watch several hundred placeholder
        /// rectangles fill in one at a time.</para>
        /// </summary>
        public event Action BakePassCompleted;

        /// <summary>
        /// Applied to every tile on <see cref="RefreshTiles"/>, for per-tile state the wall knows
        /// nothing about — the deckbuilder's copy-cap fade is the only user today. Kept as a hook
        /// rather than a feature because "you already hold every copy" is a fact about a DECK, and
        /// the Collection screen has none.
        /// </summary>
        public Action<CardDefinition, VisualElement> TileDecorator { get; set; }

        /// <summary>Cards currently passing the filter, in sorted order. The wall's read model.</summary>
        public IReadOnlyList<CardDefinition> Visible => _visible;

        public CardWall(VisualElement grid, CardFaceBaker baker)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _baker = baker ?? throw new ArgumentNullException(nameof(baker));
            _grid.RegisterCallback<GeometryChangedEvent>(_ => QueueLayout());
            _grid.RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
            _scroll = _grid.GetFirstAncestorOfType<ScrollView>();
            _viewport = _scroll?.contentViewport;
            _viewport?.RegisterCallback<GeometryChangedEvent>(_ => QueueLayout());
        }

        /// <summary>
        /// Pick up the tile size authored in USS. This is the ONLY reason the wall knows a size at
        /// all — see the fallback constants at the top of the class.
        ///
        /// <para><b>Why a custom property and not <c>resolvedStyle</c> of a tile.</b> Layout writes
        /// the solved size onto every tile as an INLINE style, and an inline style is what
        /// <c>resolvedStyle</c> then reports — so after the first pass the wall could no longer read
        /// back what USS asked for, only what it had already decided. It would converge on its own
        /// output. A custom property is never written by us, so it stays the author's number.</para>
        ///
        /// <para>Fires on load and again on any style change, including the screen-class swap
        /// <see cref="ScreenClass"/> performs when a panel resizes across a breakpoint — which is
        /// exactly when the authored size is meant to change. A missing property leaves the previous
        /// value alone rather than resetting to the fallback, so a partial stylesheet cannot shrink
        /// a wall that was already sized.</para>
        /// </summary>
        private void OnCustomStyleResolved(CustomStyleResolvedEvent evt)
        {
            bool moved = false;
            if (evt.customStyle.TryGetValue(AuthoredWidthProp, out float w) && w > 0f
                && !Mathf.Approximately(w, _authoredTileWidth))
            {
                _authoredTileWidth = w;
                moved = true;
            }
            if (evt.customStyle.TryGetValue(AuthoredHeightProp, out float h) && h > 0f
                && !Mathf.Approximately(h, _authoredTileHeight))
            {
                _authoredTileHeight = h;
                moved = true;
            }
            if (!moved) return;

            // Layout early-returns when its three inputs are unchanged, and the authored size is a
            // FOURTH input it does not track. Clearing the cache is what makes a stylesheet change
            // actually reach the tiles.
            _lastLayoutAvailable = -1f;
            QueueLayout();
        }

        /// <summary>
        /// Build one tile per card. Safe to call again — tiles and bakes are discarded and remade,
        /// so prefer <see cref="SetQuery"/> for anything that is only a change of view.
        /// </summary>
        public void SetPool(IEnumerable<CardDefinition> pool)
        {
            _grid.Clear();
            _tiles.Clear();
            _pool.Clear();

            foreach (var def in pool)
            {
                var tile = new VisualElement();
                tile.AddToClassList("card-tile");
                tile.userData = def;

                var captured = def;

                // Distinguish a tap from a scroll gesture. ClickEvent fires even after a drag, so
                // replacing it with pointer tracking: a gesture that moves more than TileDragThreshold
                // or leaves the tile before releasing is a scroll, not a tap.
                bool tilePointerDown = false;
                bool tileDragged     = false;
                Vector2 tileDownPos  = default;

                tile.RegisterCallback<PointerDownEvent>(e =>
                {
                    tilePointerDown = true;
                    tileDragged     = false;
                    tileDownPos     = new Vector2(e.position.x, e.position.y);
                });
                tile.RegisterCallback<PointerMoveEvent>(e =>
                {
                    if (!tilePointerDown) return;
                    float dx = e.position.x - tileDownPos.x;
                    float dy = e.position.y - tileDownPos.y;
                    if (dx * dx + dy * dy > TileDragThreshold * TileDragThreshold)
                        tileDragged = true;
                });
                tile.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    // Pointer left the tile — this is a scroll gesture, not a tap.
                    tilePointerDown = false;
                    tileDragged     = false;
                });
                tile.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (tilePointerDown && !tileDragged) TileClicked?.Invoke(captured);
                    tilePointerDown = false;
                    tileDragged     = false;
                });
                tile.RegisterCallback<PointerCancelEvent>(_ =>
                {
                    tilePointerDown = false;
                    tileDragged     = false;
                });

                _grid.Add(tile);
                _pool.Add(def);
                _tiles[def.Id] = tile;
            }

            // Geometry is not trustworthy while this tree is being built. The first scheduled
            // Layout chooses the responsive tier, sets the baker to that exact resolution, and only
            // then requests faces. That avoids baking the whole collection small and immediately
            // throwing those textures away on a desktop viewport.
            _tilesNeedBake = true;
            ApplyQuery();
            ScrollToTop();
        }

        /// <summary>
        /// Change what is shown and in what order. Costs no bakes: filtering toggles display and
        /// sorting re-parents the existing tiles, both of which leave the RenderTexture alone.
        /// </summary>
        public void SetQuery(CardFilter filter, CardSort sort)
        {
            _filter = filter;
            _sort = sort;
            ApplyQuery();
            ScrollToTop();
        }

        /// <summary>
        /// Put the viewport back at the first row. Called on every query change, because the wall
        /// gives no hint that the result of a filter is somewhere ABOVE the current scroll position:
        /// narrowing a pool from a scrolled-down position lands the player on empty background with
        /// the matches off-screen overhead, which reads as "the filter returned nothing" (Sam,
        /// 2026-08-26).
        ///
        /// <para>Deferred as well as immediate. The immediate write is what the player sees; the
        /// scheduled one covers the case where the grid is still resolving its new height when this
        /// runs and the ScrollView re-clamps the offset afterwards.</para>
        /// </summary>
        private void ScrollToTop()
        {
            if (_scroll == null) return;
            _scroll.scrollOffset = Vector2.zero;
            _scroll.schedule.Execute(() => _scroll.scrollOffset = Vector2.zero);
        }

        /// <summary>Re-run <see cref="TileDecorator"/> over every tile. Cheap; call on any state change.</summary>
        public void RefreshTiles()
        {
            if (TileDecorator == null) return;
            foreach (var def in _pool)
                if (_tiles.TryGetValue(def.Id, out var tile))
                    TileDecorator(def, tile);
        }

        /// <summary>The tile for a card id, or null. For hosts that need to reach one directly.</summary>
        public VisualElement TileOf(int cardId) =>
            _tiles.TryGetValue(cardId, out var tile) ? tile : null;

        // ---- Query -> visible set -> DOM order ------------------------------------------------
        private void ApplyQuery()
        {
            _visible.Clear();
            foreach (var def in CardQuery.Search(_pool, _filter, _sort))
                _visible.Add(def);

            // Hide everything, then show and re-parent the survivors in sorted order. Add() on an
            // element already in the tree MOVES it to the end rather than duplicating it, so walking
            // the visible list in order leaves the grid's children in exactly that order — and the
            // hidden ones bunched behind them, where display:none keeps them out of the flow.
            foreach (var tile in _tiles.Values)
                tile.style.display = DisplayStyle.None;

            foreach (var def in _visible)
            {
                if (!_tiles.TryGetValue(def.Id, out var tile)) continue;
                tile.style.display = DisplayStyle.Flex;
                _grid.Add(tile);
            }

            RefreshTiles();
            _lastLayoutAvailable    = -1f;  // force a fresh Layout() pass for the new visible set
            _lastLayoutViewportHeight = -1f;
            _lastLayoutVisibleCount = -1;
            QueueLayout();
        }

        // ---- Gutter solve ----------------------------------------------------------------------
        //
        // Discrete responsive tile size, bounded gutter, recomputed per row on every resize (panel
        // opening or closing, window resize). A sparse row keeps a readable gap and spends the
        // surplus as equal outer breathing room instead of shrinking the art or stretching cards
        // into islands. A partial last row stays left-aligned with the full rows. UI Toolkit has no
        // CSS Grid equivalent, so explicit sizes and margins carry the solve.

        /// <summary>
        /// Defers to the next scheduler tick instead of running inline. The grid's own
        /// GeometryChangedEvent is a trigger, and Yoga fires that mid-layout-pass — mutating margins
        /// synchronously from inside it edits the tree while Yoga is still walking it, so only
        /// whichever tiles it hasn't visited yet pick up the new values. That tears a single pass
        /// into two different geometries across rows and never converges, since the mutation
        /// re-triggers the same event. Coalesces bursts (resize + filter change) into one pass.
        /// </summary>
        private void QueueLayout()
        {
            if (_layoutQueued) return;
            _layoutQueued = true;
            _grid.schedule.Execute(() =>
            {
                _layoutQueued = false;
                Layout();
            });
        }

        private void Layout()
        {
            LayoutCount++;

            float available = _grid.resolvedStyle.width - RowSafetyMargin;
            float viewportHeight = (_viewport?.resolvedStyle.height ?? 0f) - LastRowSafetyMargin;
            // `!(x > 0f)`, not `x <= 0f`: an unresolved resolvedStyle is NaN and every comparison
            // against NaN is false, so `<= 0f` passes it through to the solve. See the long note on
            // CharacterCreateController.LayoutHeroGrid's guard, where that cost a 1.3 GB OOM kill.
            if (!(available > 0f) || !(viewportHeight > 0f)) return;

            // Break the GeometryChangedEvent feedback loop.
            //
            // The problem: Layout() writes marginRight onto tiles → Yoga fires GeometryChangedEvent
            // → the registered handler calls QueueLayout() → Layout() runs again → loop.
            // _layoutQueued only coalesces bursts within the SAME tick; it does not stop the
            // cross-tick chain, because _layoutQueued is set to false before Layout() is called.
            //
            // The tier/gutter solve depends only on (available_width, viewport_height,
            // visible_count): same inputs → same sizes and margins → no new GeometryChangedEvent.
            // So early-returning when all three are unchanged writes nothing, fires no event, and
            // terminates the chain.
            //
            // ApplyQuery() resets both fields to -1 before calling QueueLayout(), so a filter or
            // sort change always runs at least one fresh pass even if the width is unchanged.
            if (Mathf.Approximately(available, _lastLayoutAvailable)
                && Mathf.Approximately(viewportHeight, _lastLayoutViewportHeight)
                && _visible.Count == _lastLayoutVisibleCount
                && !_tilesNeedBake)
                return;
            _lastLayoutAvailable    = available;
            _lastLayoutViewportHeight = viewportHeight;
            _lastLayoutVisibleCount = _visible.Count;

            ResolveTileSize(_authoredTileWidth, _authoredTileHeight, viewportHeight,
                out float tileWidth, out float tileHeight, out float verticalMargin);
            bool tierChanged = !Mathf.Approximately(tileWidth, _activeTileWidth)
                || !Mathf.Approximately(tileHeight, _activeTileHeight);
            if (tierChanged)
            {
                _activeTileWidth = tileWidth;
                _activeTileHeight = tileHeight;
                _baker.SetFaceSize(Mathf.RoundToInt(tileWidth), Mathf.RoundToInt(tileHeight));
                _tilesNeedBake = true;
            }

            int columns = Mathf.Clamp(
                Mathf.FloorToInt((available + MinGutter) / (tileWidth + MinGutter)),
                1,
                MaxColumns);
            float naturalGutter = columns > 1 ? (available - columns * tileWidth) / (columns - 1) : 0f;
            float gutter = Mathf.Min(MaxGutter, naturalGutter);
            float outer = Mathf.Max(0f,
                (available - columns * tileWidth - (columns - 1) * gutter) * 0.5f);

            // ONLY ON A TIER CHANGE, so this is a handful of lines per screen rather than per frame
            // (Layout runs on every GeometryChangedEvent). It exists because the wall's size is
            // solved from the LIVE viewport and nothing else in the build reports what that viewport
            // actually was -- a question that cost a session on 2026-08-31, when a second row began
            // clipping on device and the only numbers available were from an offscreen harness that
            // measured a DIFFERENT viewport than the device had.
            //
            // `rows` is the diagnostic that matters. The rows fill the viewport TO THE PIXEL by
            // design (ResolveTileSize spends the remainder as pitch), and there is no vertical
            // equivalent of RowSafetyMargin, so a whole number here is the clipping case: the last
            // row has nothing to round into.
            if (tierChanged)
            {
                float pitch = tileHeight + verticalMargin;
                Debug.Log($"[CardWall] tier {tileWidth:F0}x{tileHeight:F0} · viewport "
                          + $"{available:F1}x{viewportHeight:F1} · columns {columns} · pitch "
                          + $"{pitch:F2} · rows {(viewportHeight / pitch):F3} · gutter {gutter:F1}");
            }

            for (int i = 0; i < _visible.Count; i++)
            {
                if (!_tiles.TryGetValue(_visible[i].Id, out var tile)) continue;
                tile.style.width = tileWidth;
                tile.style.height = tileHeight;
                bool startOfRow = i % columns == 0;
                bool endOfRow = (i % columns) == columns - 1 || i == _visible.Count - 1;
                // `outer` is HALF the surplus, so it belongs on both ends. Applying it only on the
                // left spent the whole surplus there: the row sat flush against the right edge --
                // against the deck drawer, in the editor -- with a gap on the left that read as a
                // missing column. The grid is `justify-content: flex-start` precisely so this solve
                // is the only thing positioning a row; `space-between` there re-spread the surplus
                // between the cards instead, which is the "islands" outcome the gutter is bounded
                // to avoid. A partial last row keeps its left `outer` and so stays aligned with the
                // full rows above it.
                tile.style.marginLeft = startOfRow ? outer : 0f;
                tile.style.marginRight = endOfRow ? outer : gutter;
                // Half above, half below: the row pitch is the sum, and splitting it keeps the
                // first row off the viewport's top edge instead of flush against it.
                tile.style.marginTop = verticalMargin * 0.5f;
                tile.style.marginBottom = verticalMargin * 0.5f;
            }

            if (_tilesNeedBake)
            {
                _tilesNeedBake = false;
                BakeTiles();
            }
        }

        /// <summary>
        /// The tile size for a viewport, and the vertical margin that spaces its rows.
        ///
        /// <para><b>The authored size is the input, and this may only shrink it.</b>
        /// <paramref name="authoredWidth"/>/<paramref name="authoredHeight"/> come from USS (see
        /// the fallback constants at the top of this class). When they fit they are used exactly;
        /// when the viewport is too short for <see cref="MinVisibleRows"/> of them the height is
        /// solved down and quantised, and the width follows the AUTHORED aspect so a card keeps its
        /// proportions rather than drifting toward square.</para>
        ///
        /// <para><b>A tall viewport buys ROWS, not a bigger card.</b> The row count is derived from
        /// the authored size rather than fixed at <see cref="MinVisibleRows"/>, which is what stops
        /// a tablet spending 260dp of slack as row pitch and rendering two cards adrift in a field
        /// of background — the vertical form of the "islands" outcome <see cref="MaxGutter"/> is
        /// bounded to avoid horizontally.</para>
        ///
        /// <para><b>The margin is an output because the solve spends it (#322/#298).</b>
        /// Quantising the height leaves a remainder — up to <see cref="TileQuantum"/> per row — and
        /// left in the margin's default that remainder lands at the BOTTOM of the viewport, where
        /// it shows as the top sliver of the next row: a card cut through the middle with only its
        /// cost band visible. Sam's ruling, 2026-08-30, is that the sliver is not to be recovered
        /// by shortening rows; the wall is to END CLEANLY after the rows that read. So the solve
        /// hands the remainder back as row PITCH and the rows fill the viewport exactly. The next
        /// row is still there and still scrolls to — it just does not peek.</para>
        /// </summary>
        /// <remarks>Private, and worth keeping that way. The offscreen verifier that briefly made
        /// it public had spent months re-deriving this arithmetic by hand and had drifted on every
        /// number it copied — a fixed two rows after the row count became derived, a stale legibility
        /// floor, an aspect from a deleted tier. A caller that wants this answer calls this method;
        /// there is no second copy, and widening the modifier is how a second copy starts.</remarks>
        private static void ResolveTileSize(
            float authoredWidth,
            float authoredHeight,
            float viewportHeight,
            out float tileWidth,
            out float tileHeight,
            out float verticalMargin)
        {
            // How many complete rows of the AUTHORED tile the viewport holds, never fewer than the
            // floor. FloorToInt of a viewport shorter than one row gives 0, which the Max absorbs.
            int rows = Mathf.Max(
                MinVisibleRows,
                Mathf.FloorToInt(viewportHeight / (authoredHeight + VerticalTileMargin)));
            float rowBudget = viewportHeight / rows;

            if (authoredHeight + VerticalTileMargin <= rowBudget)
            {
                // The authored size fits. Honour it to the pixel — this is the path every phone
                // and every desktop takes, so the number in USS is the number on screen.
                tileWidth = authoredWidth;
                tileHeight = authoredHeight;
            }
            else
            {
                // Too tall for its share. Solve the height down and quantise it, which is what
                // keeps the bake count bounded — a continuous resize would otherwise invalidate and
                // re-bake every RenderTexture on every window pixel.
                float solved = Mathf.Floor((rowBudget - VerticalTileMargin) / TileQuantum) * TileQuantum;
                tileHeight = Mathf.Max(solved, MinTileHeight);
                tileWidth = Mathf.Round(tileHeight * (authoredWidth / authoredHeight));
            }

            // Spend whatever is left as pitch so the rows fill the viewport to the pixel. Clamped
            // at the flat margin for the one case that cannot be paid: MinTileHeight above the
            // solved height, where the tile is already taller than its share and the wall would
            // rather show fewer legible rows than more unreadable ones.
            verticalMargin = Mathf.Max(VerticalTileMargin, rowBudget - tileHeight);
        }

        /// <summary>
        /// Enqueue every pool card and raise <see cref="BakePassCompleted"/> when the last one has
        /// been answered — success or failure alike, since a wall that gave up on three cards is
        /// still as finished as it is going to get.
        ///
        /// <para><b>Generation-counted, because a bake pass can be superseded mid-flight.</b> A
        /// screen-class change or a resize across a row boundary calls this again while the
        /// previous pass still has callbacks outstanding, and those callbacks still arrive — they
        /// were captured before the size moved. Counting deliveries alone would then fire the
        /// completion signal early, on the tail of a pass whose textures have already been released
        /// by <c>SetFaceSize</c>. Each pass stamps its own generation and a stale callback is
        /// counted for its tile but not toward the current pass.</para>
        ///
        /// <para>Note the callback may run SYNCHRONOUSLY, out of <c>_baker.Bake</c>, when the face
        /// is already cached — so the outstanding count is seeded before the loop rather than
        /// incremented inside it, or a cache-warm wall would complete on its first card.</para>
        /// </summary>
        private void BakeTiles()
        {
            _bakeGeneration++;
            int generation = _bakeGeneration;
            _bakesOutstanding = _pool.Count;
            if (_bakesOutstanding == 0)
            {
                BakePassCompleted?.Invoke();
                return;
            }

            foreach (CardDefinition def in _pool)
            {
                if (!_tiles.TryGetValue(def.Id, out VisualElement tile))
                {
                    CountBake(generation);
                    continue;
                }
                CardDefinition captured = def;
                _baker.Bake(captured, tex =>
                {
                    CountBake(generation);
                    if (tex == null)
                    {
                        // A blank tile that is still clickable is worse than no tile: the player
                        // can add a card they cannot read. Disable picking so the slot is inert
                        // until the bake succeeds (or is never retried — same outcome either way).
                        Debug.LogWarning($"[CardWall] bake failed for '{captured.Id}' — tile will be blank and non-interactive.");
                        tile.pickingMode = PickingMode.Ignore;
                        return;
                    }
                    tile.pickingMode = PickingMode.Position;
                    tile.style.backgroundImage =
                        new StyleBackground(Background.FromRenderTexture((RenderTexture)tex));
                });
            }
        }

        private void CountBake(int generation)
        {
            if (generation != _bakeGeneration) return;
            if (--_bakesOutstanding > 0) return;
            BakePassCompleted?.Invoke();
        }
    }
}
