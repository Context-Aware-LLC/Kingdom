using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The trace an aimed spell or hero power leaves between where it sits and where the player is
    /// pointing: a short run of soft dots, faint where it starts and gone before it reaches the
    /// finger (Sam, 2026-10-04).
    ///
    /// <para><b>It trails the pointer; it does not follow it.</b> The aim line this replaces was
    /// deleted for being ugly, and the reasons were that it stuck to the finger and ended in a
    /// literal arrowhead. So the head eases toward a point SHORT of the pointer and dissolves
    /// there: no tip, no arrowhead, no circle. It shows that the path is being tracked and
    /// nothing more.</para>
    ///
    /// <para><b>Over a legal target it fills in the rest of the way to the target, swells,
    /// brightens and turns from yellow to red</b> (Sam, 2026-10-05). Over anything else it does
    /// none of them.</para>
    ///
    /// <para>Built from sprites on <see cref="SpriteMaterials"/> and not a LineRenderer, which
    /// would need a shader found by name; see that class for why that fails in a player.</para>
    /// </summary>
    internal sealed class AimTrail
    {
        private const int Dots = 12;
        /// <summary>How far toward the pointer the head goes when it has nothing to land on.</summary>
        private const float TrailsAt = 0.5f;
        /// <summary>The head's lag behind where it is going.</summary>
        private const float FollowSeconds = 0.09f;
        private const float LockSeconds = 0.10f;
        /// <summary>Sideways bow at mid-path, as a fraction of the path's length.</summary>
        private const float Bow = 0.08f;
        /// <summary>Dots drift toward the head at this many spacings a second.</summary>
        private const float FlowPerSecond = 1.4f;
        /// <summary>World diameter of the dot nearest the head.</summary>
        private const float DotSize = 0.2f;
        private const float IdleAlpha = 0.5f;
        private const float LockedAlpha = 0.85f;
        private const float LockedSwell = 1.15f;
        /// <summary>Below this length the trail is still under the thing it left, so it fades in.</summary>
        private const float FullAtLength = 0.8f;

        /// <summary>Card materials are opaque, so depth is what draws the trail over them. Lifted
        /// along the camera ray, like <see cref="ForkedLightning"/>, so every dot stays over the
        /// board point it was solved for.</summary>
        private const float DepthFraction = 0.06f;
        private const int SortOrder = 80;
        private const int TextureSize = 32;

        private static readonly Color IdleTint = new Color(1f, 0.84f, 0.25f, 1f);
        private static readonly Color LockedTint = new Color(1f, 0.22f, 0.16f, 1f);

        private Transform _root;
        private SpriteRenderer[] _dots;
        private Texture2D _texture;
        private Vector3 _head, _headVelocity;
        private float _lock;
        private bool _live;

        /// <summary>Call every frame of the aim. <paramref name="lockedOn"/> is where a legal
        /// target under the pointer sits, or null when there is none.</summary>
        public void Track(Camera camera, Vector3 origin, Vector3 pointer, Vector3? lockedOn)
        {
            if (camera == null) return;
            Ensure();

            if (!_live)
            {
                _live = true;
                _head = origin;
                _headVelocity = Vector3.zero;
                _lock = 0f;
                _root.gameObject.SetActive(true);
            }

            Vector3 goal = lockedOn ?? Vector3.Lerp(origin, pointer, TrailsAt);
            _head = Vector3.SmoothDamp(_head, goal, ref _headVelocity, FollowSeconds);
            _lock = Mathf.MoveTowards(_lock, lockedOn.HasValue ? 1f : 0f, Time.deltaTime / LockSeconds);

            Vector3 along = _head - origin;
            float length = along.magnitude;
            // Always the same side of the direction of travel, so the bow never flips mid-drag.
            Vector3 side = length > 0.0001f
                ? new Vector3(-along.y, along.x, 0f) / length
                : Vector3.zero;
            Vector3 control = origin + along * 0.5f + side * (length * Bow);

            Vector3 eye = camera.transform.position;
            float presence = Mathf.Clamp01(length / FullAtLength);
            float phase = Mathf.Repeat(Time.time * FlowPerSecond, 1f);

            for (int i = 0; i < Dots; i++)
            {
                float t = (i + phase) / Dots;
                float u = 1f - t;
                Vector3 at = u * u * origin + 2f * u * t * control + t * t * _head;

                // Faint where it leaves and strongest toward the head. Unaimed, the last stretch
                // dissolves so there is no tip; locked, that stretch fills in.
                float body = t * Mathf.Sqrt(t);
                float tip = Mathf.Lerp(1f - Mathf.SmoothStep(0.75f, 1f, t), 1f, _lock);
                float size = DotSize * Mathf.Lerp(0.45f, 1f, t) * Mathf.Lerp(1f, LockedSwell, _lock);

                SpriteRenderer dot = _dots[i];
                dot.transform.position = Vector3.Lerp(at, eye, DepthFraction);
                dot.transform.localScale = new Vector3(size, size, 1f);
                Color c = Color.Lerp(IdleTint, LockedTint, _lock);
                c.a = presence * body * tip * Mathf.Lerp(IdleAlpha, LockedAlpha, _lock);
                dot.color = c;
            }
        }

        public void Hide()
        {
            if (!_live) return;
            _live = false;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            _live = false;
            if (_root != null) Object.Destroy(_root.gameObject);
            if (_texture != null) Object.Destroy(_texture);
            _root = null;
            _dots = null;
            _texture = null;
        }

        private void Ensure()
        {
            // A scene change takes the root with it; the Unity null check is what notices.
            if (_root != null) return;

            _texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            { name = "AimTrailDot", filterMode = FilterMode.Bilinear };
            var pixels = new Color[TextureSize * TextureSize];
            float half = TextureSize * 0.5f;
            for (int y = 0; y < TextureSize; y++)
            for (int x = 0; x < TextureSize; x++)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float falloff = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * TextureSize + x] = new Color(1f, 1f, 1f, falloff * falloff);
            }
            _texture.SetPixels(pixels);
            _texture.Apply();
            // One texel per unit: a dot's localScale is its world diameter.
            Sprite sprite = Sprite.Create(_texture, new Rect(0, 0, TextureSize, TextureSize),
                                          new Vector2(0.5f, 0.5f), TextureSize);

            _root = new GameObject("AimTrail").transform;
            _dots = new SpriteRenderer[Dots];
            for (int i = 0; i < Dots; i++)
            {
                var go = new GameObject("AimTrailDot");
                go.transform.SetParent(_root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                SpriteMaterials.Set(sr, sprite);
                sr.sortingOrder = SortOrder;
                _dots[i] = sr;
            }
            _root.gameObject.SetActive(false);
            _live = false;
        }
    }
}
