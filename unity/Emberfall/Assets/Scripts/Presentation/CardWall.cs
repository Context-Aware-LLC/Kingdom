using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Presents the collection as virtualized rows of five pre-baked card faces. The host supplies one
    /// <see cref="ListView"/> and a five-slot row template; this class owns row batching, recycled
    /// slot state, responsive sizing, card-face baking, and tile gestures.
    ///
    /// <para>The rows are virtualized; the faces are not. The complete pool is baked behind the
    /// loading transition before any ListView.bindItem callback may paint it. A recycled row only
    /// reads the completed cache. This boundary is deliberate: captures initiated from bindItem
    /// reproduce #341's frame-over-sockets failure in the Editor, while live RenderTexture
    /// backgrounds reproduce it on Android.</para>
    /// </summary>
    public sealed class CardWall
    {
        // These are pre-style fallbacks shared with CardFaceBaker; the authored values live on
        // .collection-list in collection.uss and are read through OnCustomStyleResolved.
        // They MIRROR that block and must equal it: until the first CustomStyleResolvedEvent the
        // wall lays out with these and CardFaceBaker seeds its first frozen face from them, so a
        // drift is a wrong first paint plus a wasted bake, on a frame the player sees.
        // They were 114/160 — the COMPACT rung of the three-tier ladder — until #349 (a81a321d)
        // deleted the compact and medium rungs from collection.uss and left these behind.
        // RETIREMENT TRIGGER: if the wall stops reading those custom properties, these become the
        // authored size again — update this note and collection.uss together.
        public const float TileWidth = 274f;
        public const float TileHeight = 384f;
        public const int CardsPerRow = 5;

        private static readonly CustomStyleProperty<float> AuthoredWidthProp =
            new CustomStyleProperty<float>("--card-tile-width");
        private static readonly CustomStyleProperty<float> AuthoredHeightProp =
            new CustomStyleProperty<float>("--card-tile-height");

        private const float MinGutter = 16f;
        private const float MaxGutter = 32f;
        private const int MinVisibleRows = 2;
        private const float VerticalTileMargin = 8f;
        // CAPS THE "SPARE VIEWPORT HEIGHT BECOMES ROW GAP" BEHAVIOUR BELOW (Sam, 2026-09-08:
        // "there's a lot of space between the rows"). ResolveTileSize divides the viewport evenly
        // across `rows` and hands whatever a row doesn't need for its tile to verticalMargin, with
        // no ceiling -- on a viewport tall enough to floor two rows with room to spare, that spare
        // room used to inflate every gap instead of just going unused. Capped here so it shows up
        // as blank space below the last row instead, which reads as "more rows could fit" rather
        // than "these rows are loosely packed". Four times the baseline gap, not a measured floor.
        private const float MaxVerticalMargin = 32f;
        private const float TileQuantum = 16f;
        // THE LEGIBILITY FLOOR IS STILL IN THE RETIRED UNIT AND IS DELIBERATELY LEFT THERE.
        // Authored 2026-08-30 as 112dp — 70% of the 160dp tile it floored, and 294 device px on a
        // Pixel 5a. TileHeight is now 384 authored px, so this floors a tile at 29% of its authored
        // height: a floor that no viewport can reach and that would not be legible if it did.
        // Not rescaled here on purpose — a legibility floor is a MEASUREMENT, and #349's sweep left
        // the whole panel un-re-measured. Fix it with eyes on glass, alongside --edge-safe (#350),
        // not as a side effect of a unit conversion.
        private const float MinTileHeight = 112f;
        private const float TileDragThreshold = 10f;
        private const float RowSafetyMargin = 2f;
        private const float LastRowSafetyMargin = 2f;

        private sealed class CardRow
        {
            public readonly CardDefinition[] Cards = new CardDefinition[CardsPerRow];
        }

        private sealed class SlotBinding
        {
            public CardDefinition Card;
            public int Version;
            public bool PointerDown;
            public bool Dragged;
            public Vector2 DownPosition;
        }

        private readonly ListView _list;
        private readonly VisualTreeAsset _rowTemplate;
        private readonly CardFaceBaker _baker;
        private readonly ScrollView _scroll;
        private readonly VisualElement _viewport;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private readonly List<CardDefinition> _visible = new List<CardDefinition>();
        private readonly List<CardRow> _rows = new List<CardRow>();

        private CardFilter _filter;
        private CardSort _sort = CardSort.Default;
        private bool _layoutQueued;
        private float _authoredTileWidth = TileWidth;
        private float _authoredTileHeight = TileHeight;
        private float _activeTileWidth = TileWidth;
        private float _activeTileHeight = TileHeight;
        private float _activeVerticalMargin = VerticalTileMargin;
        private float _activeGutter = MinGutter;
        private float _activeOuterMargin;
        private float _lastLayoutWidth = -1f;
        private float _lastLayoutHeight = -1f;

        private bool _poolBakeQueued;
        private bool _poolBakeReady;
        private int _bakeGeneration;
        private int _bakesOutstanding;

        public int LayoutCount { get; private set; }

        public event Action<CardDefinition> TileClicked;

        /// <summary>
        /// Raised when the complete card pool has finished baking, success or failure.
        /// </summary>
        public event Action BakePassCompleted;

        public Action<CardDefinition, VisualElement> TileDecorator { get; set; }

        public IReadOnlyList<CardDefinition> Visible => _visible;

        /// <summary>Resolve the current card, not a stale id from a recycled wall slot.</summary>
        public bool TryGetCard(VisualElement target, out CardDefinition card, out VisualElement tile)
        {
            card = null;
            tile = null;
            if (target == null || !_list.Contains(target)) return false;
            for (var element = target; element != null && element != _list; element = element.parent)
            {
                if (!(element.userData is SlotBinding binding) || binding.Card == null) continue;
                card = binding.Card;
                tile = element;
                return true;
            }
            return false;
        }

        public static void CancelTileTap(VisualElement tile)
        {
            if (tile?.userData is SlotBinding binding) binding.PointerDown = false;
        }

        public CardWall(ListView list, VisualTreeAsset rowTemplate, CardFaceBaker baker)
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _rowTemplate = rowTemplate ?? throw new ArgumentNullException(nameof(rowTemplate));
            _baker = baker ?? throw new ArgumentNullException(nameof(baker));

            ValidateTemplate();

            _list.selectionType = SelectionType.None;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.fixedItemHeight = TileHeight + VerticalTileMargin;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.unbindItem = UnbindRow;

            _scroll = _list.Q<ScrollView>();
            _viewport = _scroll?.contentViewport;
            HideScrollbars();

            _list.RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
            _list.RegisterCallback<GeometryChangedEvent>(_ => QueueLayout());
            _viewport?.RegisterCallback<GeometryChangedEvent>(_ => QueueLayout());
            QueueLayout();
        }

        public void SetPool(IEnumerable<CardDefinition> pool)
        {
            _pool.Clear();
            if (pool != null)
                foreach (CardDefinition def in pool)
                    if (def != null) _pool.Add(def);

            RestartPoolBake();
            ApplyQuery();
            ScrollToTop();
        }

        public void SetQuery(CardFilter filter, CardSort sort)
        {
            _filter = filter;
            _sort = sort;
            ApplyQuery();
            ScrollToTop();
            if (!_poolBakeReady) RestartPoolBake();
        }

        /// <summary>Rebind the realized rows so deck and ownership decorations update.</summary>
        public void RefreshTiles()
        {
            _list.RefreshItems();
        }

        private void ApplyQuery()
        {
            _visible.Clear();
            foreach (CardDefinition def in CardQuery.Search(_pool, _filter, _sort))
                _visible.Add(def);

            _rows.Clear();
            for (int start = 0; start < _visible.Count; start += CardsPerRow)
            {
                var row = new CardRow();
                for (int slot = 0; slot < CardsPerRow && start + slot < _visible.Count; slot++)
                    row.Cards[slot] = _visible[start + slot];
                _rows.Add(row);
            }

            _list.itemsSource = _rows;
            _list.RefreshItems();
        }

        private VisualElement MakeRow()
        {
            TemplateContainer clone = _rowTemplate.CloneTree();
            VisualElement row = clone.Q<VisualElement>("card-row");
            if (row == null)
                throw new InvalidOperationException("Card row template must contain an element named 'card-row'.");

            row.RemoveFromHierarchy();
            for (int slot = 0; slot < CardsPerRow; slot++)
            {
                VisualElement tile = row.Q<VisualElement>($"card-{slot}");
                if (tile == null)
                    throw new InvalidOperationException($"Card row template is missing slot 'card-{slot}'.");
                WireTile(tile);
            }
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _rows.Count)
            {
                UnbindRow(row, index);
                return;
            }

            ApplyRowGeometry(row);
            CardRow data = _rows[index];
            for (int slot = 0; slot < CardsPerRow; slot++)
                BindSlot(row.Q<VisualElement>($"card-{slot}"), data.Cards[slot]);
        }

        private void UnbindRow(VisualElement row, int index)
        {
            for (int slot = 0; slot < CardsPerRow; slot++)
                ClearSlot(row.Q<VisualElement>($"card-{slot}"));
        }

        private void BindSlot(VisualElement tile, CardDefinition def)
        {
            if (tile == null) return;
            var binding = tile.userData as SlotBinding;
            if (binding == null)
            {
                WireTile(tile);
                binding = (SlotBinding)tile.userData;
            }

            binding.Version++;
            binding.Card = def;
            binding.PointerDown = false;
            binding.Dragged = false;
            tile.EnableInClassList("card-tile--maxed", false);
            tile.EnableInClassList("card-tile--unowned", false);
            tile.style.backgroundImage = StyleKeyword.None;

            if (def == null)
            {
                tile.style.visibility = Visibility.Hidden;
                tile.pickingMode = PickingMode.Ignore;
                return;
            }

            tile.style.visibility = Visibility.Visible;
            tile.pickingMode = PickingMode.Ignore;
            TileDecorator?.Invoke(def, tile);

            // The ListView is allowed to virtualize elements, not rendering work. If the eager
            // pass is still running the root remains behind the loading plate and the slot stays
            // blank; CompletePoolBake refreshes every realized row once the cache is complete.
            if (!_poolBakeReady) return;
            if (!_baker.TryGetCachedFace(def.Id, out Texture2D face))
            {
                tile.pickingMode = PickingMode.Ignore;
                return;
            }

            tile.style.backgroundImage = new StyleBackground(Background.FromTexture2D(face));
            tile.pickingMode = PickingMode.Position;
        }

        private static void ClearSlot(VisualElement tile)
        {
            if (tile == null) return;
            if (tile.userData is SlotBinding binding)
            {
                binding.Version++;
                binding.Card = null;
                binding.PointerDown = false;
                binding.Dragged = false;
            }
            tile.style.backgroundImage = StyleKeyword.None;
            tile.style.visibility = Visibility.Hidden;
            tile.pickingMode = PickingMode.Ignore;
            tile.EnableInClassList("card-tile--maxed", false);
            tile.EnableInClassList("card-tile--unowned", false);
        }

        private void WireTile(VisualElement tile)
        {
            var binding = new SlotBinding();
            tile.userData = binding;

            tile.RegisterCallback<PointerDownEvent>(e =>
            {
                if (binding.Card == null) return;
                binding.PointerDown = true;
                binding.Dragged = false;
                binding.DownPosition = new Vector2(e.position.x, e.position.y);
            });
            tile.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!binding.PointerDown) return;
                float dx = e.position.x - binding.DownPosition.x;
                float dy = e.position.y - binding.DownPosition.y;
                if (dx * dx + dy * dy > TileDragThreshold * TileDragThreshold)
                    binding.Dragged = true;
            });
            tile.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                binding.PointerDown = false;
                binding.Dragged = false;
            });
            tile.RegisterCallback<PointerUpEvent>(_ =>
            {
                CardDefinition card = binding.Card;
                if (binding.PointerDown && !binding.Dragged && card != null)
                    TileClicked?.Invoke(card);
                binding.PointerDown = false;
                binding.Dragged = false;
            });
            tile.RegisterCallback<PointerCancelEvent>(_ =>
            {
                binding.PointerDown = false;
                binding.Dragged = false;
            });
        }

        private void ValidateTemplate()
        {
            TemplateContainer clone = _rowTemplate.CloneTree();
            VisualElement row = clone.Q<VisualElement>("card-row");
            if (row == null)
                throw new ArgumentException("Card row template must contain 'card-row'.", nameof(_rowTemplate));
            for (int slot = 0; slot < CardsPerRow; slot++)
                if (row.Q<VisualElement>($"card-{slot}") == null)
                    throw new ArgumentException($"Card row template must contain 'card-{slot}'.", nameof(_rowTemplate));
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent evt)
        {
            bool changed = false;
            if (evt.customStyle.TryGetValue(AuthoredWidthProp, out float width) && width > 0f
                && !Mathf.Approximately(width, _authoredTileWidth))
            {
                _authoredTileWidth = width;
                changed = true;
            }
            if (evt.customStyle.TryGetValue(AuthoredHeightProp, out float height) && height > 0f
                && !Mathf.Approximately(height, _authoredTileHeight))
            {
                _authoredTileHeight = height;
                changed = true;
            }
            if (!changed) return;

            _lastLayoutWidth = -1f;
            QueueLayout();
        }

        private void QueueLayout()
        {
            if (_layoutQueued) return;
            _layoutQueued = true;
            _list.schedule.Execute(() =>
            {
                _layoutQueued = false;
                Layout();
            });
        }

        private void Layout()
        {
            LayoutCount++;
            HideScrollbars();

            float available = (_viewport?.resolvedStyle.width ?? _list.resolvedStyle.width)
                              - RowSafetyMargin;
            float viewportHeight = (_viewport?.resolvedStyle.height ?? _list.resolvedStyle.height)
                                   - LastRowSafetyMargin;
            if (!(available > 0f) || !(viewportHeight > 0f)) return;
            if (Mathf.Approximately(available, _lastLayoutWidth)
                && Mathf.Approximately(viewportHeight, _lastLayoutHeight)) return;

            _lastLayoutWidth = available;
            _lastLayoutHeight = viewportHeight;

            ResolveTileSize(_authoredTileWidth, _authoredTileHeight, viewportHeight,
                out float tileWidth, out float tileHeight, out float verticalMargin);

            float widthBudget = Mathf.Max(1f,
                (available - (CardsPerRow - 1) * MinGutter) / CardsPerRow);
            if (tileWidth > widthBudget)
            {
                tileWidth = widthBudget;
                tileHeight = tileWidth * (_authoredTileHeight / _authoredTileWidth);
                verticalMargin = VerticalTileMargin;
            }

            float naturalGutter = (available - CardsPerRow * tileWidth) / (CardsPerRow - 1);
            float gutter = Mathf.Clamp(naturalGutter, MinGutter, MaxGutter);
            float outer = Mathf.Max(0f,
                (available - CardsPerRow * tileWidth - (CardsPerRow - 1) * gutter) * 0.5f);
            float rowHeight = tileHeight + verticalMargin;

            bool sizeChanged = !Mathf.Approximately(tileWidth, _activeTileWidth)
                || !Mathf.Approximately(tileHeight, _activeTileHeight);
            _activeTileWidth = tileWidth;
            _activeTileHeight = tileHeight;
            _activeVerticalMargin = verticalMargin;
            _activeGutter = gutter;
            _activeOuterMargin = outer;

            _list.fixedItemHeight = rowHeight;
            if (sizeChanged)
            {
                _baker.SetFaceSize(Mathf.RoundToInt(tileWidth), Mathf.RoundToInt(tileHeight));
                RestartPoolBake();
            }
            _list.RefreshItems();
        }

        private void ApplyRowGeometry(VisualElement row)
        {
            row.style.height = _activeTileHeight + _activeVerticalMargin;
            row.style.paddingLeft = _activeOuterMargin;
            row.style.paddingRight = _activeOuterMargin;

            for (int slot = 0; slot < CardsPerRow; slot++)
            {
                VisualElement tile = row.Q<VisualElement>($"card-{slot}");
                if (tile == null) continue;
                tile.style.width = _activeTileWidth;
                tile.style.height = _activeTileHeight;
                tile.style.marginLeft = 0f;
                tile.style.marginRight = slot < CardsPerRow - 1 ? _activeGutter : 0f;
                tile.style.marginTop = _activeVerticalMargin * 0.5f;
                tile.style.marginBottom = _activeVerticalMargin * 0.5f;
            }
        }

        private static void ResolveTileSize(
            float authoredWidth,
            float authoredHeight,
            float viewportHeight,
            out float tileWidth,
            out float tileHeight,
            out float verticalMargin)
        {
            int rows = Mathf.Max(
                MinVisibleRows,
                Mathf.FloorToInt(viewportHeight / (authoredHeight + VerticalTileMargin)));
            float rowBudget = viewportHeight / rows;

            if (authoredHeight + VerticalTileMargin <= rowBudget)
            {
                tileWidth = authoredWidth;
                tileHeight = authoredHeight;
            }
            else
            {
                float solved = Mathf.Floor((rowBudget - VerticalTileMargin) / TileQuantum) * TileQuantum;
                tileHeight = Mathf.Max(solved, MinTileHeight);
                tileWidth = Mathf.Round(tileHeight * (authoredWidth / authoredHeight));
            }

            verticalMargin = Mathf.Clamp(rowBudget - tileHeight, VerticalTileMargin, MaxVerticalMargin);
        }

        private void RestartPoolBake()
        {
            _poolBakeReady = false;
            _bakeGeneration++;
            if (_poolBakeQueued) return;
            _poolBakeQueued = true;
            _list.schedule.Execute(BakePool);
        }

        private void BakePool()
        {
            _poolBakeQueued = false;

            int generation = _bakeGeneration;
            _bakesOutstanding = _pool.Count;
            if (_bakesOutstanding == 0)
            {
                CompletePoolBake(generation);
                return;
            }

            for (int i = 0; i < _pool.Count; i++)
                _baker.Bake(_pool[i], _ => CountBake(generation));
        }

        private void CountBake(int generation)
        {
            if (generation != _bakeGeneration || _poolBakeReady) return;
            if (--_bakesOutstanding > 0) return;
            CompletePoolBake(generation);
        }

        private void CompletePoolBake(int generation)
        {
            if (generation != _bakeGeneration || _poolBakeReady) return;
            _poolBakeReady = true;
            _list.RefreshItems();
            BakePassCompleted?.Invoke();
        }

        private void ScrollToTop()
        {
            if (_rows.Count > 0) _list.ScrollToItem(0);
            if (_scroll == null) return;
            _scroll.scrollOffset = Vector2.zero;
            _scroll.schedule.Execute(() => _scroll.scrollOffset = Vector2.zero);
        }

        private void HideScrollbars()
        {
            if (_scroll == null) return;
            _scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
        }
    }
}
