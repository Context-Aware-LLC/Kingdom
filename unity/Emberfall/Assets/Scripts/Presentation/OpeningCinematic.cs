using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The game's opening film. It starts before the first scene and remains above every runtime
    /// surface while MainMenu initializes underneath it.
    ///
    /// <para>The film intentionally plays on every process launch for now. When it becomes a
    /// first-player-launch beat, put that policy in <see cref="ShouldPlay"/> and leave the playback
    /// lifecycle here unchanged.</para>
    ///
    /// <para>The source is a StreamingAsset rather than a VideoClip reference. That keeps the
    /// authored MP4 byte-for-byte intact and lets the same bootstrap use Unity's platform-specific
    /// StreamingAssets URL on Android, desktop and other players.</para>
    /// </summary>
    public sealed class OpeningCinematic : MonoBehaviour
    {
        private const string RelativeVideoPath = "Video/Emberfall-intro.mp4";
        private const float PrepareTimeoutSeconds = 30f;
        private const int VideoWidth = 1280;
        private const int VideoHeight = 720;

        private static OpeningCinematic _instance;
        private static bool _active;

        private VideoPlayer _player;
        private RenderTexture _target;
        private AspectRatioFitter _aspect;
        private RawImage _picture;
        private bool _prepared;
        private bool _finished;

        /// <summary>True while the film owns the screen and its soundtrack owns music playback.</summary>
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
            BuildPlayer();
            StartCoroutine(PrepareAndPlay());
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

            var picture = new GameObject("Picture", typeof(RectTransform), typeof(RawImage),
                typeof(AspectRatioFitter));
            picture.transform.SetParent(transform, false);
            Stretch(picture.GetComponent<RectTransform>());

            var image = picture.GetComponent<RawImage>();
            _picture = image;
            image.color = Color.white;
            image.raycastTarget = false;

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

        private void BuildPlayer()
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
            _player.url = Path.Combine(Application.streamingAssetsPath, RelativeVideoPath);
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
        // is finished, so a press on the frame it ends on is harmless.
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

        /// <summary>Ends the film on a player's press without letting that press reach the menu.
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
}
