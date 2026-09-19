using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. The in-play card focus view: a large, live copy of the real card face plus a
    /// readable rules/glossary plate.
    ///
    /// <para>The focused card is built through <see cref="BoardController.BuildFocusedCard"/>, so
    /// it is the shipping card face rather than a second visual interpretation. The companion
    /// plate keeps the glossary that cannot fit on even an enlarged card.</para>
    ///
    /// <para><b>NO COLLIDER, deliberately.</b> The panel appears under a finger that is mid-gesture
    /// on a hand card. A collider here would swallow the release, or worse, take the next tap that
    /// was aimed at the board behind it.</para>
    ///
    /// <para>Parented to the camera so it is screen-anchored: the reveal has to sit in the same
    /// place every time to be readable at a glance, and the card that triggered it may be anywhere
    /// in a fan that re-solves on every draw.</para>
    /// </summary>
    internal sealed class CardRevealPanel : MonoBehaviour
    {
        // A camera-relative plane gives the inspect surface a stable fraction of the physical
        // screen under the perspective board rig. The retired fixed world-size layout was only
        // about half-height on some phones and got smaller whenever safe-area fitting retreated the
        // board camera — exactly the opposite of what a readability surface should do.
        private const float OverlayDistance = 2f;
        internal const float CardHeightFraction = 0.84f;
        internal const float DetailWidthFraction = 0.92f;
        internal const float OuterMarginFraction = 0.045f;
        internal const float ColumnGapFraction = 0.035f;

        // Above the hand (HandSortBase 50, +20 while lifted) and below the match-end banner (100).
        private const int SortBase = 90;

        private static readonly Color PlateColor = new Color(0.09f, 0.11f, 0.16f, 1f);
        private static readonly Color DimColor = new Color(0.015f, 0.02f, 0.035f, 0.68f);

        private TextMeshPro _title;
        private TextMeshPro _subtitle;
        private TextMeshPro _body;
        private Renderer _face;
        private Transform _faceTransform;
        private SpriteRenderer _dimmer;
        private Sprite _solidSprite;
        private BoardController _board;
        private CardPresenter _focusPresenter;
        private InstanceId _focusedCard;
        private bool _hasFocusedCard;
        private float _focusX;
        private float _focusY;
        private float _focusScale;
        private int _layoutScreenWidth = -1;
        private int _layoutScreenHeight = -1;
        private Rect _layoutSafeArea;

        internal static CardRevealPanel Create(Transform parent, BoardController board)
        {
            var go = new GameObject("CardRevealPanel");
            go.transform.SetParent(parent, false);
            var panel = go.AddComponent<CardRevealPanel>();
            panel._board = board;
            panel.Build();
            go.SetActive(false);
            return panel;
        }

        private void Build()
        {
            // Camera-relative when there is a camera to hang off, world-anchored when there is not
            // (a stripped test scene). A missing camera must cost the panel its screen anchoring,
            // never its existence.
            Camera cam = Camera.main;
            if (cam != null)
            {
                transform.SetParent(cam.transform, false);
                transform.localPosition = new Vector3(0f, 0f, OverlayDistance);
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                transform.position = new Vector3(0f, 0f, -1.5f);
            }

            _solidSprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
                new Vector2(0.5f, 0.5f), Texture2D.whiteTexture.width);
            var dimGo = new GameObject("Dimmer");
            dimGo.transform.SetParent(transform, false);
            dimGo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            _dimmer = dimGo.AddComponent<SpriteRenderer>();
            _dimmer.sprite = _solidSprite;
            _dimmer.color = DimColor;
            _dimmer.sortingOrder = SortBase - 2;

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            faceGo.transform.localPosition = Vector3.zero;
            faceGo.transform.localScale = Vector3.one;
            _faceTransform = faceGo.transform;
            DestroyGenerated(faceGo.GetComponent<MeshCollider>()); // never eat a gesture
            var face = faceGo.GetComponent<Renderer>();
            _face = face;
            face.sortingOrder = SortBase;
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            face.SetPropertyBlock(mpb);

            float pad = BoardController.CardW * 0.12f;
            float inner = BoardController.CardW * 2.3f;

            // Sam, 2026-09-05: the plate's text read as tiny against how much room the plate has.
            // It was: the on-card label sizes (CardPresenter's NameEm/BodyEm * CardH * 10) copied
            // straight onto THIS box's fontSize/fontSizeMax/fontSizeMin without noticing this plate
            // is not a card — inner is 3.4026 world units wide against the on-card body box's
            // 1.292, a 2.63x difference nothing here accounted for. Values below are the old ones
            // rescaled by that factor, with autosizing added throughout so a short name/rules line
            // actually grows to fill the box instead of sitting at a card-sized glyph in a
            // plate-sized frame.

            _title = MakeText("Title", new Vector3(0f, 0f, -0.01f),
                              new Vector2(inner, BoardController.CardH * 0.27f), 3.0f, Color.white,
                              TextAlignmentOptions.Center, wrap: false);
            _title.enableAutoSizing = true;
            _title.fontSizeMax      = 3.0f;
            _title.fontSizeMin      = 1.8f; // NameEmFloor * CardH * 10 * PlateScale, rounded
            _title.overflowMode     = TextOverflowModes.Ellipsis;
            // The card NAME is the one string on this panel that is a title rather than prose, so
            // it takes the display face; everything below is the text face (EF-050's two roles).
            TMP_FontAsset display = BoardController.CardFont();
            if (display != null) _title.font = display;

            _subtitle = MakeText("Subtitle", new Vector3(0f, 0f, -0.01f),
                                 new Vector2(inner, BoardController.CardH * 0.19f), 1.8f,
                                 new Color(0.72f, 0.76f, 0.84f),
                                 TextAlignmentOptions.Center, wrap: false);
            _subtitle.enableAutoSizing = true;
            _subtitle.fontSizeMax      = 1.8f;
            _subtitle.fontSizeMin      = 1.0f;
            _subtitle.overflowMode     = TextOverflowModes.Ellipsis;

            _body = MakeText("Body", new Vector3(0f, 0f, -0.01f),
                             new Vector2(inner, BoardController.CardH * 1.10f), 2.4f,
                             new Color(0.94f, 0.94f, 0.94f),
                             TextAlignmentOptions.Top, wrap: true);
            // BodyEm/BodyEmFloor * CardH * 10 * PlateScale (0.90, 0.55 pre-rescale) — see PlateScale
            // above. The floor is still a real one for the longest shipped rules text; only the
            // scale it was applied at was wrong.
            _body.enableAutoSizing = true;
            _body.fontSizeMax      = 2.4f;
            _body.fontSizeMin      = 1.3f;
            _body.overflowMode     = TextOverflowModes.Ellipsis;

            ApplyLayout(force: true);
        }

        private void LateUpdate() => ApplyLayout(force: false);

        private void ApplyLayout(bool force)
        {
            Camera cam = Camera.main;
            int screenW = Mathf.Max(1, Screen.width);
            int screenH = Mathf.Max(1, Screen.height);
            Rect safe = Screen.safeArea;
            if (safe.width <= 1f || safe.height <= 1f)
                safe = new Rect(0f, 0f, screenW, screenH);
            if (!force && screenW == _layoutScreenWidth && screenH == _layoutScreenHeight
                       && safe == _layoutSafeArea) return;

            _layoutScreenWidth = screenW;
            _layoutScreenHeight = screenH;
            _layoutSafeArea = safe;

            float viewH;
            float viewW;
            if (cam != null)
            {
                if (transform.parent != cam.transform)
                    transform.SetParent(cam.transform, false);
                transform.localPosition = new Vector3(0f, 0f, OverlayDistance);
                transform.localRotation = Quaternion.identity;
                viewH = cam.orthographic
                    ? cam.orthographicSize * 2f
                    : 2f * OverlayDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                viewW = viewH * cam.aspect;
            }
            else
            {
                viewH = DeviceLayout.DesignHalfHeight * 2f;
                viewW = viewH * (float)screenW / screenH;
            }

            Rect safeViewport = Rect.MinMaxRect(
                safe.xMin / screenW, safe.yMin / screenH,
                safe.xMax / screenW, safe.yMax / screenH);
            CardRevealLayout layout = CardRevealLayout.Solve(viewW, viewH, safeViewport);

            if (_dimmer != null)
            {
                _dimmer.transform.localPosition = new Vector3(0f, 0f, 0.05f);
                _dimmer.transform.localScale = new Vector3(viewW, viewH, 1f);
            }
            if (_faceTransform != null)
            {
                _faceTransform.localPosition = new Vector3(layout.DetailCenter.x, layout.DetailCenter.y, 0f);
                _faceTransform.localScale = new Vector3(layout.DetailWidth, layout.CardHeight, 1f);
            }

            float pad = layout.DetailWidth * 0.07f;
            float inner = layout.DetailWidth - pad * 2f;
            _title.transform.localPosition = new Vector3(
                layout.DetailCenter.x, layout.DetailCenter.y + layout.CardHeight * 0.40f, -0.01f);
            _title.rectTransform.sizeDelta = new Vector2(inner, layout.CardHeight * 0.14f);
            _subtitle.transform.localPosition = new Vector3(
                layout.DetailCenter.x, layout.DetailCenter.y + layout.CardHeight * 0.28f, -0.01f);
            _subtitle.rectTransform.sizeDelta = new Vector2(inner, layout.CardHeight * 0.10f);
            _body.transform.localPosition = new Vector3(
                layout.DetailCenter.x, layout.DetailCenter.y - layout.CardHeight * 0.06f, -0.01f);
            _body.rectTransform.sizeDelta = new Vector2(inner, layout.CardHeight * 0.58f);

            float fontScale = layout.CardHeight / (BoardController.CardH * 1.90f);
            _title.fontSizeMax = 3.0f * fontScale;
            _title.fontSizeMin = 1.8f * fontScale;
            _subtitle.fontSizeMax = 1.8f * fontScale;
            _subtitle.fontSizeMin = 1.0f * fontScale;
            _body.fontSizeMax = 2.4f * fontScale;
            _body.fontSizeMin = 1.3f * fontScale;

            _focusX = layout.CardCenter.x;
            _focusY = layout.CardCenter.y;
            _focusScale = layout.CardHeight / BoardController.CardH;
            if (_focusPresenter != null)
            {
                Transform focus = _focusPresenter.transform;
                focus.localPosition = new Vector3(_focusX, _focusY, -0.03f);
                focus.localScale = Vector3.one * _focusScale;
            }
        }

        private TextMeshPro MakeText(string goName, Vector3 localPos, Vector2 size, float fontSize,
                                     Color color, TextAlignmentOptions align, bool wrap)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;

            var tmp = go.AddComponent<TextMeshPro>();
            go.GetComponent<Renderer>().sortingOrder = SortBase + 1;   // over the plate
            TMP_FontAsset body = BoardController.BodyFont();
            if (body != null) tmp.font = body;
            tmp.fontSize        = fontSize;
            tmp.alignment       = align;
            tmp.color           = color;
            tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = size;
            return tmp;
        }

        internal void Show(CardRevealTrigger trigger, CardRevealContent content)
        {
            if (_title == null) return;
            ApplyLayout(force: false);
            ApplyPlateColor();
            if (trigger != null && (!_hasFocusedCard || !_focusedCard.Equals(trigger.CardId)))
                RebuildFocusCard(trigger);
            _title.text    = content.Title    ?? "";
            _subtitle.text = content.Subtitle ?? "";
            _body.text     = content.Body     ?? "";
            gameObject.SetActive(true);
        }

        private void ApplyPlateColor()
        {
            if (_face == null) return;
            var mpb = new MaterialPropertyBlock();
            _face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            _face.SetPropertyBlock(mpb);
        }

        internal void Hide()
        {
            if (this != null) gameObject.SetActive(false);
        }

        private void RebuildFocusCard(CardRevealTrigger trigger)
        {
            ReleaseFocusCard();
            if (_board == null || trigger == null) return;

            _focusPresenter = _board.BuildFocusedCard(
                transform, trigger.Definition, trigger.LiveState);
            if (_focusPresenter == null) return;

            _focusedCard = trigger.CardId;
            _hasFocusedCard = true;
            Transform focus = _focusPresenter.transform;
            focus.localPosition = new Vector3(_focusX, _focusY, -0.03f);
            focus.localRotation = Quaternion.identity;
            focus.localScale = Vector3.one * _focusScale;
            var group = focus.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (group != null) group.sortingOrder = SortBase + 1;
        }

        private void ReleaseFocusCard()
        {
            _focusedCard = default;
            _hasFocusedCard = false;
            if (_focusPresenter == null) return;
            _focusPresenter.Unbind();
            GameObject old = _focusPresenter.gameObject;
            _focusPresenter = null;
            old.SetActive(false);
            if (Application.isPlaying) Destroy(old);
            else DestroyImmediate(old);
        }

        private void OnDestroy()
        {
            // The child is destroyed with this panel; only the live-state subscription needs an
            // explicit release here.
            if (_focusPresenter != null) _focusPresenter.Unbind();
            DestroyGenerated(_solidSprite);
        }

        private static void DestroyGenerated(Object generated)
        {
            if (generated == null) return;
            if (Application.isPlaying) Destroy(generated);
            else DestroyImmediate(generated);
        }
    }

    /// <summary>Pure screen-plane solve for the full-card inspect view.</summary>
    internal readonly struct CardRevealLayout
    {
        internal Vector2 CardCenter { get; }
        internal Vector2 DetailCenter { get; }
        internal float CardHeight { get; }
        internal float CardWidth { get; }
        internal float DetailWidth { get; }

        private CardRevealLayout(Vector2 cardCenter, Vector2 detailCenter,
                                 float cardHeight, float cardWidth, float detailWidth)
        {
            CardCenter = cardCenter;
            DetailCenter = detailCenter;
            CardHeight = cardHeight;
            CardWidth = cardWidth;
            DetailWidth = detailWidth;
        }

        internal static CardRevealLayout Solve(float viewWidth, float viewHeight, Rect safeViewport)
        {
            float safeW = viewWidth * Mathf.Clamp01(safeViewport.width);
            float safeH = viewHeight * Mathf.Clamp01(safeViewport.height);
            float centreX = viewWidth * (safeViewport.center.x - 0.5f);
            float centreY = viewHeight * (safeViewport.center.y - 0.5f);
            float margin = safeH * CardRevealPanel.OuterMarginFraction;
            float gap = safeH * CardRevealPanel.ColumnGapFraction;
            float usableW = Mathf.Max(0f, safeW - margin * 2f);
            float desiredCardH = safeH * CardRevealPanel.CardHeightFraction;
            float desiredCardW = desiredCardH * BoardController.CardW / BoardController.CardH;

            // Preserve enough room for readable rules text. Normal landscape screens keep the
            // full-height card; unusually narrow safe areas shrink it just enough to stay inside.
            float minimumDetailW = Mathf.Min(safeH * 0.42f, usableW * 0.42f);
            float maximumCardW = Mathf.Max(0f, usableW - gap - minimumDetailW);
            float cardW = Mathf.Min(desiredCardW, maximumCardW);
            float cardH = cardW * BoardController.CardH / BoardController.CardW;
            float availableDetail = Mathf.Max(0f, usableW - cardW - gap);
            float detailW = Mathf.Min(safeH * CardRevealPanel.DetailWidthFraction, availableDetail);
            float totalW = cardW + gap + detailW;
            float left = centreX - totalW * 0.5f;
            var cardCenter = new Vector2(left + cardW * 0.5f, centreY);
            var detailCenter = new Vector2(left + cardW + gap + detailW * 0.5f, centreY);
            return new CardRevealLayout(cardCenter, detailCenter, cardH, cardW, detailW);
        }
    }

    /// <summary>
    /// EF-049 follow-on. Flavor/lore line (CardDefinition.FlavorText), bottom-right of the screen —
    /// completely separate from both <see cref="CardRevealPanel"/>'s rules/glossary plate and
    /// CardPresenter's on-card Tooltip banner. BoardController owns the one shared instance and
    /// CardPresenter drives it from direct hover, keeping flavor text out of the enlarged focus
    /// view while avoiding duplicate screen-anchored panels.
    /// </summary>
    internal sealed class CardFlavorTextPanel : MonoBehaviour
    {
        private const float PanelW = BoardController.CardW * 1.9f;
        private const float PanelH = BoardController.CardH * 0.62f;
        private const int SortBase = 90;
        private const float PanelZ = -1.5f;

        // Bottom-right: positive X, negative Y from screen centre. Clear of the hand, which sits
        // bottom-CENTRE. Y centre -2.9, half-height ~0.51 -> bottom of -3.4, well inside the
        // +-4.6 safe half-height. The band above it (roughly y[-1.75, -0.65]) was reserved for the
        // reject-toast stack until #268 deleted that surface; it is free now, but this panel's
        // placement does not depend on it either way.
        private const float PanelX = 4.6f;
        private const float PanelY = -2.9f;

        private static readonly Color PlateColor = new Color(0.09f, 0.11f, 0.16f, 1f);

        private TextMeshPro _body;
        private Renderer _face;

        internal static CardFlavorTextPanel Create(Transform parent)
        {
            var go = new GameObject("CardFlavorTextPanel");
            go.transform.SetParent(parent, false);
            var panel = go.AddComponent<CardFlavorTextPanel>();
            panel.Build();
            go.SetActive(false);
            return panel;
        }

        private void Build()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                transform.SetParent(cam.transform, false);
                transform.localPosition = new Vector3(PanelX, PanelY, -cam.transform.position.z + PanelZ);
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                transform.position = new Vector3(PanelX, PanelY, PanelZ);
            }

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            faceGo.transform.localScale = new Vector3(PanelW, PanelH, 1f);
            DestroyGenerated(faceGo.GetComponent<MeshCollider>());
            var face = faceGo.GetComponent<Renderer>();
            _face = face;
            face.sortingOrder = SortBase;
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            face.SetPropertyBlock(mpb);

            float pad = PanelW * 0.10f;
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(transform, false);
            bodyGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _body = bodyGo.AddComponent<TextMeshPro>();
            bodyGo.GetComponent<Renderer>().sortingOrder = SortBase + 1;
            TMP_FontAsset body = BoardController.BodyFont();
            if (body != null) _body.font = body;
            _body.fontStyle       = FontStyles.Italic;
            _body.alignment       = TextAlignmentOptions.MidlineRight;
            _body.color           = new Color(0.80f, 0.82f, 0.88f);
            _body.textWrappingMode = TextWrappingModes.Normal;
            _body.rectTransform.sizeDelta = new Vector2(PanelW - pad * 2f, PanelH * 0.86f);
            _body.enableAutoSizing = true;
            _body.fontSizeMax      = 0.85f;
            _body.fontSizeMin      = 0.5f;
            _body.overflowMode     = TextOverflowModes.Ellipsis;
        }

        private static void DestroyGenerated(Object generated)
        {
            if (generated == null) return;
            if (Application.isPlaying) Destroy(generated);
            else DestroyImmediate(generated);
        }

        private object _owner;

        /// <summary>
        /// No-ops (stays hidden) for an empty flavor line — a blank plate in the corner would read
        /// as a bug, not as "this card has none".
        ///
        /// <para><paramref name="owner"/> is an opaque token (the calling CardPresenter, or null)
        /// recorded so a later <see cref="Hide"/> can tell a stale event from the live one — the
        /// same hazard <see cref="HoverRevealModel.PointerExit"/> guards against: Unity delivers
        /// enter/exit per collider with no ordering guarantee between two cards, so a stale exit
        /// from the card the pointer just LEFT must not blank the panel for the card it just
        /// ENTERED. The shared BoardController-owned surface therefore passes its CardPresenter as
        /// owner; callers with exclusive ownership may leave it null and use unconditional hide.</para>
        /// </summary>
        internal void Show(string flavorText, object owner = null)
        {
            _owner = owner;
            if (_body == null || string.IsNullOrEmpty(flavorText))
            {
                gameObject.SetActive(false);
                return;
            }
            ApplyPlateColor();
            _body.text = flavorText;
            gameObject.SetActive(true);
        }

        private void ApplyPlateColor()
        {
            if (_face == null) return;
            var mpb = new MaterialPropertyBlock();
            _face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            _face.SetPropertyBlock(mpb);
        }

        /// <summary>Unconditional when <paramref name="owner"/> is null; otherwise only hides if
        /// it still matches whoever's <see cref="Show"/> is currently up.</summary>
        internal void Hide(object owner = null)
        {
            if (owner != null && !Equals(owner, _owner)) return;
            _owner = null;
            if (this != null) gameObject.SetActive(false);
        }
    }
}
