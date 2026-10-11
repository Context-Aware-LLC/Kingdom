using Kingdom.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// One ordered class pair drawn from the prepared 2x4 hero sheet. The primary class owns the
    /// larger left field; the secondary owns the right. Both quads share the same two seam vertices,
    /// so there is no spacer, stroke or transparent gutter between them.
    /// Every cell is exactly half the sheet wide and a quarter tall, with no gutter: a cell cut to
    /// any other size shows a strip of its neighbour along the tile's edge.
    /// </summary>
    public sealed class DeckClassPortrait : VisualElement
    {
        public const string SheetKey = "hero_portrait_sheet";

        // The average of the seam's top and bottom positions. This is the one composition value to
        // tune after eyes-on-glass; every deck entry surface consumes the same geometry.
        private const float PrimaryShare = 0.63f;
        private const float SeamLean = 0.06f;
        private const float HalfTexel = 0.5f;

        private readonly Sprite _sheet;
        private readonly Cell _primary;
        private readonly Cell _secondary;

        // The source art does not place every face at the same scale or coordinate. Keep the
        // framing data beside the cell mapping so each hero can be corrected independently after
        // the first eyes-on-glass pass without recutting the sheet or changing the shared seam.
        private readonly struct Cell
        {
            public Cell(int column, int row, float focusX)
            {
                Column = column;
                Row = row;
                FocusX = focusX;
            }

            public int Column { get; }
            public int Row { get; }
            public float FocusX { get; }
        }

        private DeckClassPortrait(Sprite sheet, Cell primary, Cell secondary, string variantClass)
        {
            _sheet = sheet;
            _primary = primary;
            _secondary = secondary;
            pickingMode = PickingMode.Ignore;
            AddToClassList("deck-class-portrait");
            if (!string.IsNullOrEmpty(variantClass)) AddToClassList(variantClass);
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        /// <summary>Add the shared ordered-pair portrait to a deck control. False leaves the caller
        /// free to retain its existing fallback artwork when the shipping sprite is unavailable.</summary>
        public static bool Mount(
            VisualElement host,
            IUiSpriteProvider sprites,
            CardClass primary,
            CardClass secondary,
            string variantClass)
        {
            if (host == null || sprites == null) return false;
            Sprite sheet = sprites.Get(SheetKey);
            if (sheet == null || !TryCell(primary, out Cell primaryCell)
                              || !TryCell(secondary, out Cell secondaryCell))
                return false;

            host.Add(new DeckClassPortrait(sheet, primaryCell, secondaryCell, variantClass));
            return true;
        }

        private void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width;
            float height = contentRect.height;
            if (width <= 0f || height <= 0f || _sheet == null || _sheet.texture == null) return;
            DrawPair(context, width, height);
        }

        private void DrawPair(MeshGenerationContext context, float width, float height)
        {
            float seamTop = width * (PrimaryShare + SeamLean);
            float seamBottom = width * (PrimaryShare - SeamLean);

            // Secondary first, primary second. They meet on the same edge; drawing the larger,
            // identity-bearing primary last also makes fractional-pixel coverage prefer it.
            DrawCell(context, _secondary,
                seamTop, 0f, width, 0f, width, height, seamBottom, height);
            DrawCell(context, _primary,
                0f, 0f, seamTop, 0f, seamBottom, height, 0f, height);
        }

        private void DrawCell(
            MeshGenerationContext context,
            Cell cell,
            float topLeftX,
            float topLeftY,
            float topRightX,
            float topRightY,
            float bottomRightX,
            float bottomRightY,
            float bottomLeftX,
            float bottomLeftY)
        {
            Texture2D texture = _sheet.texture;
            Rect sheetRect = _sheet.textureRect;
            float cellWidth = sheetRect.width / 2f;
            float cellHeight = sheetRect.height / 4f;
            var source = new Rect(
                sheetRect.x + cell.Column * cellWidth,
                sheetRect.y + sheetRect.height - (cell.Row + 1) * cellHeight,
                cellWidth,
                cellHeight);

            // Treat the picture as one undistorted rectangle beneath a diagonal clip. Mapping the
            // same uMax onto both slanted-edge vertices would squeeze the lower scanlines harder
            // than the upper ones and visibly shear a face.
            float destinationLeft = Mathf.Min(topLeftX, bottomLeftX);
            float destinationRight = Mathf.Max(topRightX, bottomRightX);
            float destinationWidth = destinationRight - destinationLeft;
            float destinationHeight = Mathf.Max(1f, bottomLeftY - topLeftY);
            Rect uv = Cover(source, destinationWidth / destinationHeight, cell.FocusX);

            float uMin = (uv.xMin + HalfTexel) / texture.width;
            float uMax = (uv.xMax - HalfTexel) / texture.width;
            float vMin = (uv.yMin + HalfTexel) / texture.height;
            float vMax = (uv.yMax - HalfTexel) / texture.height;

            MeshWriteData mesh = context.Allocate(4, 6, texture);
            Color32 white = new Color32(255, 255, 255, 255);
            mesh.SetAllVertices(new[]
            {
                VertexAt(topLeftX, topLeftY,
                    UvX(topLeftX, destinationLeft, destinationWidth, uMin, uMax), vMax, white),
                VertexAt(topRightX, topRightY,
                    UvX(topRightX, destinationLeft, destinationWidth, uMin, uMax), vMax, white),
                VertexAt(bottomRightX, bottomRightY,
                    UvX(bottomRightX, destinationLeft, destinationWidth, uMin, uMax), vMin, white),
                VertexAt(bottomLeftX, bottomLeftY,
                    UvX(bottomLeftX, destinationLeft, destinationWidth, uMin, uMax), vMin, white),
            });
            mesh.SetAllIndices(new ushort[] { 0, 1, 2, 2, 3, 0 });
        }

        private static Rect Cover(Rect source, float destinationAspect, float focusX)
        {
            float sourceAspect = source.width / source.height;
            if (destinationAspect < sourceAspect)
            {
                float visibleWidth = source.height * destinationAspect;
                float centeredX = source.x + source.width * focusX - visibleWidth * 0.5f;
                float x = Mathf.Clamp(centeredX, source.x, source.xMax - visibleWidth);
                return new Rect(x, source.y, visibleWidth, source.height);
            }

            float visibleHeight = source.width / destinationAspect;
            float y = source.y + (source.height - visibleHeight) * 0.5f;
            return new Rect(source.x, y, source.width, visibleHeight);
        }

        private static Vertex VertexAt(float x, float y, float u, float v, Color32 tint) => new Vertex
        {
            position = new Vector3(x, y, Vertex.nearZ),
            uv = new Vector2(u, v),
            tint = tint,
        };

        private static float UvX(
            float x,
            float destinationLeft,
            float destinationWidth,
            float uMin,
            float uMax) =>
            Mathf.Lerp(uMin, uMax, (x - destinationLeft) / destinationWidth);

        private static bool TryCell(CardClass cardClass, out Cell cell)
        {
            switch (cardClass)
            {
                case CardClass.TEMPEST: cell = new Cell(0, 0, 0.50f); return true;
                case CardClass.TIDECALLER: cell = new Cell(1, 0, 0.53f); return true;
                case CardClass.STONEWARDEN: cell = new Cell(0, 1, 0.50f); return true;
                case CardClass.VERDANT: cell = new Cell(1, 1, 0.58f); return true;
                case CardClass.GRAVEKEEP: cell = new Cell(0, 2, 0.63f); return true;
                case CardClass.NIGHTVEIL: cell = new Cell(1, 2, 0.62f); return true;
                case CardClass.CLOCKWRIGHT: cell = new Cell(0, 3, 0.50f); return true;
                case CardClass.EMBERWARD: cell = new Cell(1, 3, 0.58f); return true;
                default: cell = default; return false;
            }
        }
    }
}
