using System;
using System.Collections;
using System.IO;
using Emberfall.Session;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The game's opening sequence: a splash card, then the opening film, then MainMenu underneath
    /// is revealed. It starts before the first scene and remains above every runtime surface while
    /// MainMenu initializes.
    ///
    /// <para><b>THE FILM IS DOWNLOADED, NOT PACKAGED</b> (Sam, 2026-09-27). It used to ship as a
    /// StreamingAsset, which meant its 60MB shipped byte-for-byte in every APK whether or not the
    /// player ever saw it — a quarter of the whole package for one skippable clip, on a game that
    /// already requires a connection and has no offline mode (so "it might not be there yet" is not
    /// a new failure mode this introduces). It now lives in Supabase Storage and is fetched to
    /// <see cref="Application.persistentDataPath"/> on first launch and re-used from there on every
    /// launch after — the splash card is the padding that download needs, not decoration.</para>
    ///
    /// <para>The source is a plain file path once cached, not a VideoClip reference — the same
    /// <see cref="VideoPlayer"/> URL-source setup this used for StreamingAssets works unchanged
    /// once it is pointed at <see cref="Application.persistentDataPath"/> instead.</para>
    ///
    /// <para>The film intentionally plays on every process launch for now. When it becomes a
    /// first-player-launch beat, put that policy in <see cref="ShouldPlay"/> and leave the playback
    /// lifecycle here unchanged.</para>
    /// </summary>
    public sealed class OpeningCinematic : MonoBehaviour
    {
        private const string CacheFileName = "intro.mp4";

        /// <summary>Bucket/object path under Supabase Storage's public object endpoint. NOT
        /// "Emberfall" anything (Sam, 2026-09-27) -- that is the internal codename, not the
        /// published name (still undecided, see the game-name thread), and this path is public: it
        /// is fetched over plain HTTP by every client. A path mismatch here is a 404 that the
        /// download-failure branch below quietly swallows into "skip to menu".</summary>
        private const string StorageObjectPath = "video/intro.mp4";

        private const float DownloadTimeoutSeconds = 60f;
        private const float PrepareTimeoutSeconds = 30f;
        private const int VideoWidth = 1280;
        private const int VideoHeight = 720;

        private static OpeningCinematic _instance;
        private static bool _active;

        private VideoPlayer _player;
        private RenderTexture _target;
        private AspectRatioFitter _aspect;
        private RawImage _picture;
        private TextMeshProUGUI _wordmark;
        private TextMeshProUGUI _status;
        private UnityWebRequest _download;
        private bool _prepared;
        private bool _finished;

        /// <summary>True while the sequence owns the screen and its soundtrack owns music playback
        /// (<see cref="MusicDirector"/> reads this) — covers the splash/download wait as well as the
        /// film itself, since menu music starting underneath a black download screen would read as
        /// broken audio, not as "the video hasn't started yet".</summary>
        public static bool IsActive => _active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _active = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.isBatchMode || !ShouldPlay()) return;

            var host = new GameObject("~OpeningCinematic");
            host.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(host);

            _active = true;
            _instance = host.AddComponent<OpeningCinematic>();
        }

        // Every-launch behavior is deliberate for the current milestone. This method is the sole
        // future seam for a FirstRunMarkers-backed policy; playback itself must not own identity.
        private static bool ShouldPlay() => true;

        private void Awake()
        {
            BuildScreen();
            StartCoroutine(Run());
        }

        private void BuildScreen()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            gameObject.AddComponent<GraphicRaycaster>();

            var events = gameObject.AddComponent<EventSystem>();
            events.sendNavigationEvents = false;
            var input = gameObject.AddComponent<InputSystemUIInputModule>();
            input.AssignDefaultActions();

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(VideoWidth, VideoHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            var blocker = new GameObject("Black", typeof(RectTransform), typeof(Image));
            blocker.transform.SetParent(transform, false);
            Stretch(blocker.GetComponent<RectTransform>());
            Image black = blocker.GetComponent<Image>();
            black.color = Color.black;
            black.raycastTarget = true;

            // Same wordmark treatment as MainMenuScreen's `.mm-wordmark` (mainmenu.uss) so the
            // splash reads as the same brand rather than a placeholder loading screen.
            var wordmark = new GameObject("Wordmark", typeof(RectTransform), typeof(TextMeshProUGUI));
            wordmark.transform.SetParent(transform, false);
            var wordmarkRect = wordmark.GetComponent<RectTransform>();
            wordmarkRect.anchorMin = new Vector2(0.5f, 0.5f);
            wordmarkRect.anchorMax = new Vector2(0.5f, 0.5f);
            wordmarkRect.sizeDelta = new Vector2(900, 120);
            wordmarkRect.anchoredPosition = Vector2.zero;
            _wordmark = wordmark.GetComponent<TextMeshProUGUI>();
            _wordmark.text = "EMBERFALL";
            _wordmark.fontSize = 64;
            _wordmark.fontStyle = FontStyles.Bold;
            _wordmark.characterSpacing = 8;
            _wordmark.alignment = TextAlignmentOptions.Center;
            _wordmark.color = new Color32(244, 239, 228, 255);

            var status = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            status.transform.SetParent(transform, false);
            var statusRect = status.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.5f, 0.5f);
            statusRect.anchorMax = new Vector2(0.5f, 0.5f);
            statusRect.sizeDelta = new Vector2(600, 60);
            statusRect.anchoredPosition = new Vector2(0, -90);
            _status = status.GetComponent<TextMeshProUGUI>();
            _status.text = "";
            _status.fontSize = 24;
            _status.alignment = TextAlignmentOptions.Center;
            _status.color = new Color32(200, 200, 200, 255);

            var picture = new GameObject("Picture", typeof(RectTransform), typeof(RawImage),
                typeof(AspectRatioFitter));
            picture.transform.SetParent(transform, false);
            Stretch(picture.GetComponent<RectTransform>());

            var image = picture.GetComponent<RawImage>();
            _picture = image;
            image.color = Color.white;
            image.raycastTarget = false;
            image.enabled = false;

            _aspect = picture.GetComponent<AspectRatioFitter>();
            _aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _aspect.aspectRatio = (float)VideoWidth / VideoHeight;

            _target = new RenderTexture(VideoWidth, VideoHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "OpeningCinematic",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _target.Create();
            image.texture = _target;
        }

        /// <summary>Splash (wordmark + status) until a playable local copy of the film exists, then
        /// the film itself. Every exit from this — no settings asset, a download failure, a prepare
        /// timeout, a playback error, a skip — funnels through <see cref="Complete"/> or
        /// <see cref="Skip"/> so MainMenu underneath is always reached exactly once.</summary>
        private IEnumerator Run()
        {
            string cachedPath = Path.Combine(Application.persistentDataPath, CacheFileName);

            if (!File.Exists(cachedPath))
            {
                bool downloaded = false;
                yield return DownloadFilm(cachedPath, ok => downloaded = ok);
                if (_finished) yield break;
                if (!downloaded)
                {
                    Complete();
                    yield break;
                }
            }

            _status.text = "";
            BuildPlayer(cachedPath);
            yield return PrepareAndPlay();
        }

        /// <summary>Downloads straight to a `.part` file and renames on success, so a killed process
        /// or a skip mid-download never leaves a truncated file at <paramref name="cachedPath"/> for
        /// a later launch to mistake for a complete, playable cache.</summary>
        private IEnumerator DownloadFilm(string cachedPath, Action<bool> setResult)
        {
            string url = ResolveVideoUrl();
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogWarning("[OpeningCinematic] no Supabase project configured; " +
                                 "skipping the opening film.");
                setResult(false);
                yield break;
            }

            string partPath = cachedPath + ".part";
            _status.text = "Downloading…";

            using (_download = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                _download.downloadHandler = new DownloadHandlerFile(partPath) { removeFileOnAbort = true };
                _download.timeout = 0; // Skip() is the escape hatch, not a fixed deadline on a real download.

                UnityWebRequestAsyncOperation op = _download.SendWebRequest();

                float deadline = Time.realtimeSinceStartup + DownloadTimeoutSeconds;
                while (!op.isDone && !_finished)
                {
                    // The deadline only guards against a connection that never starts moving —
                    // once real bytes are landing, let it run to completion regardless of size.
                    if (_download.downloadedBytes == 0 && Time.realtimeSinceStartup > deadline)
                    {
                        _download.Abort();
                        break;
                    }
                    _status.text = $"Downloading… {Mathf.RoundToInt(_download.downloadProgress * 100f)}%";
                    yield return null;
                }

                if (_finished)
                {
                    // Skip() already aborted this request; the `using` below disposes it. Null the
                    // field FIRST so OnDestroy's own `_download.Abort()` guard can't reach a request
                    // that is about to be (or already is) disposed.
                    _download = null;
                    setResult(false);
                    yield break;
                }

                if (_download.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[OpeningCinematic] film download failed ({_download.error}); " +
                                     "skipping to MainMenu.");
                    _download = null;
                    setResult(false);
                    yield break;
                }

                _download = null;
            }

            try
            {
                if (File.Exists(cachedPath)) File.Delete(cachedPath);
                File.Move(partPath, cachedPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpeningCinematic] could not finalize the downloaded film ({ex.Message}); " +
                                 "skipping to MainMenu.");
                setResult(false);
                yield break;
            }

            setResult(true);
        }

        /// <summary>`{projectUrl}/storage/v1/object/public/{StorageObjectPath}` off the SAME project
        /// <see cref="SupabaseAuthSettings"/> already points sign-in at (see AuthGateway's identical
        /// `Resources.Load` call) — one project reference, not a second config surface for one
        /// URL.</summary>
        private static string ResolveVideoUrl()
        {
            SupabaseAuthSettings settings;
            try { settings = Resources.Load<SupabaseAuthSettings>("SupabaseAuthSettings"); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpeningCinematic] SupabaseAuthSettings could not be loaded: {ex.Message}");
                return null;
            }
            if (settings == null) return null;

            string projectUrl = settings.ToConfig().ProjectUrl;
            if (string.IsNullOrEmpty(projectUrl)) return null;

            return $"{projectUrl.TrimEnd('/')}/storage/v1/object/public/{StorageObjectPath}";
        }

        private void BuildPlayer(string localFilePath)
        {
            var soundtrack = gameObject.AddComponent<AudioSource>();
            soundtrack.playOnAwake = false;
            soundtrack.loop = false;
            soundtrack.spatialBlend = 0f;
            soundtrack.outputAudioMixerGroup = AudioSettings.MusicGroup;

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.waitForFirstFrame = true;
            _player.skipOnDrop = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _target;
            _player.source = VideoSource.Url;
            _player.url = localFilePath;
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, soundtrack);
            _player.prepareCompleted += OnPrepared;
            _player.loopPointReached += OnFinished;
            _player.errorReceived += OnError;
        }

        // Any press skips (Sam, 2026-09-26): click, tap or space. Polled on the press edge rather
        // than through the EventSystem so it also works while the film is still preparing, when the
        // canvas has nothing to raycast against yet. Skip() and Complete() both bail once the film
        // is finished, so a press on the frame it ends on is harmless. Also live during the splash
        // download (Sam, 2026-09-27) -- a player who wants out should not be held for a 60MB fetch.
        private void Update()
        {
            if (_finished) return;

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var touch = Touchscreen.current;

            bool pressed =
                (keyboard != null && keyboard.spaceKey.wasPressedThisFrame) ||
                (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                (touch != null && touch.primaryTouch.press.wasPressedThisFrame);

            if (pressed) Skip();
        }

        /// <summary>Ends the sequence on a player's press without letting that press reach the menu.
        /// The skip lands on the press edge, so the release arrives after the film is gone and the
        /// MainMenu underneath sees a pointer-up (or a space key-up) it never saw the start of. The
        /// screen is therefore kept alive, black, until every skip input is let go — its EventSystem
        /// is what feeds the menu, and with navigation off and an opaque raycast target on top it
        /// swallows the release. Music is released immediately; only the input shield lingers.</summary>
        private void Skip()
        {
            if (_finished) return;
            _finished = true;
            _active = false;

            if (_download != null) _download.Abort();
            if (_player != null) _player.Stop();
            if (_picture != null) _picture.enabled = false;
            if (_wordmark != null) _wordmark.enabled = false;
            if (_status != null) _status.enabled = false;
            StartCoroutine(HoldUntilReleased());
        }

        private IEnumerator HoldUntilReleased()
        {
            // Capped so a stuck key or a lost touch can never trap the player behind black.
            float deadline = Time.realtimeSinceStartup + 2f;
            while (AnySkipInputHeld() && Time.realtimeSinceStartup < deadline)
                yield return null;

            // One more frame so the release itself is dispatched to this EventSystem, not the menu's.
            yield return null;
            Destroy(gameObject);
        }

        private static bool AnySkipInputHeld()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var touch = Touchscreen.current;
            return (keyboard != null && keyboard.spaceKey.isPressed) ||
                   (mouse != null && mouse.leftButton.isPressed) ||
                   (touch != null && touch.primaryTouch.press.isPressed);
        }

        private IEnumerator PrepareAndPlay()
        {
            float deadline = Time.realtimeSinceStartup + PrepareTimeoutSeconds;
            _player.Prepare();

            while (!_prepared && !_finished && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!_prepared && !_finished)
            {
                Debug.LogWarning($"[OpeningCinematic] video did not prepare within " +
                                 $"{PrepareTimeoutSeconds:0} seconds; continuing to MainMenu.");
                Complete();
            }
        }

        private void OnPrepared(VideoPlayer player)
        {
            if (_finished) return;
            _prepared = true;

            if (player.width > 0 && player.height > 0)
                _aspect.aspectRatio = (float)player.width / player.height;

            _wordmark.enabled = false;
            _status.enabled = false;
            _picture.enabled = true;
            player.Play();
        }

        private void OnFinished(VideoPlayer _) => Complete();

        private void OnError(VideoPlayer _, string message)
        {
            Debug.LogWarning($"[OpeningCinematic] video playback failed ({message}); " +
                             "continuing to MainMenu.");
            Complete();
        }

        private void Complete()
        {
            if (_finished) return;
            _finished = true;
            _active = false;

            if (_player != null) _player.Stop();
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_download != null)
            {
                _download.Abort();
                _download = null;
            }

            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepared;
                _player.loopPointReached -= OnFinished;
                _player.errorReceived -= OnError;
            }

            if (_target != null)
            {
                _target.Release();
                Destroy(_target);
            }

            if (_instance == this)
            {
                _instance = null;
                _active = false;
            }
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
