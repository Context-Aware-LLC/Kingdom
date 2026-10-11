using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// EF-049. The in-play card focus view: a full-safe-height, live copy of the real card face.
    ///
    /// <para>The focused card is built through <see cref="BoardController.BuildFocusedCard"/>, so
    /// it is the shipping card face rather than a second visual interpretation.</para>
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
        internal const float CardHeightFraction = 1f;

        // Offsets from that plane, which only order the three layers: the card and its flavor
        // plate toward the camera, the dimmer behind them.
        private const float FocusZ = -0.03f;
        private const float DimmerZ = 0.05f;

        // Above the hand (HandSortBase 50, +20 while lifted) and below the match-end banner (100).
        private const int SortBase = 90;

        private static readonly Color DimColor = new Color(0.015f, 0.02f, 0.035f, 0.68f);

        private SpriteRenderer _dimmer;
        private Sprite _solidSprite;
        private BoardController _board;
        private CardPresenter _focusPresenter;
        private CardFlavorTextPanel _flavor;
        private InstanceId _focusedCard;
        private bool _hasFocusedCard;
        private ICardLiveState _focusedLive;
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
            dimGo.transform.localPosition = new Vector3(0f, 0f, DimmerZ);
            _dimmer = dimGo.AddComponent<SpriteRenderer>();
            _dimmer.sprite = _solidSprite;
            _dimmer.color = DimColor;
            _dimmer.sortingOrder = SortBase - 2;

            _flavor = CardFlavorTextPanel.Create(transform, SortBase);

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

            // The layout is solved on the plane at OverlayDistance, and under a perspective camera
            // a layer off that plane draws at a different size: left alone, the dimmer stops 2.4%
            // short of the screen edge and the card overruns the safe height by 1.5%. Each layer
            // is scaled back onto the solve.
            bool perspective = cam != null && !cam.orthographic;
            float near = perspective ? (OverlayDistance + FocusZ) / OverlayDistance : 1f;
            float far = perspective ? (OverlayDistance + DimmerZ) / OverlayDistance : 1f;

            if (_dimmer != null)
            {
                _dimmer.transform.localPosition = new Vector3(0f, 0f, DimmerZ);
                _dimmer.transform.localScale = new Vector3(viewW * far, viewH * far, 1f);
            }
            _focusX = layout.CardCenter.x * near;
            _focusY = layout.CardCenter.y * near;
            _focusScale = layout.CardHeight / BoardController.CardH * near;
            if (_focusPresenter != null)
            {
                Transform focus = _focusPresenter.transform;
                focus.localPosition = new Vector3(_focusX, _focusY, FocusZ);
                focus.localScale = Vector3.one * _focusScale;
            }
            if (_flavor != null)
            {
                Rect area = layout.FlavorArea;
                _flavor.Place(new Rect(area.x * near, area.y * near, area.width * near, area.height * near),
                              _focusScale, FocusZ);
            }
        }

        internal void Show(CardRevealTrigger trigger)
        {
            ApplyLayout(force: false);
            // The live state is compared as well as the id: a weapon's hand card and its equipped
            // socket share one InstanceId but not one live state, and a focus copy still bound to
            // the hand card's would show printed stats for the equipped weapon.
            if (trigger != null && (!_hasFocusedCard || !_focusedCard.Equals(trigger.CardId)
                                    || !ReferenceEquals(_focusedLive, trigger.LiveState)))
                RebuildFocusCard(trigger);
            gameObject.SetActive(true);
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
            _focusedLive = trigger.LiveState;
            Transform focus = _focusPresenter.transform;
            focus.localPosition = new Vector3(_focusX, _focusY, FocusZ);
            focus.localRotation = Quaternion.identity;
            focus.localScale = Vector3.one * _focusScale;
            var group = focus.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (group != null) group.sortingOrder = SortBase + 1;
            if (_flavor != null) _flavor.Show(trigger.Definition.FlavorText);
        }

        private void ReleaseFocusCard()
        {
            if (_flavor != null) _flavor.Hide();
            _focusedCard = default;
            _hasFocusedCard = false;
            _focusedLive = null;
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
        internal float CardHeight { get; }
        internal float CardWidth { get; }

        /// <summary>Where the flavor plate may draw: the column right of the card, rising from
        /// the card's foot. Zero when the screen leaves no readable column there (4:3 and
        /// narrower) — such a screen shows no flavor line rather than one laid over the card.</summary>
        internal Rect FlavorArea { get; }

        // All in card terms, so the plate keeps its proportion to the card on every screen.
        private const float FlavorGap = 0.04f;        // of card height: card to plate, plate to safe edge
        private const float FlavorFoot = 0.05f;       // of card height: plate foot above the card's
        private const float FlavorMaxHeight = 0.45f;  // of card height
        private const float FlavorMaxWidth = 1f;      // of card width
        private const float FlavorMinWidth = 0.4f;    // of card width

        private CardRevealLayout(Vector2 cardCenter, float cardHeight, float cardWidth, Rect flavorArea)
        {
            CardCenter = cardCenter;
            CardHeight = cardHeight;
            CardWidth = cardWidth;
            FlavorArea = flavorArea;
        }

        internal static CardRevealLayout Solve(float viewWidth, float viewHeight, Rect safeViewport)
        {
            float safeH = viewHeight * Mathf.Clamp01(safeViewport.height);
            float centreX = viewWidth * (safeViewport.center.x - 0.5f);
            float centreY = viewHeight * (safeViewport.center.y - 0.5f);
            float cardH = safeH * CardRevealPanel.CardHeightFraction;
            float cardW = cardH * BoardController.CardW / BoardController.CardH;

            float gap = cardH * FlavorGap;
            float flavorLeft = centreX + cardW * 0.5f + gap;
            float safeRight = viewWidth * (Mathf.Clamp01(safeViewport.xMax) - 0.5f);
            float flavorW = Mathf.Min(safeRight - gap - flavorLeft, cardW * FlavorMaxWidth);
            Rect flavor = flavorW >= cardW * FlavorMinWidth
                ? new Rect(flavorLeft, centreY - cardH * 0.5f + cardH * FlavorFoot,
                           flavorW, cardH * FlavorMaxHeight)
                : Rect.zero;
            return new CardRevealLayout(new Vector2(centreX, centreY), cardH, cardW, flavor);
        }
    }

    /// <summary>
    /// The flavor line (CardDefinition.FlavorText) of the card in the focus view: a small plate
    /// beside the focused card, lower right. The card face has no room for it at any size, and
    /// the focus view is the one read gesture that ends on touch as well as on a mouse — a plate
    /// driven by raw hover never came down on a touchscreen (#362).
    ///
    /// <para>A child of <see cref="CardRevealPanel"/>, laid out in card units and scaled with the
    /// focused card, so its type keeps one size against the card on every screen. No collider,
    /// for the reason the panel has none.</para>
    /// </summary>
    internal sealed class CardFlavorTextPanel : MonoBehaviour
    {
        // Card units. The focused card's own rules text is 1.0 at its largest.
        private const float FontMax = 0.9f;
        private const float FontMin = 0.6f;
        private const float PadX = 0.09f;
        private const float PadY = 0.07f;

        private static readonly Color PlateColor = new Color(0.09f, 0.11f, 0.16f, 1f);

        private TextMeshPro _body;
        private Renderer _face;
        private string _text = "";
        private float _width;
        private float _maxHeight;
        private bool _hasRoom;

        internal static CardFlavorTextPanel Create(Transform parent, int sortingOrder)
        {
            var go = new GameObject("CardFlavorTextPanel");
            go.transform.SetParent(parent, false);
            var panel = go.AddComponent<CardFlavorTextPanel>();
            panel.Build(sortingOrder);
            go.SetActive(false);
            return panel;
        }

        private void Build(int sortingOrder)
        {
            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            DestroyGenerated(faceGo.GetComponent<MeshCollider>());
            _face = faceGo.GetComponent<Renderer>();
            _face.sortingOrder = sortingOrder;
            MaterialCatalog.Assign(_face, MaterialCatalog.Plate);

            // Pivot at the bottom-left, so the plate grows up and right from the foot of its area
            // and a one-line flavor sits where the last line of a long one does.
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(transform, false);
            _body = bodyGo.AddComponent<TextMeshPro>();
            bodyGo.GetComponent<Renderer>().sortingOrder = sortingOrder + 1;
            TMP_FontAsset body = BoardController.BodyFont();
            if (body != null) _body.font = body;
            _body.rectTransform.pivot = Vector2.zero;
            _body.rectTransform.localPosition = new Vector3(PadX, PadY, -0.01f);
            _body.fontStyle        = FontStyles.Italic;
            _body.alignment        = TextAlignmentOptions.BottomLeft;
            _body.color            = new Color(0.80f, 0.82f, 0.88f);
            _body.textWrappingMode = TextWrappingModes.Normal;
            _body.enableAutoSizing = true;
            _body.fontSizeMax      = FontMax;
            _body.fontSizeMin      = FontMin;
            _body.overflowMode     = TextOverflowModes.Truncate;
        }

        private static void DestroyGenerated(Object generated)
        {
            if (generated == null) return;
            if (Application.isPlaying) Destroy(generated);
            else DestroyImmediate(generated);
        }

        /// <summary><paramref name="area"/> is in the focus panel's plane, <paramref name="z"/> its
        /// depth there, and <paramref name="scale"/> the focused card's, so everything inside is
        /// in card units. A zero area means the screen has no room beside the card and nothing
        /// is drawn.</summary>
        internal void Place(Rect area, float scale, float z = 0f)
        {
            _hasRoom = area.width > 0f && area.height > 0f && scale > 0f;
            if (_hasRoom)
            {
                transform.localPosition = new Vector3(area.xMin, area.yMin, z);
                transform.localScale = Vector3.one * scale;
                _width = area.width / scale;
                _maxHeight = area.height / scale;
            }
            Refresh();
        }

        /// <summary>An empty line hides the plate: a blank one beside the card would read as a
        /// bug, not as "this card has none".</summary>
        internal void Show(string flavorText)
        {
            _text = flavorText ?? "";
            Refresh();
        }

        internal void Hide() => Show("");

        private void Refresh()
        {
            if (this == null || _body == null || _face == null) return;
            bool visible = _hasRoom && _text.Length > 0;
            gameObject.SetActive(visible);
            if (!visible) return;

            float innerW = _width - PadX * 2f;
            float innerH = _maxHeight - PadY * 2f;
            _body.rectTransform.sizeDelta = new Vector2(innerW, innerH);
            _body.text = _text;
            // Measured now, and regardless of active state: the focus panel fills this in before
            // it activates itself, and the plate has to fit the line on the first frame it shows.
            _body.ForceMeshUpdate(true);

            // A line TMP could not measure reports a zero, NaN or inverted box; take the whole
            // area then, rather than draw a plate with no size.
            Vector3 drawn = _body.textBounds.size;
            float textW = !(drawn.x > 0f) ? innerW : Mathf.Min(drawn.x, innerW);
            float textH = !(drawn.y > 0f) ? innerH : Mathf.Min(drawn.y, innerH);
            float plateW = textW + PadX * 2f;
            float plateH = textH + PadY * 2f;
            _face.transform.localPosition = new Vector3(plateW * 0.5f, plateH * 0.5f, 0f);
            _face.transform.localScale = new Vector3(plateW, plateH, 1f);

            var mpb = new MaterialPropertyBlock();
            _face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            _face.SetPropertyBlock(mpb);
        }
    }
}
