using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. The reveal panel itself — a plate with three text blocks, built procedurally from
    /// primitives like RejectToastView and the targeting banner.
    ///
    /// <para><b>A panel, not an enlarged card.</b> The card blow-up (a card flying out of the hand
    /// and growing) is explicitly parked: it has to overlap the board, sort above everything,
    /// animate back and survive interruption mid-sweep. None of that is needed to make a card
    /// READABLE, which is the gap. A panel also carries what an enlarged card still could not — the
    /// glossary line for each keyword, which never fits on the face at any size.</para>
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

        internal static CardRevealPanel Create(Transform parent)
        {
            var go = new GameObject("CardRevealPanel");
            go.transform.SetParent(parent, false);
            var panel = go.AddComponent<CardRevealPanel>();
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
            faceGo.transform.localScale = new Vector3(PanelW, PanelH, 1f);
            Destroy(faceGo.GetComponent<MeshCollider>());   // see class doc: never eat a gesture
            var face = faceGo.GetComponent<Renderer>();
            face.sortingOrder = SortBase;
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", PlateColor);
            face.SetPropertyBlock(mpb);

            float pad = PanelW * 0.06f;
            float inner = PanelW - pad * 2f;

            _title = MakeText("Title", new Vector3(0f, PanelH * 0.40f, -0.01f),
                              new Vector2(inner, PanelH * 0.14f), 1.5f, Color.white,
                              TextAlignmentOptions.Center, wrap: false);
            // The card NAME is the one string on this panel that is a title rather than prose, so
            // it takes the display face; everything below is the text face (EF-050's two roles).
            TMP_FontAsset display = BoardController.CardFont();
            if (display != null) _title.font = display;

            _subtitle = MakeText("Subtitle", new Vector3(0f, PanelH * 0.28f, -0.01f),
                                 new Vector2(inner, PanelH * 0.10f), 0.85f,
                                 new Color(0.72f, 0.76f, 0.84f),
                                 TextAlignmentOptions.Center, wrap: false);

            _body = MakeText("Body", new Vector3(0f, -PanelH * 0.06f, -0.01f),
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

        internal void Show(CardRevealContent content)
        {
            if (_title == null) return;
            _title.text    = content.Title    ?? "";
            _subtitle.text = content.Subtitle ?? "";
            _body.text     = content.Body     ?? "";
            gameObject.SetActive(true);
        }

        internal void Hide()
        {
            if (this != null) gameObject.SetActive(false);
        }
    }
}
