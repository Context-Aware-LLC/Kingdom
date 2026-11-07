using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The versus / match-load screen (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/versus-screen.md). Split from
    /// <see cref="LoadingOverlay"/>: the boot plate is a brand splash for app startup, this is
    /// the per-match "who am I fighting" card shown between matched-and-board-ready.
    ///
    /// LIVE as of 2026-07-24 (spec §10 item 4a-bis): both bootstraps raise it the moment the two
    /// hero classes are known, and it fades on the first released snapshot. The boot plate is NOT
    /// replaced — it still covers frame 0, and in the networked boot it must, because the classes
    /// only arrive with <c>match_start</c> and the pairing wait before that is unbounded. So the
    /// order is boot plate (→ "Waiting for an opponent…") → THIS → board. When MainMenu becomes the
    /// entry scene the boot plate stops appearing here and the split in spec §1 is real.
    ///
    /// Three tiers (spec §3): the baked class-neutral landscape (with both medallion frames
    /// painted in) → per-match hero medallions + clash orb → text. The landscape and frames are
    /// in the plate; only the portraits, orb and text are inserted here.
    ///
    /// Like LoadingOverlay this is a camera-parented world-space rig, all overlays anchored to
    /// screen centre and sized from screen HEIGHT so the height-anchored plate never crops
    /// (spec §5). Reveal fades everything in together; NotifyMatchReady() fades it back out.
    /// </summary>
    public sealed class VersusOverlay : MonoBehaviour
    {
        /// <summary>Everything the screen needs, resolved by the caller from the live match.
        /// Kept pure-presentation so the rig renders identically from stub data (verify pass).</summary>
        public struct Data
        {
            public CardClass LocalClass, RemoteClass;
            public string LocalName, RemoteName;
            /// <summary>The two players shown over the shared StormGlass loading indicator.</summary>
            public static Data For(CardClass localClass, string localName,
                                   CardClass remoteClass, string remoteName) => new Data
            {
                LocalClass  = localClass,
                RemoteClass = remoteClass,
                LocalName   = localName,
                RemoteName  = remoteName,
            };
        }

        /// <summary>Longest nameplate we render. Past this the string is clipped with an ellipsis —
        /// the slot is a fixed-width box under a medallion, not a scroller.</summary>
        private const int MaxNameChars = 20;

        // ── Slot geometry as fractions of screen height (spec §4/§5).
        //    The plate is centred and height-scaled, so these land on the painted frames at any aspect.
        // Measured on Sam's 1664×936 crop of the regenerated Causeway (2026-11-06).
        // Both exported tiers preserve these normalized positions. The 454px mask includes
        // its feather and covers the parchment just inside the decorative gold rims.
        private const float MedallionDiaFrac  = 454f / 936f;
        private const float LeftMedallionXFrac  = (418f - 832f) / 936f;
        private const float RightMedallionXFrac = (1246f - 832f) / 936f;
        private const float LeftMedallionYFrac  = (468f - 312f) / 936f;
        private const float RightMedallionYFrac = (468f - 312f) / 936f;
        private const float OrbYFrac            = (468f - 312f) / 936f;
        private const float OrbDiaFrac       = 400f / 2160f;    // Ø400 ≈ 18.5%
        private const float NameplateYFrac   = -390f / 2160f;   // canvas 1470 — clears the frame's bottom lip
        private const float ClassYFrac       = -520f / 2160f;   // canvas 1600
        private const float RankYFrac        = -590f / 2160f;   // ≈27.3% below — reserved, blank
        private const float StatusYFrac      = -(1850f - 1080f) / 2160f; // loading-plate slot verbatim (§4)

        // ── Type scale. TextMeshPro's fontSize on a world-space text is a POINT value: at scale 1
        //    the em box is fontSize/10 world units, NOT fontSize. These slots were first authored as
        //    if the coefficient were a height fraction, which shipped every string ~10× under —
        //    "Emberward" and the status line rendered at 5–7px on a 1080p frame, below legibility.
        //    Sized in em-as-fraction-of-height and multiplied by 10 (tooling/scripts/versus_text_mock.py
        //    prints the conversion). Signed off in image-space by Sam, 2026-07-24.
        private const float NameSizeFrac   = 0.460f;   // em ≈ 4.6% of height (50px @1080)
        private const float ClassSizeFrac  = 0.280f;   // em ≈ 2.8% (30px @1080)
        private const float StatusSizeFrac = 0.240f;   // em ≈ 2.4% (26px @1080)

        // Tracking, in TMP's em/100 units. The class band and status are small caps-adjacent labels
        // and read as deliberate spaced; the nameplate is set solid.
        private const float ClassTracking  = 14f;
        private const float StatusTracking = 6f;

        // ── Sorting. Deliberately ABOVE LoadingOverlay's 300/310/320 band: the two overlays are
        //    both alive during the handover (the boot plate is released as this one is built, and
        //    cross-fades out underneath it). At equal sortingOrder that overlap is undefined.
        private const int SortBase = 400;

        // Reveal/fade pacing. MinHold is measured from BUILD, not from reveal-complete, so the
        // screen is fully up for MinHold − RevealDuration before it may start leaving. The board
        // is usually ready long before that, which is the point: this is a beat the player reads,
        // not a progress bar. Total on-screen ≈ MinHold + FadeOutDuration.
        private const float RevealDuration  = 0.5f;
        private const float MinHold         = 2.0f;
        private const float FadeOutDuration = 0.5f;

        private static readonly Color NameTint   = new Color(0.16f, 0.12f, 0.08f);
        private static readonly Color ClassTint  = new Color(0.15f, 0.20f, 0.28f);
        private static readonly Color StatusTint = new Color(0.20f, 0.16f, 0.11f);
        private static readonly Color ErrorTint  = new Color(0.65f, 0.10f, 0.07f);

        private static readonly int FadeId    = Shader.PropertyToID("_Fade");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ShaderUtilOutlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly int ShaderUtilOutlineWidth = Shader.PropertyToID("_OutlineWidth");
        private static readonly int UnderlayColor    = Shader.PropertyToID("_UnderlayColor");
        private static readonly int UnderlayOffsetX  = Shader.PropertyToID("_UnderlayOffsetX");
        private static readonly int UnderlayOffsetY  = Shader.PropertyToID("_UnderlayOffsetY");
        private static readonly int UnderlayDilate   = Shader.PropertyToID("_UnderlayDilate");
        private static readonly int UnderlaySoftness = Shader.PropertyToID("_UnderlaySoftness");

        private SpriteRenderer _plate;
        private SpriteRenderer _matte;
        private Material _orbMat;                       // instanced — _Fade is per-overlay
        private Transform _orbRoot;
        private Vector3 _orbScale;
        private readonly System.Collections.Generic.List<Material> _medallionMats = new();
        private readonly System.Collections.Generic.List<TextMeshPro> _texts = new();
        private TextMeshPro _status;
        private IArtProvider _artProvider;

        private float _t0;
        private bool _matchReady;
        private bool _failed;
        private float _fadeOutT = -1f;
        private bool _destroyed;

        /// <summary>Build the rig under <paramref name="cam"/>. Null camera → null (skipped, boot unaffected).</summary>
        public static VersusOverlay Create(Camera cam, Data data, IArtProvider artProvider = null)
        {
            if (cam == null)
            {
                Debug.LogWarning("[VersusOverlay] no camera — overlay skipped.");
                return null;
            }
            var go = new GameObject("VersusOverlay");
            go.transform.SetParent(cam.transform, false);
            float dist = cam.nearClipPlane + 1f;
            go.transform.localPosition = new Vector3(0f, 0f, dist);
            var overlay = go.AddComponent<VersusOverlay>();
            overlay._artProvider = artProvider;
            overlay.Build(cam, dist, data);
            return overlay;
        }

        /// <summary>The match is ready to show (board built). Fades the screen out after the
        /// minimum hold; idempotent.</summary>
        public void NotifyMatchReady()
        {
            if (_destroyed) return;
            _matchReady = true;
        }

        /// <summary>Pairing/boot could not complete. The screen stays up and shows the reason.</summary>
        public void NotifyBootFailed(string reason)
        {
            if (_destroyed) return;
            _failed = true;
            SetStatus(reason);
            if (_status != null) _status.color = ErrorTint;
        }

        /// <summary>Status / tip line — live connection + pairing state, as the loading plate feeds it.</summary>
        public void SetStatus(string text)
        {
            if (_destroyed || _status == null) return;
            _status.text = text;
        }

        private void Build(Camera cam, float dist, Data data)
        {
            _t0 = Time.time;
            float h = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            // Fit the complete composition on 4:3 tablets as well as wide phones. A cover crop
            // would cut through the outer edges of both painted portrait apertures on tablets.
            var matteObject = new GameObject("Versus Matte");
            matteObject.transform.SetParent(transform, false);
            _matte = matteObject.AddComponent<SpriteRenderer>();
            Texture2D white = Texture2D.whiteTexture;
            _matte.sprite = Sprite.Create(white, new Rect(0, 0, white.width, white.height),
                new Vector2(0.5f, 0.5f), white.width);
            _matte.transform.localScale = new Vector3(h * cam.aspect, h, 1f);
            _matte.sortingOrder = SortBase - 1;
            _matte.color = new Color(0.035f, 0.025f, 0.045f, 0f);
            h *= Mathf.Min(1f, cam.aspect / (16f / 9f));

            // ── Plate — class-neutral landscape with both frames painted in. SpriteRenderer so the
            //    default sprite material honors alpha (LoadingOverlay precedent). Both delivery
            //    tiers share the same composition and normalized aperture measurements above.
            var plateTex = BackgroundArt.LoadTexture("VersusPlate_Causeway");
            if (plateTex != null)
            {
                var pgo = new GameObject("Plate");
                pgo.transform.SetParent(transform, false);
                _plate = pgo.AddComponent<SpriteRenderer>();
                _plate.sprite = Sprite.Create(plateTex,
                    new Rect(0, 0, plateTex.width, plateTex.height),
                    new Vector2(0.5f, 0.5f), pixelsPerUnit: plateTex.height / h);
                _plate.sortingOrder = SortBase;
            }

            // ── Hero medallions — portraits masked to the painted apertures. Local LEFT, remote
            //    RIGHT (this screen's own convention; the board's local-at-bottom rule doesn't apply).
            float medDia = MedallionDiaFrac * h;
            BuildMedallion(data.LocalClass,  LeftMedallionXFrac * h,  LeftMedallionYFrac * h,  medDia, h, "MedallionLocal");
            BuildMedallion(data.RemoteClass, RightMedallionXFrac * h, RightMedallionYFrac * h, medDia, h, "MedallionRemote");

            // ── StormGlass heartbeat, level with the medallion centres.
            var orbShared = MaterialCatalog.Get("M_LoadingOrb");
            if (orbShared != null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Orb";
                Destroy(quad.GetComponent<Collider>());
                quad.transform.SetParent(transform, false);
                quad.transform.localPosition = new Vector3(0f, OrbYFrac * h, -0.01f);
                float od = OrbDiaFrac * h;
                _orbRoot = quad.transform;
                _orbScale = new Vector3(od, od, 1f);
                quad.transform.localScale = _orbScale;
                _orbMat = new Material(orbShared);

                quad.GetComponent<MeshRenderer>().sharedMaterial = _orbMat;
                quad.GetComponent<MeshRenderer>().sortingOrder = SortBase + 10;
            }

            // ── Text. Nameplate + class per player, re-centred under each medallion column (spec §5).
            //    Rank is reserved (§4.4) and mode has no system yet (§6) — both render nothing; the
            //    layout must still read deliberate with them blank.
            BuildText($"NameLocal",  SanitizeName(data.LocalName),  LeftMedallionXFrac * h,  NameplateYFrac * h, NameSizeFrac * h, NameTint,  h, bold: true, plainText: true);
            BuildText($"NameRemote", SanitizeName(data.RemoteName), RightMedallionXFrac * h, NameplateYFrac * h, NameSizeFrac * h, NameTint,  h, bold: true, plainText: true);
            BuildText($"ClassLocal",  ClassName(data.LocalClass),  LeftMedallionXFrac * h,  ClassYFrac * h, ClassSizeFrac * h, ClassTint, h, tracking: ClassTracking, underlay: true);
            BuildText($"ClassRemote", ClassName(data.RemoteClass), RightMedallionXFrac * h, ClassYFrac * h, ClassSizeFrac * h, ClassTint, h, tracking: ClassTracking, underlay: true);

            // ── Status / tip — centred, live. Empty until SetStatus; doubles as the failure surface.
            _status = BuildText("StatusLine", "", 0f, StatusYFrac * h, StatusSizeFrac * h, StatusTint, h, tracking: StatusTracking);
            _status.rectTransform.sizeDelta = new Vector2(1.6f * h, 0.16f * h);
        }

        private void BuildMedallion(CardClass heroClass, float x, float y, float dia, float h, string name)
        {
            var shader = Shader.Find("Kingdom/SG_MedallionMask");
            if (shader == null) return;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.SetActive(false);
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(transform, false);
            quad.transform.localPosition = new Vector3(x, y, -0.008f);
            quad.transform.localScale = new Vector3(dia, dia, 1f);

            var mat = new Material(shader);
            _medallionMats.Add(mat);

            var rend = quad.GetComponent<MeshRenderer>();
            rend.sharedMaterial = mat;
            rend.sortingOrder = SortBase + 5;   // above the plate, below the orb

            _artProvider?.LoadArt($"hero_{heroClass.ToString().ToLowerInvariant()}", sprite =>
            {
                if (_destroyed || sprite == null) return;
                mat.SetTexture(MainTexId, sprite.texture);
                quad.SetActive(true);
            });
        }

        /// <summary>
        /// The nameplate is the only string on this screen that does NOT originate in our own code —
        /// it comes off the wire (<c>NetworkedMatchSession.OpponentName</c>). Today the server sets it
        /// to the opponent's server-assigned <c>PlayerId</c>, so it is trusted (dev-docs/quality/security.md:24,
        /// VULN-001 §2 fact 3); this exists so that the day a player picks their own name, the
        /// renderer is not the thing that has to be remembered.
        ///
        /// Two hazards, both handled here and neither a substitute for validating server-side:
        /// TMP parses <b>rich-text markup</b> in the string body (an opponent named
        /// <c>&lt;size=400%&gt;</c> would otherwise redraw the layout on YOUR machine — see
        /// <paramref name="plainText"/> on <see cref="BuildText"/>, which is what actually disarms
        /// that), and an unbounded name would run out of its box. Control characters are dropped
        /// rather than escaped: nothing legitimate contains them.
        /// </summary>
        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= MaxNameChars) { sb.Append('…'); break; }
            }
            return sb.ToString();
        }

        private TextMeshPro BuildText(string name, string text, float x, float y, float size, Color tint, float h,
                                      bool bold = false, float tracking = 0f, bool underlay = false,
                                      bool plainText = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(x, y, -0.02f);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.rectTransform.sizeDelta = new Vector2(MedallionDiaFrac * h * 0.92f, 0.16f * h); // < box width (§5)
            tmp.fontSize = size;
            tmp.characterSpacing = tracking;
            tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            BoardController.ApplyUiFont(tmp);   // before fontMaterial below clones the face's material
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            // richText off for anything not authored here — see SanitizeName. This is the line that
            // actually neutralises markup in a wire-supplied name; the character filter only trims it.
            //
            // Ellipsis is the other half, and it is a DIFFERENT guarantee from SanitizeName's cap
            // (VULN-002 §4.4). That cap counts CHARACTERS; this bounds PIXELS against the rect above,
            // and 20 of "WWWWWWWWWWWWWWWWWWWW" is not 20 of "iiiiiiiiiiiiiiiiiiii". Without it TMP's
            // default Overflow draws the name straight out of its plate and across the board — a
            // playable-area denial that no length rule can prevent. Do not drop one for the other.
            if (plainText)
            {
                tmp.richText = false;
                tmp.parseCtrlCharacters = false;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
            }
            tmp.color = new Color(tint.r, tint.g, tint.b, 0f);   // fades in with the rig
            tmp.sortingOrder = SortBase + 20;
            tmp.text = text;
            // Dark ink with a warm light edge reads across the sunlit stone and garden shadows.
            // Per-instance material via fontMaterial keeps the treatment local to this screen.
            var mat = tmp.fontMaterial;
            if (underlay)
            {
                // TMP's SDF outline grows INWARD as well as outward, so on a small 30px face it eats
                // the stroke. An underlay sits wholly BEHIND the glyph, separating the class label
                // from the textured gardens without losing its weight.
                mat.DisableKeyword("OUTLINE_ON");
                mat.SetFloat(ShaderUtilOutlineWidth, 0f);
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor(UnderlayColor, new Color(1f, 0.97f, 0.88f, 0.9f));
                mat.SetFloat(UnderlayOffsetX, 0f);
                mat.SetFloat(UnderlayOffsetY, -0.25f);
                mat.SetFloat(UnderlayDilate, 0.15f);
                mat.SetFloat(UnderlaySoftness, 0.35f);
            }
            else
            {
                mat.EnableKeyword("OUTLINE_ON");
                mat.SetColor(ShaderUtilOutlineColor, new Color(1f, 0.97f, 0.88f, 0.9f));
                mat.SetFloat(ShaderUtilOutlineWidth, 0.18f);
            }
            _texts.Add(tmp);
            return tmp;
        }

        private void Update()
        {
            float t = Time.time - _t0;
            float reveal = EaseOutCubic(Mathf.Clamp01(t / RevealDuration));

            if (_fadeOutT < 0f && reveal >= 1f && t >= MinHold && _matchReady && !_failed)
                _fadeOutT = t;
            float fadeOut = _fadeOutT < 0f ? 1f : 1f - Mathf.Clamp01((t - _fadeOutT) / FadeOutDuration);
            float a = reveal * fadeOut;

            if (_plate != null) _plate.color = new Color(1f, 1f, 1f, a);
            if (_matte != null) _matte.color = new Color(0.035f, 0.025f, 0.045f, a);
            LoadingIndicator.UpdateWorld(_orbMat, _orbRoot, _orbScale, a);
            foreach (var m in _medallionMats) m.SetFloat(FadeId, a);
            foreach (var tmp in _texts)
            {
                if (tmp == null) continue;
                Color c = tmp.color;
                c.a = a;
                tmp.color = c;
            }

            if (_fadeOutT >= 0f && fadeOut <= 0f)
                Destroy(gameObject);
        }

        private static string ClassName(CardClass c) => GameTerms.Class(c);

        private static float EaseOutCubic(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        private void OnDestroy()
        {
            _destroyed = true;
            if (_plate != null && _plate.sprite != null) Destroy(_plate.sprite);
            if (_matte != null && _matte.sprite != null) Destroy(_matte.sprite);
            if (_orbMat != null) Destroy(_orbMat);
            foreach (var m in _medallionMats) if (m != null) Destroy(m);
        }

    }
}
