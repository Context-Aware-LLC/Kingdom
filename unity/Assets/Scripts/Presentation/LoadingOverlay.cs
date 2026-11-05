using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
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
    ///                    wordmark entrance has finished AND MinHold has passed. Most loads
    ///                    finish in a blink, and a plate gone that fast does not earn its
    ///                    place (Sam, 2026-10-21: two seconds, every plate)
    ///   boot failure   — NotifyBootFailed(): never fades; the status line is the error
    ///                    surface. Reconnecting mid-match is HUD scope, not this.
    ///
    /// Legacy plates anchor to screen center and size from screen HEIGHT (spec §5).
    /// The illustrated plates instead fit above a compact footer without cropping. Most rigs are
    /// camera children; a source-scene transition may temporarily become a persistent root and
    /// follow the active camera until its destination adopts it.
    /// </summary>
    public sealed class LoadingOverlay : MonoBehaviour
    {
        // Slot geometry as fractions of screen height, from the 5040×2160 canvas (spec §4).
        private const float OrbDiaFrac    = 630f / 2160f;            // ≈29.2% of height
        private const float WordmarkYFrac = (1080f - 375f) / 2160f;  // slot center above screen center
        private const float WordmarkWFrac = 1600f / 2160f;
        private const float WordmarkHFrac = 330f / 2160f;
        private const float StatusYFrac   = -(1850f - 1080f) / 2160f;

        // Reveal/fade pacing. MinHold is measured from BUILD, as VersusOverlay's is, so a
        // transition plate built in the source scene counts the scene load toward it.
        // Total on-screen ≥ MinHold + FadeOutDuration.
        private const float WordmarkDelay      = 0.6f;
        private const float WordmarkInDuration = 0.9f;
        private const float MinHold            = 2.0f;
        private const float FadeOutDuration    = 0.6f;

        private static readonly Color ErrorTint = new Color(1.00f, 0.55f, 0.45f);
        private static readonly Color IllustrationMatte = new Color(0.96f, 0.92f, 0.82f);

        private static readonly int FadeId     = Shader.PropertyToID("_Fade");
        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        // Addressables.LoadAssetAsync(...).WaitForCompletion is intentionally confined to a
        // prewarmable cache. Collection primes its next plate while the menu's boot cover is still
        // visible, so clicking Collection never starts a blocking bundle read before the transition
        // has painted. These assets are global presentation furniture and already remained resident
        // for the process lifetime because their Addressables handles were never released; making
        // that lifetime explicit avoids repeating WaitForCompletion and gives prewarm real meaning.
        private static readonly Dictionary<string, Object> AssetCache = new();

        private SpriteRenderer _plate;
        private SpriteRenderer _matte;
        private Transform _orbRoot;
        private bool _illustrated;
        private float _layoutAspect;
        private string _plateName = DefaultPlate;
        private string _orbName;                  // null = keep M_LoadingOrb's own _MainTex
        private Material _orbMat;                 // instanced — _Fade/_Progress are per-overlay
        private SpriteRenderer _wordmark;
        private Transform _wordmarkRoot;
        private float _wordmarkScale;
        private TextMeshPro _status;
        private float _statusShown;               // 0→1 entrance of the status line

        private float _t0;
        private bool _matchReady;
        private bool _failed;
        private float _fadeOutT = -1f;            // <0 = not fading yet
        private bool _destroyed;
        private bool _preservedAcrossScenes;
        private Camera _followCamera;
        private float _cameraDistance;
        private float _builtHeight;               // view height (world units) the rig was sized for

        /// <summary>Fires once the fade-out finishes, right before this destroys itself. Screen
        /// Space Overlay UI always paints over this camera-parented rig regardless of its own
        /// fade state, so a caller revealing overlay content (e.g. MainMenu) must wait for this
        /// rather than racing it off NotifyReady/NotifyMatchReady alone.</summary>
        public event System.Action OnFadedOut;

        /// <summary>The boot plate, and the default for every caller that does not ask for another.</summary>
        public const string DefaultPlate = "LoadingPlate_Master";
        public const string IntroPlate = "LoadingPlate_VerdantWatering";
        public const string CollectionPlate = "LoadingPlate_ClockwrightCollection";
        public const string ReturnPlate = "LoadingPlate_GravekeepDog";
        public const string MatchPlate = "LoadingPlate_ClockwrightBreakfast";
        /// <summary>The wait after a sign-in while the collection and decks come down (Sam, 2026-11-05).</summary>
        public const string GatesPlate = "LoadingPlate_TempestBarber";

        /// <summary>
        /// Build the rig under <paramref name="cam"/>. Null camera → null (overlay skipped, boot
        /// unaffected).
        /// </summary>
        /// <param name="plateName">File name (no extension) of a plate under
        /// <c>Assets/Art/Backgrounds/Loading/</c>, letting a screen show its own art rather than the
        /// brand splash. The folder is what decides import settings and the address is the file
        /// name. Register new PNGs and their .meta GUIDs in the Backgrounds Addressables group
        /// (AddressablesSetup can regenerate it), then name the plate here. Anything missing falls back
        /// to skipping the plate, not to a broken screen.</param>
        /// <param name="orbName">File name (no extension) of an orb under
        /// <c>Assets/Art/Orbs/</c> to swap onto the orb material's <c>_MainTex</c>, so a plate can
        /// be shown with the orb authored to go with it. <b>Null or empty keeps the orb the
        /// material already carries</b> — which is what every existing caller gets, so the boot
        /// overlay is byte-identical to before. <c>VersusOverlay</c> does the same swap for its
        /// per-match variant; this is that mechanism, reached by name instead of by index.</param>
        public static LoadingOverlay Create(Camera cam, string plateName = DefaultPlate,
                                            string orbName = null)
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
            overlay._followCamera = cam;
            overlay._cameraDistance = dist;
            overlay._plateName = string.IsNullOrWhiteSpace(plateName) ? DefaultPlate : plateName;
            overlay._illustrated = overlay._plateName == IntroPlate
                || overlay._plateName == CollectionPlate || overlay._plateName == ReturnPlate
                || overlay._plateName == MatchPlate || overlay._plateName == GatesPlate;
            overlay._orbName = string.IsNullOrWhiteSpace(orbName) ? null : orbName;
            overlay.Build(cam, dist);
            return overlay;
        }

        /// <summary>
        /// Resolve every asset needed by a later <see cref="Create"/> while another loading cover is
        /// already on screen. The created renderers are still per-transition; only immutable source
        /// assets and the shared orb material are warmed here.
        /// </summary>
        public static void Prewarm(string plateName = DefaultPlate, string orbName = null)
        {
            string resolvedPlate = string.IsNullOrWhiteSpace(plateName) ? DefaultPlate : plateName;
            LoadAsset<Texture2D>($"Assets/Art/Backgrounds/Loading/{resolvedPlate}.png",
                $"backgrounds/{resolvedPlate}");
            if (!string.IsNullOrWhiteSpace(orbName))
                LoadAsset<Texture2D>($"Assets/Art/Orbs/{orbName}.png", $"orbs/{orbName}");
            LoadAsset<Sprite>("Assets/Art/UI/Loading_Wordmark_KingdomOfBadIdeas.svg",
                "ui/Loading_Wordmark_KingdomOfBadIdeas");
            MaterialCatalog.Get("M_LoadingOrb");
        }

        /// <summary>
        /// Detach this camera-space rig before its source scene is unloaded. Its world pose still
        /// faces the source camera while the asynchronous load runs; the destination calls
        /// <see cref="AttachToCamera"/> during activation to hand it to the new camera.
        /// </summary>
        public void PreserveAcrossSceneLoad()
        {
            if (_destroyed || _preservedAcrossScenes) return;
            transform.SetParent(null, true);
            _preservedAcrossScenes = true;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Continue a preserved transition on the destination scene's camera.</summary>
        public void AttachToCamera(Camera cam)
        {
            if (_destroyed || cam == null) return;
            _followCamera = cam;
            _cameraDistance = cam.nearClipPlane + 1f;
            if (_illustrated) LayoutIllustration(cam);
            if (!_preservedAcrossScenes)
            {
                transform.SetParent(cam.transform, false);
                transform.localPosition = new Vector3(0f, 0f, _cameraDistance);
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one * FitScale();
                return;
            }
            FollowCamera();
        }

        /// <summary>The match has real state on the board (first released snapshot). Idempotent;
        /// fade-out still waits for the wordmark entrance and the minimum hold.</summary>
        public void NotifyMatchReady() => NotifyReady();

        /// <summary>Non-match spelling of <see cref="NotifyMatchReady"/> — same gate, for callers
        /// with no match (MainMenu's app-boot moment: identity/account bootstrap finished).
        /// Idempotent; fade-out still waits for the wordmark entrance and the minimum hold.</summary>
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
            if (_status != null) _status.color = _illustrated
                ? new Color(0.62f, 0.10f, 0.06f) : ErrorTint;
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
            float h = ViewHeight(cam, dist);
            _builtHeight = h;

            // The new paintings have no empty central orb socket. Contain the complete image
            // above a parchment footer, so narrower landscape screens keep the dog, coat and egg.
            if (_illustrated)
            {
                var matteGo = new GameObject("IllustrationMatte");
                matteGo.transform.SetParent(transform, false);
                _matte = matteGo.AddComponent<SpriteRenderer>();
                var white = Texture2D.whiteTexture;
                _matte.sprite = Sprite.Create(white, new Rect(0, 0, white.width, white.height),
                    new Vector2(0.5f, 0.5f), white.width);
                _matte.sortingOrder = 299;
                _matte.color = IllustrationMatte;
            }

            // ── Plate — SpriteRenderer so the default sprite material honors alpha
            //    (MatchEndBanner precedent; primitives' Default-Material strips in URP builds).
            // Lives under Art/Backgrounds/: CardArtPostprocessor keys import settings off the
            // folder, and only Backgrounds treats a full-screen plate correctly (4096 desktop /
            // 2048 mobile, mips off). It once sat in Art/Loading/ and silently imported at
            // Unity's 2048 default — a 5040-wide master shown at 2048. Art/Loading is now gone.
            // The Loading/ subfolder is organisational only: the background lane matches at any
            // depth, and AddressablesSetup addresses by file name, so the address is unchanged.
            var plateTex = LoadAsset<Texture2D>(
                $"Assets/Art/Backgrounds/Loading/{_plateName}.png", $"backgrounds/{_plateName}");
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
                _orbRoot = quad.transform;
                float dia = OrbDiaFrac * h;
                quad.transform.localScale = new Vector3(dia, dia, 1f);
                var rend = quad.GetComponent<MeshRenderer>();
                _orbMat = new Material(orbShared);

                // Only when asked. Setting _MainTex unconditionally — even to the same texture the
                // material already holds — would make every caller's orb depend on this lookup
                // succeeding, and a missing file would then blank the boot orb rather than leave it
                // alone. A null here means "the material is already right".
                if (_orbName != null)
                {
                    var orbTex = LoadAsset<Texture2D>(
                        $"Assets/Art/Orbs/{_orbName}.png", $"orbs/{_orbName}");
                    if (orbTex != null) _orbMat.SetTexture(MainTexId, orbTex);
                    else Debug.LogWarning($"[LoadingOverlay] orb '{_orbName}' did not load; "
                                          + "showing the material's default orb.");
                }

                rend.sharedMaterial = _orbMat;
                rend.sortingOrder = 310;
            }

            // ── Wordmark — authored vector outlines with the approved 60° arc already in
            //    the geometry. This keeps the brand mark independent of TMP layout/font state
            //    and makes the composition scale as one stable unit around the Confluence orb.
            var wordmarkSprite = LoadAsset<Sprite>(
                "Assets/Art/UI/Loading_Wordmark_KingdomOfBadIdeas.svg",
                "ui/Loading_Wordmark_KingdomOfBadIdeas");
            if (wordmarkSprite != null)
            {
                var go = new GameObject("Wordmark");
                go.transform.SetParent(transform, false);

                _wordmark = go.AddComponent<SpriteRenderer>();
                _wordmarkRoot = go.transform;
                _wordmarkRoot.localPosition = new Vector3(0f, WordmarkYFrac * h, -0.02f);
                _wordmark.sprite = wordmarkSprite;
                _wordmark.sortingOrder = 320;
                _wordmark.color = new Color(1f, 1f, 1f, 0f);

                Vector2 wordmarkSize = wordmarkSprite.bounds.size;
                _wordmarkScale = Mathf.Min(
                    WordmarkWFrac * h / wordmarkSize.x,
                    WordmarkHFrac * h / wordmarkSize.y);
                _wordmarkRoot.localScale = Vector3.one * (_wordmarkScale * 1.06f);
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
            BoardController.ApplyUiFont(_status);
            if (_illustrated)
            {
                _status.color = new Color(0.20f, 0.16f, 0.11f, 0f);
                _status.enableAutoSizing = true;
                _status.fontSizeMin = 0.18f * h;
                _status.fontSizeMax = 0.24f * h;
                _status.textWrappingMode = TextWrappingModes.Normal;
                LayoutIllustration(cam);
            }
        }

        private void LayoutIllustration(Camera cam)
        {
            if (_builtHeight <= 0f) return;
            float h = _builtHeight;
            _layoutAspect = cam.aspect;
            if (_matte != null)
                _matte.transform.localScale = new Vector3(h * cam.aspect, h, 1f);
            if (_plate != null)
            {
                float aspect = _plate.sprite.rect.width / _plate.sprite.rect.height;
                float scale = Mathf.Min(0.84f, cam.aspect / aspect);
                _plate.transform.localScale = Vector3.one * scale;
                _plate.transform.localPosition = new Vector3(0f, 0.08f * h, 0f);
            }
            float footerWidth = Mathf.Min(1.7f, cam.aspect * 0.9f) * h;
            float footerY = -0.42f * h;
            if (_orbRoot != null)
            {
                _orbRoot.localPosition = new Vector3(0.44f * footerWidth, footerY, -0.01f);
                _orbRoot.localScale = new Vector3(0.09f * h, 0.09f * h, 1f);
            }
            if (_wordmarkRoot != null)
            {
                _wordmarkRoot.localPosition = new Vector3(-0.34f * footerWidth, footerY, -0.02f);
                Vector2 size = _wordmark.sprite.bounds.size;
                _wordmarkScale = Mathf.Min(0.30f * footerWidth / size.x, 0.10f * h / size.y);
            }
            if (_status != null)
            {
                _status.transform.localPosition = new Vector3(0.07f * footerWidth, footerY, -0.02f);
                _status.rectTransform.sizeDelta = new Vector2(0.43f * footerWidth, 0.10f * h);
            }
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

            // Fade-out gate: choreography done AND held long enough AND ready AND not failed.
            if (_fadeOutT < 0f && revealDone && t >= MinHold && _matchReady && !_failed)
                _fadeOutT = t;
            float fade = _fadeOutT < 0f ? 1f : 1f - Mathf.Clamp01((t - _fadeOutT) / FadeOutDuration);

            if (_plate != null) _plate.color = new Color(1f, 1f, 1f, fade);
            if (_matte != null) _matte.color = new Color(
                IllustrationMatte.r, IllustrationMatte.g, IllustrationMatte.b, fade);
            if (_orbMat != null) _orbMat.SetFloat(FadeId, fade);
            if (_wordmark != null)
            {
                _wordmark.color = new Color(1f, 1f, 1f, enter * fade);
                _wordmarkRoot.localScale = Vector3.one
                    * (_wordmarkScale * Mathf.Lerp(1.06f, 1f, enter));
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

        private void LateUpdate()
        {
            if (_preservedAcrossScenes && _followCamera != null) FollowCamera();
            if (_illustrated && _followCamera != null
                && !Mathf.Approximately(_layoutAspect, _followCamera.aspect))
                LayoutIllustration(_followCamera);
        }

        private void FollowCamera()
        {
            transform.SetPositionAndRotation(
                _followCamera.transform.TransformPoint(0f, 0f, _cameraDistance),
                _followCamera.transform.rotation);
            transform.localScale = Vector3.one * FitScale();
        }

        /// <summary>The rig is sized in world units for the camera that built it. A destination
        /// camera with a different projection sees a different height at the same distance — the
        /// orthographic menu (size 5 → 10 units) handing to Collection's 60° perspective camera
        /// (1.5 units at 1.3) showed the rig ~6.7× too large, a close-up of the orb's core.</summary>
        private float FitScale()
        {
            if (_followCamera == null || _builtHeight <= 0f) return 1f;
            return ViewHeight(_followCamera, _cameraDistance) / _builtHeight;
        }

        private static float ViewHeight(Camera cam, float dist) => cam.orthographic
            ? cam.orthographicSize * 2f
            : 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        private static float EaseOutCubic(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

        private void OnDestroy()
        {
            _destroyed = true;
            if (_orbMat != null) Destroy(_orbMat);
            if (_plate != null && _plate.sprite != null) Destroy(_plate.sprite);
            if (_matte != null && _matte.sprite != null) Destroy(_matte.sprite);
        }

        private static T LoadAsset<T>(string editorPath, string address) where T : Object
        {
            if (AssetCache.TryGetValue(address, out Object cached) && cached is T typed)
                return typed;
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(editorPath);
#else
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>(address);
            var asset = handle.WaitForCompletion();
#endif
            if (asset == null)
                Debug.LogWarning($"[LoadingOverlay] asset '{address}' not found — element skipped.");
            else
                AssetCache[address] = asset;
            return asset;
        }
    }
}
