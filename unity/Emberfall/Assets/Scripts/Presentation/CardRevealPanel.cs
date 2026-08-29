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
        // Sized in card widths rather than in camera fractions, so the panel keeps a fixed
        // relationship to the thing it is describing across every aspect ratio the phone build
        // has to survive.
        private const float PanelW = BoardController.CardW * 2.55f;
        private const float PanelH = BoardController.CardH * 1.90f;
        private const float DetailX = 1.75f;
        private const float FocusX = -2.15f;
        private const float FocusScale = 2.10f;

        // Above the hand (HandSortBase 50, +20 while lifted) and below the match-end banner (100).
        private const int SortBase = 90;

        // Card materials are opaque, so draw order alone loses to the depth buffer. World z is what
        // actually puts the panel in front; -1.5 clears the hand fan's stagger and its hover pull.
        private const float PanelZ = -1.5f;

        // Up from centre: on a phone the hand is at the bottom under the player's own hand, and
        // the reveal is worthless if a thumb is resting on it.
        private const float PanelY = 0.55f;

        private static readonly Color PlateColor = new Color(0.09f, 0.11f, 0.16f, 1f);

        private TextMeshPro _title;
        private TextMeshPro _subtitle;
        private TextMeshPro _body;
        private Renderer _face;
        private BoardController _board;
        private CardPresenter _focusPresenter;
        private InstanceId _focusedCard;
        private bool _hasFocusedCard;

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
                transform.localPosition = new Vector3(0f, PanelY, -cam.transform.position.z + PanelZ);
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                transform.position = new Vector3(0f, PanelY, PanelZ);
            }

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            faceGo.transform.localPosition = new Vector3(DetailX, 0f, 0f);
            faceGo.transform.localScale = new Vector3(PanelW, PanelH, 1f);
            DestroyGenerated(faceGo.GetComponent<MeshCollider>()); // never eat a gesture
            var face = faceGo.GetComponent<Renderer>();
            _face = face;
            face.sortingOrder = SortBase;
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            face.SetPropertyBlock(mpb);

            float pad = PanelW * 0.06f;
            float inner = PanelW - pad * 2f;

            _title = MakeText("Title", new Vector3(DetailX, PanelH * 0.40f, -0.01f),
                              new Vector2(inner, PanelH * 0.14f), 1.5f, Color.white,
                              TextAlignmentOptions.Center, wrap: false);
            // The card NAME is the one string on this panel that is a title rather than prose, so
            // it takes the display face; everything below is the text face (EF-050's two roles).
            TMP_FontAsset display = BoardController.CardFont();
            if (display != null) _title.font = display;

            _subtitle = MakeText("Subtitle", new Vector3(DetailX, PanelH * 0.28f, -0.01f),
                                 new Vector2(inner, PanelH * 0.10f), 0.85f,
                                 new Color(0.72f, 0.76f, 0.84f),
                                 TextAlignmentOptions.Center, wrap: false);

            _body = MakeText("Body", new Vector3(DetailX, -PanelH * 0.06f, -0.01f),
                             new Vector2(inner, PanelH * 0.58f), 0.90f,
                             new Color(0.94f, 0.94f, 0.94f),
                             TextAlignmentOptions.Top, wrap: true);
            // Autosize floors at 0.55: below that the body drops under the acuity margin the
            // 265 px phone card already runs at, and an unreadable panel is worse than a clipped
            // one because nothing tells the player they are missing a line.
            _body.enableAutoSizing = true;
            _body.fontSizeMax      = 0.90f;
            _body.fontSizeMin      = 0.55f;
            _body.overflowMode     = TextOverflowModes.Ellipsis;
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
            focus.localPosition = new Vector3(FocusX, 0f, -0.03f);
            focus.localRotation = Quaternion.identity;
            focus.localScale = Vector3.one * FocusScale;
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
        }

        private static void DestroyGenerated(Object generated)
        {
            if (generated == null) return;
            if (Application.isPlaying) Destroy(generated);
            else DestroyImmediate(generated);
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
