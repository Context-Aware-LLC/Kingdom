using System;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Server-driven Royal Council turn hourglass. It replaces the literal TURN/ROPE counter with
    /// a physical object on the left fixture: procedural sand drains against the absolute deadline,
    /// and the glass rocks and burns red during the final ten seconds. The server remains the only
    /// authority; every animation here is cosmetic.
    /// </summary>
    public sealed class TurnClockView : MonoBehaviour
    {
        public static readonly Vector3 BoardPosition = new(-6.3f, 0.6f, 0f);
        public static readonly Vector2 ArtworkUv =
            new(435f / 4612f, 1f - 942f / 2080f);

        private static readonly Color SandColor = new(0.96f, 0.63f, 0.16f, 1f);
        private static readonly Color RopeSandColor = new(1.00f, 0.20f, 0.08f, 1f);
        private static readonly Color RopeBodyTint = new(1.00f, 0.72f, 0.62f, 1f);
        private const string HourglassSpriteKey = "turn-hourglass-royal-council";
        private const long RopeWindowMs = 10_000;
        private const float HourglassW = 2.84f;
        private const float HourglassH = 4.40f;
        private const float SandHalfW = 0.38f;
        private const float SandNeckY = 0.08f;
        private const float SandTopH = 0.80f;
        private const float SandBottomY = -0.96f;
        private const float SandBottomH = 0.78f;
        private const float VisualOffsetX = 0.58f;
        private const float VisualOffsetY = 0.28f;
        private const int SandOrder = 11;
        private const int BodyOrder = 12;

        private static readonly int[] TriangleIndices = { 0, 1, 2 };
        private static readonly int[] QuadIndices = { 0, 1, 2, 0, 2, 3 };
        private static readonly Vector2[] TriangleUvs = { Vector2.zero, Vector2.up, Vector2.one };
        private static readonly Vector2[] QuadUvs =
            { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };

        private SpriteRenderer _body;
        private Transform _visualRoot;
        private MeshRenderer _topSandRenderer;
        private MeshRenderer _bottomSandRenderer;
        private MeshRenderer _streamRenderer;
        private Mesh _topSand;
        private Mesh _bottomSand;
        private Mesh _stream;
        private readonly Vector3[] _topVertices = new Vector3[3];
        private readonly Vector3[] _bottomVertices = new Vector3[3];
        private readonly Vector3[] _streamVertices = new Vector3[4];
        private MaterialPropertyBlock _mpb;
        private Func<long> _nowMs;
        private long? _deadlineMs;
        private long _durationMs;
        private float _flipStartedAt = -1f;

        public void Init(Func<long> nowMs = null, IUiSpriteProvider uiSprites = null)
        {
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _mpb = new MaterialPropertyBlock();

            var visualGo = new GameObject("HourglassVisual");
            visualGo.transform.SetParent(transform, false);
            visualGo.transform.localPosition = new Vector3(VisualOffsetX, VisualOffsetY, 0f);
            _visualRoot = visualGo.transform;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            _topSandRenderer = CreateSandMesh("TopSand", out _topSand);
            _bottomSandRenderer = CreateSandMesh("BottomSand", out _bottomSand);
            _streamRenderer = CreateSandMesh("SandStream", out _stream);
            InitializeTriangle(_topSand);
            InitializeTriangle(_bottomSand);
            InitializeQuad(_stream);
            _body = CreateBody(uiSprites.Get(HourglassSpriteKey));
            UpdateSand(1f, false);
            SetVisible(false);
        }

        public void OnTurnTimer(TurnTimer timer)
        {
            long now = _nowMs();
            _deadlineMs = timer.DeadlineMs;
            _durationMs = Math.Max(1, timer.DeadlineMs - now);
            _flipStartedAt = Time.unscaledTime;
            SetVisible(true);
        }

        public void OnStateUpdated(GameState state)
        {
            if (state == null) return;
            if (state.Phase == TurnPhase.Mulligan || state.Outcome != MatchOutcome.InProgress)
            {
                _deadlineMs = null;
                SetVisible(false);
            }
        }

        private void Update()
        {
            if (_deadlineMs == null || _body == null) return;

            long remaining = Math.Max(0, _deadlineMs.Value - _nowMs());
            float fraction = Mathf.Clamp01(remaining / (float)Math.Max(1, _durationMs));
            bool rope = remaining <= RopeWindowMs;
            UpdateSand(fraction, rope);

            float now = Time.unscaledTime;
            float wobble = rope ? Mathf.Sin(now * 12f) * 3.1f : Mathf.Sin(now * 1.35f) * 0.45f;
            float flip = 0f;
            if (_flipStartedAt >= 0f)
            {
                float k = (now - _flipStartedAt) / 0.52f;
                if (k < 1f) flip = Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI) * 17f;
                else _flipStartedAt = -1f;
            }
            _visualRoot.localRotation = Quaternion.Euler(0f, 0f, flip + wobble);
            _body.color = rope ? RopeBodyTint : Color.white;
        }

        private SpriteRenderer CreateBody(Sprite sprite)
        {
            var go = new GameObject("HourglassBody");
            go.transform.SetParent(_visualRoot, false);
            go.transform.localPosition = new Vector3(0f, 0.12f, -0.02f);
            var sr = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(sr, sprite);
            sr.sortingOrder = BodyOrder;
            if (sprite != null)
            {
                Vector2 native = sprite.bounds.size;
                go.transform.localScale = new Vector3(HourglassW / native.x, HourglassH / native.y, 1f);
            }
            else
            {
                Debug.LogWarning($"[TurnClockView] no UI sprite '{HourglassSpriteKey}'.");
            }
            return sr;
        }

        private MeshRenderer CreateSandMesh(string name, out Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_visualRoot, false);
            go.transform.localPosition = new Vector3(0f, 0.04f, -0.01f);
            var filter = go.AddComponent<MeshFilter>();
            mesh = new Mesh { name = name };
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            MaterialCatalog.Assign(renderer, MaterialCatalog.Plate);
            renderer.sortingOrder = SandOrder;
            return renderer;
        }

        private void UpdateSand(float topFraction, bool rope)
        {
            float bottomFraction = 1f - topFraction;
            float topY = SandNeckY + SandTopH * topFraction;
            float topW = SandHalfW * Mathf.Sqrt(topFraction);
            SetTriangle(_topSand, _topVertices,
                new Vector3(0f, SandNeckY, 0f),
                new Vector3(-topW, topY, 0f),
                new Vector3(topW, topY, 0f));

            float bottomW = SandHalfW * Mathf.Sqrt(bottomFraction);
            float bottomApex = SandBottomY + SandBottomH * bottomFraction;
            SetTriangle(_bottomSand, _bottomVertices,
                new Vector3(-bottomW, SandBottomY, 0f),
                new Vector3(bottomW, SandBottomY, 0f),
                new Vector3(0f, bottomApex, 0f));

            float streamBottom = Mathf.Max(SandBottomY + 0.02f, bottomApex);
            SetQuad(_stream, _streamVertices,
                new Vector3(-0.028f, SandNeckY, 0f),
                new Vector3(0.028f, SandNeckY, 0f),
                new Vector3(0.028f, streamBottom, 0f),
                new Vector3(-0.028f, streamBottom, 0f));
            _streamRenderer.gameObject.SetActive(topFraction > 0.002f && topFraction < 0.998f);

            Color color = rope ? RopeSandColor : SandColor;
            Tint(_topSandRenderer, color);
            Tint(_bottomSandRenderer, color);
            Tint(_streamRenderer, color);
        }

        private static void InitializeTriangle(Mesh mesh)
        {
            mesh.vertices = new Vector3[3];
            mesh.uv = TriangleUvs;
            mesh.triangles = TriangleIndices;
        }

        private static void InitializeQuad(Mesh mesh)
        {
            mesh.vertices = new Vector3[4];
            mesh.uv = QuadUvs;
            mesh.triangles = QuadIndices;
        }

        private static void SetTriangle(
            Mesh mesh, Vector3[] vertices, Vector3 a, Vector3 b, Vector3 c)
        {
            vertices[0] = a;
            vertices[1] = b;
            vertices[2] = c;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private static void SetQuad(
            Mesh mesh, Vector3[] vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            vertices[0] = a;
            vertices[1] = b;
            vertices[2] = c;
            vertices[3] = d;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private void Tint(Renderer renderer, Color color)
        {
            if (renderer == null) return;
            renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", color);
            _mpb.SetColor("_Color", color);
            renderer.SetPropertyBlock(_mpb);
        }

        private void SetVisible(bool visible)
        {
            if (_body != null) _body.gameObject.SetActive(visible);
            if (_topSandRenderer != null) _topSandRenderer.gameObject.SetActive(visible);
            if (_bottomSandRenderer != null) _bottomSandRenderer.gameObject.SetActive(visible);
            if (_streamRenderer != null) _streamRenderer.gameObject.SetActive(visible);
            if (!visible && _visualRoot != null) _visualRoot.localRotation = Quaternion.identity;
        }
    }
}
