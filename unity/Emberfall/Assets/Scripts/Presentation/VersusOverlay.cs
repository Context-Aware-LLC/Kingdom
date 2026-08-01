using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
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
            public int OrbVariant;   // 0..2, from OrbVariantForMatch(matchId)

            /// <summary>The normal construction: names as given, orb derived from the match id so
            /// both clients land on the same variant (spec §4.2).</summary>
            public static Data For(CardClass localClass, string localName,
                                   CardClass remoteClass, string remoteName, string matchId) => new Data
            {
                LocalClass  = localClass,
                RemoteClass = remoteClass,
                LocalName   = localName,
                RemoteName  = remoteName,
                OrbVariant  = OrbVariantForMatch(matchId),
            };
        }

        /// <summary>Longest nameplate we render. Past this the string is clipped with an ellipsis —
        /// the slot is a fixed-width box under a medallion, not a scroller.</summary>
        private const int MaxNameChars = 20;

        // ── Slot geometry as fractions of screen height, from the 5040×2160 canvas (spec §4/§5).
        //    The plate is centred and height-scaled, so these land on the painted frames at any aspect.
        private const float PlateAspect      = 5040f / 2160f;
        // MEASURED per-frame from the generated plate (Causeway), NOT the spec's nominal geometry:
        // the frames were painted by the image model, so each aperture centre is read off the plate
        // and the two are wired INDEPENDENTLY — the plate is asymmetric and the symmetric ±X
        // assumption misplaced them (Sam measured each in GIMP, 2026-07-24). Canvas centres:
        // Pyra/left (1480,730), Maren/right (3544,732). Ø1200 overfills the irregular aperture so
        // the portrait tucks under the frame's inner lip with no gap ring.
        private const float MedallionDiaFrac  = 1200f / 2160f;           // Ø1200 ≈ 55.6% of height
        private const float LeftMedallionXFrac  = (1480f - 2520f) / 2160f;  // -1040 (canvas 1480)
        private const float RightMedallionXFrac = (3544f - 2520f) / 2160f;  // +1024 (canvas 3544)
        private const float LeftMedallionYFrac  = (1080f - 730f) / 2160f;   // 350 above centre
        private const float RightMedallionYFrac = (1080f - 732f) / 2160f;   // 348 above centre
        private const float OrbYFrac            = (1080f - 731f) / 2160f;    // 349, level with the medallions
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

        private static readonly Color NameTint   = new Color(0.96f, 0.93f, 0.86f);
        private static readonly Color ClassTint  = new Color(0.78f, 0.82f, 0.90f);
        private static readonly Color StatusTint = new Color(0.92f, 0.88f, 0.82f);
        private static readonly Color ErrorTint  = new Color(1.00f, 0.55f, 0.45f);

        private static readonly string[] OrbNames = { "StormGlass", "Confluence", "HollowCore" };

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
        private Material _orbMat;                       // instanced — _Fade is per-overlay
        private readonly System.Collections.Generic.List<Material> _medallionMats = new();
        private readonly System.Collections.Generic.List<TextMeshPro> _texts = new();
        private TextMeshPro _status;

        private float _t0;
        private bool _matchReady;
        private bool _failed;
        private float _fadeOutT = -1f;
        private bool _destroyed;

        /// <summary>Deterministic orb pick from the match id — both clients must land on the same
        /// variant (spec §4.2: a per-client roll would show the two players different orbs). Stable
        /// FNV-1a rather than string.GetHashCode, which is not guaranteed stable across runtimes.</summary>
        public static int OrbVariantForMatch(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return 0;
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in matchId) { h ^= ch; h *= 16777619u; }
                return (int)(h % (uint)OrbNames.Length);
            }
        }

        /// <summary>Build the rig under <paramref name="cam"/>. Null camera → null (skipped, boot unaffected).</summary>
        public static VersusOverlay Create(Camera cam, Data data)
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

            // ── Plate — class-neutral landscape with both frames painted in. SpriteRenderer so the
            //    default sprite material honors alpha (LoadingOverlay precedent). In Art/Backgrounds/
            //    so CardArtPostprocessor imports it full-res (4096/2048), not the 2048 default.
            var plateTex = LoadAsset<Texture2D>(
                "Assets/Art/Backgrounds/VersusPlate_Causeway.png", "backgrounds/VersusPlate_Causeway");
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

            // ── Clash orb — the one live element, level with the medallion centres. Instanced
            //    M_LoadingOrb with the per-match variant swapped onto _MainTex.
            var orbShared = MaterialCatalog.Get("M_LoadingOrb");
            if (orbShared != null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Orb";
                Destroy(quad.GetComponent<Collider>());
                quad.transform.SetParent(transform, false);
                quad.transform.localPosition = new Vector3(0f, OrbYFrac * h, -0.01f);
                float od = OrbDiaFrac * h;
                quad.transform.localScale = new Vector3(od, od, 1f);
                _orbMat = new Material(orbShared);

                int v = Mathf.Clamp(data.OrbVariant, 0, OrbNames.Length - 1);
                var orbTex = LoadAsset<Texture2D>(
                    $"Assets/Art/Loading/Orb_{OrbNames[v]}.png", $"loading/Orb_{OrbNames[v]}");
                if (orbTex != null) _orbMat.SetTexture(MainTexId, orbTex);

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
            var shader = Shader.Find("Emberfall/SG_MedallionMask");
            var heroTex = LoadAsset<Texture2D>(
                $"Assets/Art/Hero/art_hero_{heroClass.ToString().ToLowerInvariant()}.png",
                $"art/hero_{heroClass.ToString().ToLowerInvariant()}");
            if (shader == null || heroTex == null) return;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(transform, false);
            quad.transform.localPosition = new Vector3(x, y, -0.008f);
            quad.transform.localScale = new Vector3(dia, dia, 1f);

            var mat = new Material(shader);
            mat.SetTexture(MainTexId, heroTex);
            _medallionMats.Add(mat);

            var rend = quad.GetComponent<MeshRenderer>();
            rend.sharedMaterial = mat;
            rend.sortingOrder = SortBase + 5;   // above the plate, below the orb
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
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            // richText off for anything not authored here — see SanitizeName. This is the line that
            // actually neutralises markup in a wire-supplied name; the character filter only trims it.
            if (plainText) { tmp.richText = false; tmp.parseCtrlCharacters = false; }
            tmp.color = new Color(tint.r, tint.g, tint.b, 0f);   // fades in with the rig
            tmp.sortingOrder = SortBase + 20;
            tmp.text = text;
            // Dark outline so text reads over the busy ruins band (the plate leaves these bands
            // unlit per §8, but the ground is textured). Per-instance material via fontMaterial.
            var mat = tmp.fontMaterial;
            if (underlay)
            {
                // TMP's SDF outline grows INWARD as well as outward, so on a light 30px face it eats
                // the stroke — the class band rendered thinner and paler than the design mock, the
                // outline costing the very weight it was added to protect. An underlay sits wholly
                // BEHIND the glyph: same separation from the textured ruins, no stroke lost.
                mat.DisableKeyword("OUTLINE_ON");
                mat.SetFloat(ShaderUtilOutlineWidth, 0f);
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor(UnderlayColor, new Color(0f, 0f, 0f, 0.75f));
                mat.SetFloat(UnderlayOffsetX, 0f);
                mat.SetFloat(UnderlayOffsetY, -0.25f);
                mat.SetFloat(UnderlayDilate, 0.15f);
                mat.SetFloat(UnderlaySoftness, 0.35f);
            }
            else
            {
                mat.EnableKeyword("OUTLINE_ON");
                mat.SetColor(ShaderUtilOutlineColor, new Color(0f, 0f, 0f, 0.9f));
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
            if (_orbMat != null) _orbMat.SetFloat(FadeId, a);
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

        // Class display name — title-case, not the ALL-CAPS enum (spec §6:
        // "Emberward / Tidecaller / Gravekeep / Verdant").
        private static string ClassName(CardClass c) => c switch
        {
            CardClass.EMBERWARD  => "Emberward",
            CardClass.TIDECALLER => "Tidecaller",
            CardClass.GRAVEKEEP  => "Gravekeep",
            CardClass.VERDANT    => "Verdant",
            _                    => "Neutral",
        };

        private static float EaseOutCubic(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        private void OnDestroy()
        {
            _destroyed = true;
            if (_orbMat != null) Destroy(_orbMat);
            foreach (var m in _medallionMats) if (m != null) Destroy(m);
        }

        private static T LoadAsset<T>(string editorPath, string address) where T : Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(editorPath);
#else
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>(address);
            var asset = handle.WaitForCompletion();
#endif
            if (asset == null)
                Debug.LogWarning($"[VersusOverlay] asset '{address}' not found — element skipped.");
            return asset;
        }
    }
}
