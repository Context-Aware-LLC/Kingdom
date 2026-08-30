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
        // Must match .card-tile in Collection/collection.uss. These are the baker's safe pre-layout defaults
        // and the SMALLEST OF THE THREE FIXED TIERS. Layout promotes the wall to a larger tier when
        // at least four columns and two complete rows fit. Discrete tiers matter: a continuous
        // resize would invalidate and rebake ~90 RenderTextures on every window pixel.
        //
        // THIS IS NO LONGER THE FLOOR. It used to be — ResolveTileTier fell back to it
        // unconditionally, with nothing below — and on a phone that was #177/#179's whole
        // complaint. Measured at the device dp budget, 2026-08-25, before the compact tier below
        // existed:
        //
        //     #collection-scroll   704.4 x 291.0      the viewport
        //     tile 0 :             340.2 x 477.0      TALLER than the viewport, half its width
        //     tiles per row: 1     #collection-grid resolved to 133480dp tall
        //
        // One card per row and a grid 325 screens long, which is exactly Sam's report: "there's one
        // row of cards and then it's just the background". The tier table was authored when an
        // authored px was an arbitrary unit; since 99b179b1 it is one Android dp, so 340 means
        // 340dp and a phone has 291dp of viewport to put it in. The table never moved with the
        // unit.
        public const float TileWidth = 340f;
        public const float TileHeight = 477f;

        public const float MediumTileWidth = 480f;
        public const float MediumTileHeight = 674f;

        public const float DesktopTileWidth = 620f;
        public const float DesktopTileHeight = 870f;

        /// <summary>Floor == --space-4; never squeeze tighter than that token.</summary>
        private const float MinGutter = 16f;

        /// <summary>Do not turn a four-card row into four islands just to touch both edges.</summary>
        private const float MaxGutter = 32f;

        /// <summary>Art-first desktop target; extra width becomes breathing room, not tiny cards.</summary>
        private const int MaxColumns = 5;

        /// <summary>A larger tier must still form a wall rather than a two-card detail view.</summary>
        private const int MinColumnsForLargerTier = 4;

        /// <summary>Keep two complete rows visible before promoting a viewport to a larger tier.</summary>
        private const int TargetVisibleRows = 2;

        /// <summary>The vertical space between two rows, and the FLOOR for the derived tier's
        /// solved pitch. Must match <c>.card-tile</c>'s top+bottom margin in collection.uss (two
        /// times --space-1), which is what a fixed tier still uses.</summary>
        private const float VerticalTileMargin = 8f;

        /// <summary>The COMPACT floor is derived from the viewport rather than tabulated, because a
        /// fourth magic pair would have gone stale the same way the other three did. Height is
        /// solved for <see cref="TargetVisibleRows"/> and then quantised to this step, which is what
        /// keeps the bake count bounded — the whole reason the larger tiers are discrete. A phone
        /// resizing between 128 and 144 rebakes once, not on every pixel.</summary>
        private const float TileQuantum = 16f;

        /// <summary>Legibility floor for the derived tier. At the phone's 2.625x scale a 96dp tile
        /// is 252 device px, against the 265px collection tile EF-050's acuity margin was measured
        /// on — so this is about where a card face stops being readable, and the wall would rather
        /// show fewer rows than unreadable ones.</summary>
        private const float MinTileHeight = 96f;

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
            _scroll = _grid.GetFirstAncestorOfType<ScrollView>();
            _viewport = _scroll?.contentViewport;
            _viewport?.RegisterCallback<GeometryChangedEvent>(_ => QueueLayout());
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
            float viewportHeight = _viewport?.resolvedStyle.height ?? 0f;
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

            ResolveTileTier(available, viewportHeight,
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
        /// <para><b>The margin is an output because the derived tier spends it (#322/#298).</b>
        /// Quantising the height leaves a remainder — up to <see cref="TileQuantum"/> per row — and
        /// left in the margin's default that remainder lands at the BOTTOM of the viewport, where
        /// it shows as the top sliver of row three: a card cut through the middle with only its
        /// cost band visible. Sam's ruling, 2026-08-30, is that the sliver is not to be recovered
        /// by shortening rows; the wall is to END CLEANLY after the two rows that read. So the
        /// derived tier hands the remainder back as row PITCH, and two rows fill the viewport
        /// exactly. Row three is still there and still scrolls to — it just does not peek.</para>
        ///
        /// <para>The fixed tiers keep the flat margin. Their slack is whatever a tabulated size
        /// happens to leave over a desk-sized viewport, which can be hundreds of dp — spreading
        /// THAT as pitch would push the rows apart into the "islands" outcome the gutter solve is
        /// bounded to avoid.</para>
        /// </summary>
        private static void ResolveTileTier(
            float availableWidth,
            float viewportHeight,
            out float tileWidth,
            out float tileHeight,
            out float verticalMargin)
        {
            verticalMargin = VerticalTileMargin;

            if (TierFits(availableWidth, viewportHeight, DesktopTileWidth, DesktopTileHeight))
            {
                tileWidth = DesktopTileWidth;
                tileHeight = DesktopTileHeight;
                return;
            }

            if (TierFits(availableWidth, viewportHeight, MediumTileWidth, MediumTileHeight))
            {
                tileWidth = MediumTileWidth;
                tileHeight = MediumTileHeight;
                return;
            }

            if (TierFits(availableWidth, viewportHeight, TileWidth, TileHeight))
            {
                tileWidth = TileWidth;
                tileHeight = TileHeight;
                return;
            }

            // COMPACT: derived, not tabulated. Solve the height that puts TargetVisibleRows in the
            // viewport, quantise it so the bake count stays bounded, and take the width from the
            // fixed tiers' own aspect so a card keeps its proportions across every tier.
            float solved = viewportHeight / TargetVisibleRows - VerticalTileMargin;
            solved = Mathf.Floor(solved / TileQuantum) * TileQuantum;
            tileHeight = Mathf.Max(solved, MinTileHeight);
            tileWidth = Mathf.Round(tileHeight * (TileWidth / TileHeight));

            // Spend the quantiser's remainder as pitch so TargetVisibleRows fill the viewport to
            // the pixel. Clamped at the floor for the one case that cannot be paid: MinTileHeight
            // above it, where the tile is already taller than its share and the wall would rather
            // show fewer legible rows than more unreadable ones.
            verticalMargin = Mathf.Max(
                VerticalTileMargin,
                viewportHeight / TargetVisibleRows - tileHeight);
        }

        private static bool TierFits(float availableWidth, float viewportHeight, float width, float height)
        {
            float rowWidth = MinColumnsForLargerTier * width
                + (MinColumnsForLargerTier - 1) * MinGutter;
            float rowsHeight = TargetVisibleRows * (height + VerticalTileMargin);
            return availableWidth >= rowWidth && viewportHeight >= rowsHeight;
        }

        private void BakeTiles()
        {
            foreach (CardDefinition def in _pool)
            {
                if (!_tiles.TryGetValue(def.Id, out VisualElement tile)) continue;
                CardDefinition captured = def;
                _baker.Bake(captured, tex =>
                {
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
    }
}
