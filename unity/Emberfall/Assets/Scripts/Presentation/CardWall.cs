using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The card wall: one persistent baked tile per pool card, laid out edge-to-edge in a grid whose
    /// gutter is solved per row. Extracted from the old <c>DeckbuilderController</c> (Sam, 2026-08-07)
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
        // Must match .card-tile width in deckbuilder.uss / collection.uss. Tile size is FIXED — this
        // is a bake-cache wall, never stretched to fill space — so the gutter is the only lever.
        public const float TileWidth = 276f;

        /// <summary>Floor == --space-4; never squeeze tighter than that token.</summary>
        private const float MinGutter = 16f;

        // A row solved to land EXACTLY on the available width is a floating-point tie: the tile's
        // real resolved width (measured live: 275.9598px, not the authored 276px) never matches
        // TileWidth to the decimal, so the row Yoga actually lays out overshoots by a hair and it
        // silently wraps one tile early — our margins still assumed the wider row, so the tile that
        // should've been last-in-row lands first-in-next-row with its trailing margin zeroed, flush
        // against its new neighbour. This margin keeps every row comfortably under budget.
        private const float RowSafetyMargin = 2f;

        private readonly VisualElement _grid;
        private readonly CardFaceBaker _baker;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private readonly Dictionary<string, VisualElement> _tiles = new Dictionary<string, VisualElement>();
        private readonly List<CardDefinition> _visible = new List<CardDefinition>();

        private CardFilter _filter;                       // null = no constraint
        private CardSort _sort = CardSort.Default;
        private bool _layoutQueued;

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
                tile.RegisterCallback<ClickEvent>(_ => TileClicked?.Invoke(captured));

                _baker.Bake(def, tex =>
                {
                    if (tex == null) return;
                    tile.style.backgroundImage =
                        new StyleBackground(Background.FromRenderTexture((RenderTexture)tex));
                });

                _grid.Add(tile);
                _pool.Add(def);
                _tiles[def.Id] = tile;
            }

            ApplyQuery();
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
        public VisualElement TileOf(string cardId) =>
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
            QueueLayout();
        }

        // ---- Gutter solve ----------------------------------------------------------------------
        //
        // Fixed tile size, variable gutter, recomputed per row on every resize (panel opening or
        // closing, window resize) so full rows span the grid edge to edge instead of packing left
        // with dead space on the right; a partial last row stays left-aligned at the same gutter
        // rather than spreading apart. This is the manual equivalent of CSS Grid's
        // `repeat(auto-fill, TileWidth)` + `justify-content: space-between`, which UI Toolkit's
        // flexbox-only layout has no direct way to express.

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
            float available = _grid.resolvedStyle.width - RowSafetyMargin;
            if (available <= 0f) return;

            int columns = Mathf.Max(1, Mathf.FloorToInt((available + MinGutter) / (TileWidth + MinGutter)));
            float gutter = columns > 1 ? (available - columns * TileWidth) / (columns - 1) : 0f;

            for (int i = 0; i < _visible.Count; i++)
            {
                if (!_tiles.TryGetValue(_visible[i].Id, out var tile)) continue;
                bool endOfRow = (i % columns) == columns - 1 || i == _visible.Count - 1;
                tile.style.marginLeft = 0f;
                tile.style.marginRight = endOfRow ? 0f : gutter;
            }
        }
    }
}
