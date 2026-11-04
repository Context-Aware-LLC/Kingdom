using System;
using System.Collections;
using System.IO;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Kingdom.Presentation
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
        /// "Kingdom" anything (Sam, 2026-09-27) -- that is the internal codename, not the
        /// published name (still undecided, see the game-name thread), and this path is public: it
        /// is fetched over plain HTTP by every client. A path mismatch here is a 404 that the
        /// download-failure branch below quietly swallows into "skip to menu".</summary>
        private const string StorageObjectPath = "video/intro.mp4";

        private const float DownloadTimeoutSeconds = 60f;
        private const float PrepareTimeoutSeconds = 30f;
        private const int VideoWidth = 1280;
        private const int VideoHeight = 720;

        /// <summary>The studio mark shown until the film is ready to play (Sam, 2026-09-27). Loaded
        /// from Resources, not Addressables: this runs BeforeSceneLoad, ahead of anything that
        /// initializes the Addressables catalog.</summary>
        private const string SplashResource = "Splash/SplashWisp";
        private const float SplashHeight = 360f;

        private static OpeningCinematic _instance;
        private static bool _active;

        private VideoPlayer _player;
        private RenderTexture _target;
        private AspectRatioFitter _aspect;
        private RawImage _picture;
        private RawImage _splash;
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

            // No progress readout (Sam, 2026-09-27): the mark alone holds the screen through the
            // first-launch download and the prepare, so the player sees a splash, not a loader.
            var splashTex = Resources.Load<Texture2D>(SplashResource);
            if (splashTex != null)
            {
                var splash = new GameObject("Splash", typeof(RectTransform), typeof(RawImage));
                splash.transform.SetParent(transform, false);
                var splashRect = splash.GetComponent<RectTransform>();
                splashRect.anchorMin = new Vector2(0.5f, 0.5f);
                splashRect.anchorMax = new Vector2(0.5f, 0.5f);
                splashRect.sizeDelta = new Vector2(
                    SplashHeight * splashTex.width / splashTex.height, SplashHeight);
                splashRect.anchoredPosition = Vector2.zero;
                _splash = splash.GetComponent<RawImage>();
                _splash.texture = splashTex;
                _splash.raycastTarget = false;
            }
            else
            {
                Debug.LogWarning($"[OpeningCinematic] splash '{SplashResource}' not found; showing black.");
            }

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

        /// <summary>Splash until a playable local copy of the film exists, then
        /// the film itself. Every exit from this — no settings asset, a download failure, a prepare
        /// timeout, a playback error, a skip — funnels through <see cref="Complete"/> or
        /// <see cref="Skip"/> so MainMenu underneath is always reached exactly once.</summary>
        private IEnumerator Run()
        {
            string cachedPath = Path.Combine(Application.persistentDataPath, CacheFileName);

            if (!File.Exists(cachedPath))
            {
                string url = ResolveVideoUrl();
                if (string.IsNullOrEmpty(url))
                {
                    Debug.LogWarning("[OpeningCinematic] no Supabase project configured; " +
                                     "skipping the opening film.");
                    Complete();
                    yield break;
                }

                OpeningFilmDownload.Begin(url, cachedPath);

                float deadline = Time.realtimeSinceStartup + DownloadTimeoutSeconds;
                while (OpeningFilmDownload.InProgress && !_finished)
                {
                    // The deadline only guards against a connection that never starts moving —
                    // once real bytes are landing, let it run to completion regardless of size.
                    if (OpeningFilmDownload.DownloadedBytes == 0 && Time.realtimeSinceStartup > deadline)
                    {
                        OpeningFilmDownload.Abort();
                        break;
                    }
                    yield return null;
                }

                // A skip leaves the download running on its own; the next launch plays from cache.
                if (_finished) yield break;

                // Covers a failed request, an abort, and a rename that did not land; the download
                // logs its own reason.
                if (!File.Exists(cachedPath))
                {
                    Complete();
                    yield break;
                }
            }

            BuildPlayer(cachedPath);
            yield return PrepareAndPlay();
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
        // The skip ends the wait, not the fetch: see OpeningFilmDownload.
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

            if (_player != null) _player.Stop();
            if (_picture != null) _picture.enabled = false;
            if (_splash != null) _splash.enabled = false;
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

            if (_splash != null) _splash.enabled = false;
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

    /// <summary>The first-launch fetch of the opening film, owned by the process rather than by
    /// <see cref="OpeningCinematic"/>. A skip during the splash used to abort the request and throw
    /// the partial file away, so every player who tapped through the splash on first launch
    /// re-downloaded the film from the start on the next launch, and the one after (Sam,
    /// 2026-11-04: Storage egress against the Supabase quota). Now the skip only ends the wait: the
    /// request keeps running with no screen, the next launch finds the finished file and plays it,
    /// and the film is fetched once per install.
    ///
    /// <para>Writes to a `.part` file and renames on success, so a killed process never leaves a
    /// truncated file at the cache path for a later launch to mistake for a complete, playable
    /// cache.</para></summary>
    internal static class OpeningFilmDownload
    {
        private static UnityWebRequest _request;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _request = null;

        public static bool InProgress => _request != null;

        public static ulong DownloadedBytes => _request != null ? _request.downloadedBytes : 0UL;

        /// <summary>Starts the fetch unless one is already running in this process.</summary>
        public static void Begin(string url, string cachedPath)
        {
            if (_request != null) return;

            string partPath = cachedPath + ".part";
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(partPath) { removeFileOnAbort = true },
                timeout = 0 // no fixed deadline on a real download; see the no-bytes guard in Run().
            };
            _request = request;
            request.SendWebRequest().completed += _ => Finish(request, partPath, cachedPath);
        }

        /// <summary>Gives up on a connection that never started moving. The partial file is
        /// removed by the handler.</summary>
        public static void Abort()
        {
            if (_request != null) _request.Abort();
        }

        private static void Finish(UnityWebRequest request, string partPath, string cachedPath)
        {
            bool ok = request.result == UnityWebRequest.Result.Success;
            string error = request.error;

            // Dispose before the rename so the handler has let go of the `.part` file.
            if (_request == request) _request = null;
            request.Dispose();

            if (!ok)
            {
                Debug.LogWarning($"[OpeningCinematic] film download failed ({error}).");
                return;
            }

            try
            {
                if (File.Exists(cachedPath)) File.Delete(cachedPath);
                File.Move(partPath, cachedPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpeningCinematic] could not finalize the downloaded film ({ex.Message}).");
            }
        }
    }
}
