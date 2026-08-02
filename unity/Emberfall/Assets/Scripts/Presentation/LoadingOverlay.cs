using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The boot loading screen (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §4/§5) — a camera-parented
    /// world-space rig built procedurally in Awake() by GameBootstrap, NetworkedGameBootstrap
    /// and MainMenuController alike, so it covers frame 0 with no scene surgery. Serves app
    /// startup (MainMenu) and menu→match boot (Board/BoardNetworked) with the same rig.
    ///
    /// Reveal choreography (trigger ruling, Sam 2026-07-19):
    ///   frame 0        — plate + orb visible (no trigger; authored into existence in Awake)
    ///   +wordmarkDelay — wordmark fades/settles in (pure timing, no game signal)
    ///   on demand      — status line appears when the tracked work begins (SetStatus;
    ///                    placeholder text until the progress/tip design pass)
    ///   fade out       — NotifyMatchReady() (first MatchRunner.OnStateUpdated) AND the
    ///                    wordmark entrance has finished: the choreography IS the minimum
    ///                    hold, no arbitrary timer
    ///   boot failure   — NotifyBootFailed(): never fades; the status line is the error
    ///                    surface. Reconnecting mid-match is HUD scope, not this.
    ///
    /// All overlays anchor to screen center and size from screen HEIGHT (spec §5) — the
    /// plate is height-anchored so vertical content never crops; width flexes per aspect.
    /// </summary>
    public sealed class LoadingOverlay : MonoBehaviour
    {
        // Slot geometry as fractions of screen height, from the 5040×2160 canvas (spec §4).
        private const float PlateAspect   = 5040f / 2160f;
        private const float OrbDiaFrac    = 630f / 2160f;            // ≈29.2% of height
        private const float WordmarkYFrac = (1080f - 375f) / 2160f;  // slot center above screen center
        private const float WordmarkWFrac = 1600f / 2160f;
        private const float WordmarkHFrac = 330f / 2160f;
        private const float StatusYFrac   = -(1850f - 1080f) / 2160f;

        // Reveal/fade pacing.
        private const float WordmarkDelay      = 0.6f;
        private const float WordmarkInDuration = 0.9f;
        private const float FadeOutDuration    = 0.6f;

        // Wordmark ember gradient — TMP vertex colors, NOT the material (as-built §4).
        private static readonly Color EmberGold = new Color(1.00f, 0.87f, 0.55f);
        private static readonly Color EmberRed  = new Color(0.75f, 0.22f, 0.08f);
        private static readonly Color ErrorTint = new Color(1.00f, 0.55f, 0.45f);

        private static readonly int FadeId     = Shader.PropertyToID("_Fade");
        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int FaceColorId     = Shader.PropertyToID("_FaceColor");
        private static readonly int OutlineColorId  = Shader.PropertyToID("_OutlineColor");
        private static readonly int UnderlayColorId = Shader.PropertyToID("_UnderlayColor");

        private SpriteRenderer _plate;
        private Material _orbMat;                 // instanced — _Fade/_Progress are per-overlay
        private TextMeshPro _wordmark;
        private Transform _wordmarkRoot;
        private float _faceA = 1f, _outlineA = 1f, _underlayA = 1f;
        private TextMeshPro _status;
        private float _statusShown;               // 0→1 entrance of the status line

        private float _t0;
        private bool _matchReady;
        private bool _failed;
        private float _fadeOutT = -1f;            // <0 = not fading yet
        private bool _destroyed;

        /// <summary>Fires once the fade-out finishes, right before this destroys itself. Screen
        /// Space Overlay UI always paints over this camera-parented rig regardless of its own
        /// fade state, so a caller revealing overlay content (e.g. MainMenu) must wait for this
        /// rather than racing it off NotifyReady/NotifyMatchReady alone.</summary>
        public event System.Action OnFadedOut;

        /// <summary>Build the rig under <paramref name="cam"/>. Null camera → null (overlay skipped, boot unaffected).</summary>
        public static LoadingOverlay Create(Camera cam)
        {
            if (cam == null)
            {
                Debug.LogWarning("[LoadingOverlay] no camera — overlay skipped.");
                return null;
            }
            var go = new GameObject("LoadingOverlay");
            go.transform.SetParent(cam.transform, false);
            float dist = cam.nearClipPlane + 1f;
            go.transform.localPosition = new Vector3(0f, 0f, dist);
            var overlay = go.AddComponent<LoadingOverlay>();
            overlay.Build(cam, dist);
            return overlay;
        }

        /// <summary>The match has real state on the board (first released snapshot). Idempotent;
        /// fade-out still waits for the wordmark entrance to finish.</summary>
        public void NotifyMatchReady() => NotifyReady();

        /// <summary>Non-match spelling of <see cref="NotifyMatchReady"/> — same gate, for callers
        /// with no match (MainMenu's app-boot moment: identity/account bootstrap finished).
        /// Idempotent; fade-out still waits for the wordmark entrance to finish.</summary>
        public void NotifyReady()
        {
            if (_destroyed) return;
            _matchReady = true;
        }

        /// <summary>Boot cannot reach a match. The overlay stays up and shows the reason.</summary>
        public void NotifyBootFailed(string reason)
        {
            if (_destroyed) return;
            _failed = true;
            SetStatus(reason);
            if (_status != null) _status.color = ErrorTint;
        }

        /// <summary>Progress/tip line — PLACEHOLDER surface until the indicator design pass.
        /// First call fades the line in ("the indicator appears as its tracked work begins").</summary>
        public void SetStatus(string text)
        {
            if (_destroyed || _status == null) return;
            _status.text = text;
        }

        /// <summary>Optional 0–1 hook into the orb's _Progress (design pending; defaults to 1).</summary>
        public void SetProgress(float progress)
        {
            if (_destroyed || _orbMat == null) return;
            _orbMat.SetFloat(ProgressId, Mathf.Clamp01(progress));
        }

        private void Build(Camera cam, float dist)
        {
            _t0 = Time.time;
            float h = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            // ── Plate — SpriteRenderer so the default sprite material honors alpha
            //    (MatchEndBanner precedent; primitives' Default-Material strips in URP builds).
            // Lives in Art/Backgrounds/, not Art/Loading/: CardArtPostprocessor keys import
            // settings off the folder, and only Backgrounds treats a full-screen plate correctly
            // (4096 desktop / 2048 mobile, mips off). In Art/Loading/ it silently imported at
            // Unity's 2048 default — a 5040-wide master shown at 2048.
            var plateTex = LoadAsset<Texture2D>(
                "Assets/Art/Backgrounds/LoadingPlate_Master.png", "backgrounds/LoadingPlate_Master");
            if (plateTex != null)
            {
                var go = new GameObject("Plate");
                go.transform.SetParent(transform, false);
                _plate = go.AddComponent<SpriteRenderer>();
                _plate.sprite = Sprite.Create(plateTex,
                    new Rect(0, 0, plateTex.width, plateTex.height),
                    new Vector2(0.5f, 0.5f), pixelsPerUnit: plateTex.height / h);
                _plate.sortingOrder = 300;
            }

            // ── Orb — quad + instanced M_LoadingOrb (motion lives in the shader).
            var orbShared = MaterialCatalog.Get("M_LoadingOrb");
            if (orbShared != null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Orb";
                Destroy(quad.GetComponent<Collider>());   // never eat board raycasts
                quad.transform.SetParent(transform, false);
                float dia = OrbDiaFrac * h;
                quad.transform.localScale = new Vector3(dia, dia, 1f);
                var rend = quad.GetComponent<MeshRenderer>();
                _orbMat = new Material(orbShared);
                rend.sharedMaterial = _orbMat;
                rend.sortingOrder = 310;
            }

            // ── Wordmark — 3D TMP, Alfa Slab One SDF, instanced wordmark material,
            //    vertex-gradient ember, TextArcWarp 60° (as-built §4). Starts at alpha 0.
            var font = LoadAsset<TMP_FontAsset>(
                "Assets/Art/Fonts/AlfaSlabOne-Regular SDF.asset", "fonts/AlfaSlabOne-Regular SDF");
            var wordmarkShared = MaterialCatalog.Get("M_WordmarkEmberfall");
            if (font != null && wordmarkShared != null)
            {
                var go = new GameObject("Wordmark");
                go.transform.SetParent(transform, false);

                _wordmark = go.AddComponent<TextMeshPro>();
                // AddComponent<TextMeshPro> swaps the GO's Transform for a RectTransform,
                // DESTROYING the original — cache the root only after this point.
                _wordmarkRoot = _wordmark.transform;
                _wordmarkRoot.localPosition = new Vector3(0f, WordmarkYFrac * h, -0.02f);
                _wordmark.font = font;
                _wordmark.fontSharedMaterial = wordmarkShared;
                _wordmark.rectTransform.sizeDelta = new Vector2(WordmarkWFrac * h, WordmarkHFrac * h);
                _wordmark.enableAutoSizing = true;
                _wordmark.fontSizeMin = 1f;
                _wordmark.fontSizeMax = 500f;
                _wordmark.alignment = TextAlignmentOptions.Center;
                _wordmark.textWrappingMode = TextWrappingModes.NoWrap;
                _wordmark.characterSpacing = 6f;   // as-built: spacing lives on the component
                _wordmark.enableVertexGradient = true;
                _wordmark.colorGradient = new VertexGradient(EmberGold, EmberGold, EmberRed, EmberRed);
                _wordmark.sortingOrder = 320;
                _wordmark.text = "EMBERFALL";
                go.AddComponent<TextArcWarp>();    // default 60° — the approved arc

                // Fade scales face/outline/underlay together (vertex alpha alone would leave
                // the charred outline at full strength). Base alphas come from the SHARED
                // material; writes go to tmp.fontMaterial re-queried per call, because TMP can
                // re-instance its material during mesh regeneration and strand a cached copy.
                _faceA     = GetAlpha(wordmarkShared, FaceColorId);
                _outlineA  = GetAlpha(wordmarkShared, OutlineColorId);
                _underlayA = GetAlpha(wordmarkShared, UnderlayColorId);
                ApplyWordmarkAlpha(0f);
            }

            // ── Status line — placeholder for the progress/tip slot (spec §4 row 3).
            //    Empty until SetStatus; doubles as the boot-failure surface.
            var statusGo = new GameObject("StatusLine");
            statusGo.transform.SetParent(transform, false);
            statusGo.transform.localPosition = new Vector3(0f, StatusYFrac * h, -0.02f);
            _status = statusGo.AddComponent<TextMeshPro>();
            _status.fontSize = 0.35f * h;
            _status.alignment = TextAlignmentOptions.Center;
            _status.textWrappingMode = TextWrappingModes.NoWrap;
            _status.rectTransform.sizeDelta = new Vector2(1.6f * h, 0.12f * h);
            _status.color = new Color(0.92f, 0.88f, 0.82f, 0f);
            _status.sortingOrder = 320;
            _status.text = "";
        }

        private void Update()
        {
            float t = Time.time - _t0;

            // Wordmark entrance: fade + slight scale settle.
            float enter = EaseOutCubic(Mathf.Clamp01((t - WordmarkDelay) / WordmarkInDuration));
            bool revealDone = enter >= 1f;

            // Status line entrance: fades in once it has text (the tracked work began).
            bool statusActive = _status != null && _status.text.Length > 0;
            _statusShown = Mathf.MoveTowards(_statusShown, statusActive ? 1f : 0f, Time.deltaTime * 3f);

            // Fade-out gate: choreography done AND match ready AND not failed.
            if (_fadeOutT < 0f && revealDone && _matchReady && !_failed)
                _fadeOutT = t;
            float fade = _fadeOutT < 0f ? 1f : 1f - Mathf.Clamp01((t - _fadeOutT) / FadeOutDuration);

            if (_plate != null) _plate.color = new Color(1f, 1f, 1f, fade);
            if (_orbMat != null) _orbMat.SetFloat(FadeId, fade);
            if (_wordmark != null)
            {
                ApplyWordmarkAlpha(enter * fade);
                _wordmarkRoot.localScale = Vector3.one * Mathf.Lerp(1.06f, 1f, enter);
            }
            if (_status != null)
            {
                Color c = _status.color;
                c.a = _statusShown * fade;
                _status.color = c;
            }

            if (_fadeOutT >= 0f && fade <= 0f)
            {
                OnFadedOut?.Invoke();
                Destroy(gameObject);
            }
        }

        private void ApplyWordmarkAlpha(float a)
        {
            if (_wordmark == null) return;
            Material m = _wordmark.fontMaterial;   // current instance, survives TMP re-instancing
            SetAlpha(m, FaceColorId, _faceA * a);
            SetAlpha(m, OutlineColorId, _outlineA * a);
            SetAlpha(m, UnderlayColorId, _underlayA * a);
        }

        private static float GetAlpha(Material m, int id) =>
            m != null && m.HasProperty(id) ? m.GetColor(id).a : 1f;

        private static void SetAlpha(Material m, int id, float a)
        {
            if (m == null || !m.HasProperty(id)) return;
            Color c = m.GetColor(id);
            c.a = a;
            m.SetColor(id, c);
        }

        private static float EaseOutCubic(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        private void OnDestroy()
        {
            _destroyed = true;
            if (_orbMat != null) Destroy(_orbMat);
            // The wordmark's fontMaterial instance is TMP's — TMP owns and destroys it.
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
                Debug.LogWarning($"[LoadingOverlay] asset '{address}' not found — element skipped.");
            return asset;
        }
    }
}
