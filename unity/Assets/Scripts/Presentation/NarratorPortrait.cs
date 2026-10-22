using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// An animated character in a UI Toolkit slot. The character, camera, lights and texture
    /// live only as long as the presentation. Uses a uGUI RawImage because live render-texture
    /// backgrounds in UI Toolkit have lost layers on Android (see CardFaceBaker).
    /// </summary>
    public sealed class NarratorPortrait : MonoBehaviour
    {
        public const string HeraldAddress = "characters/RoyalHeraldAnnouncer";
        public const string HeraldPrefabPath = "Assets/Characters/RoyalHerald/RoyalHeraldAnnouncer.prefab";
        public const string CaptureLayerName = "NarratorCapture";

        private static readonly HashSet<int> StageSlots = new HashSet<int>();

        private VisualElement _slot;
        private Action<bool> _onVisibilityChanged;
        private AsyncOperationHandle<GameObject> _load;
        private GameObject _stage;
        private Camera _camera;
        private RawImage _picture;
        private RenderTexture _target;
        private Bounds _bounds;
        private float _nextFrame;
        private float _appearedAt;
        private bool _closed;
        private int _stageSlot = -1;

        /// <summary>Load without blocking the scroll or its dismissal. A failed load leaves the
        /// ordinary text lesson usable. The caller owns Close; detachment also closes the view.</summary>
        public static NarratorPortrait Show(VisualElement slot, string address, Action<bool> onVisibilityChanged)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));
            var host = new GameObject("~NarratorPortrait");
            var view = host.AddComponent<NarratorPortrait>();
            view._slot = slot;
            view._onVisibilityChanged = onVisibilityChanged;
            slot.RegisterCallback<DetachFromPanelEvent>(view.OnDetached);
            view.StartCoroutine(view.Load(address));
            return view;
        }

        private IEnumerator Load(string address)
        {
            _load = Addressables.LoadAssetAsync<GameObject>(address);
            yield return _load;
            if (_closed) yield break;
            if (_load.Status != AsyncOperationStatus.Succeeded || _load.Result == null)
            {
                Debug.LogWarning($"[NarratorPortrait] Could not load '{address}'; the lesson remains available.");
                Close();
                yield break;
            }

            try
            {
                Build(_load.Result);
                _appearedAt = Time.unscaledTime;
                _onVisibilityChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NarratorPortrait] Could not prepare '{address}': {ex.Message}");
                Close();
            }
        }

        private void Build(GameObject prefab)
        {
            int layer = LayerMask.NameToLayer(CaptureLayerName);
            if (layer < 0) throw new InvalidOperationException($"Missing layer '{CaptureLayerName}'.");

            _stage = new GameObject("Character stage");
            _stage.transform.SetParent(transform, false);
            // Outside gameplay/menu camera frusta. No changes to their masks, lights or scene.
            _stageSlot = 0;
            while (!StageSlots.Add(_stageSlot)) _stageSlot++;
            _stage.transform.position = new Vector3(_stageSlot * 32f, -10000f, 0f);
            GameObject actor = Instantiate(prefab, _stage.transform);
            foreach (Transform node in actor.GetComponentsInChildren<Transform>(true))
                node.gameObject.layer = layer;
            foreach (Animator animator in actor.GetComponentsInChildren<Animator>())
            {
                animator.applyRootMotion = false;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Update(0f);
            }
            _bounds = Measure(actor);
            if (!(_bounds.size.y > 0f) || float.IsInfinity(_bounds.size.y))
                throw new InvalidOperationException("Character has no finite visible bounds.");

            var cameraObject = new GameObject("Portrait camera");
            cameraObject.transform.SetParent(_stage.transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.clear;
            _camera.cullingMask = 1 << layer;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.nearClipPlane = 0.01f;
            _camera.farClipPlane = _bounds.size.y * 6f;
            _camera.transform.position = _bounds.center + Vector3.forward * (_bounds.size.y * 3f);
            _camera.transform.LookAt(_bounds.center);

            // Local lights avoid illuminating the menu or changing global RenderSettings.
            AddLight("Warm key", new Vector3(-1f, 1f, 1.5f), new Color(1f, 0.92f, 0.8f), 5f, layer);
            AddLight("Cool fill", new Vector3(1f, 0.5f, 1f), new Color(0.72f, 0.82f, 1f), 2f, layer);

            var canvasObject = new GameObject("Portrait canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1; // menu panels are 0; settings and opening film remain above it
            var picture = new GameObject("Portrait", typeof(RectTransform), typeof(RawImage));
            picture.transform.SetParent(canvasObject.transform, false);
            _picture = picture.GetComponent<RawImage>();
            _picture.raycastTarget = false;
            _picture.enabled = false;
            _picture.rectTransform.anchorMin = new Vector2(0f, 1f);
            _picture.rectTransform.anchorMax = new Vector2(0f, 1f);
            _picture.rectTransform.pivot = new Vector2(0f, 1f);
        }

        private void AddLight(string label, Vector3 offset, Color color, float intensity, int layer)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_stage.transform, false);
            go.transform.position = _bounds.center + offset * _bounds.size.y;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = _bounds.size.y * 5f;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << layer;
        }

        private static Bounds Measure(GameObject actor)
        {
            Bounds bounds = default;
            bool first = true;
            var baked = new Mesh();
            try
            {
                foreach (SkinnedMeshRenderer skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.updateWhenOffscreen = true;
                    // The scale-aware bake compensates for FBX import transforms and produces
                    // renderer-local vertices that TransformPoint maps to the visible mesh.
                    skin.BakeMesh(baked, true);
                    foreach (Vector3 vertex in baked.vertices)
                    {
                        Vector3 point = skin.transform.TransformPoint(vertex);
                        if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
                        else bounds.Encapsulate(point);
                    }
                    baked.Clear();
                }
                foreach (MeshRenderer mesh in actor.GetComponentsInChildren<MeshRenderer>())
                {
                    if (first) { bounds = mesh.bounds; first = false; }
                    else bounds.Encapsulate(mesh.bounds);
                }
            }
            finally { Destroy(baked); }
            return bounds;
        }

        private void LateUpdate()
        {
            if (_closed || _picture == null) return;
            if (_slot.panel == null) { Close(); return; }
            Rect rect = _slot.worldBound;
            bool visible = _slot.visible && rect.width > 1f && rect.height > 1f;
            for (VisualElement element = _slot; visible && element != null; element = element.parent)
                visible = element.resolvedStyle.display != DisplayStyle.None && element.resolvedStyle.opacity > 0f;
            _stage.SetActive(visible);
            _picture.enabled = visible;
            if (!visible) return;

            // Convert the panel's scaled, top-left coordinates to the screen-overlay canvas.
            Vector2 topLeft = RuntimePanelUtils.ScreenToPanel(_slot.panel, Vector2.zero);
            Vector2 bottomRight = RuntimePanelUtils.ScreenToPanel(_slot.panel, new Vector2(Screen.width, Screen.height));
            Vector2 span = bottomRight - topLeft;
            if (!(span.x > 0f && span.y > 0f)) { _picture.enabled = false; return; }
            Vector2 scale = new Vector2(Screen.width / span.x, Screen.height / span.y);
            Vector2 position = Vector2.Scale(rect.position - topLeft, scale);
            Vector2 size = Vector2.Scale(rect.size, scale);
            _picture.rectTransform.anchoredPosition = new Vector2(position.x, -position.y);
            _picture.rectTransform.sizeDelta = size;
            _picture.color = new Color(1f, 1f, 1f, Mathf.Clamp01((Time.unscaledTime - _appearedAt) / 0.25f));

            // Bound the mobile render cost and keep resizing from allocating every frame.
            float resolutionScale = Mathf.Min(1f, 768f / Mathf.Max(size.x, size.y));
            int width = Mathf.Max(16, Mathf.RoundToInt(size.x * resolutionScale / 16f) * 16);
            int height = Mathf.Max(16, Mathf.RoundToInt(size.y * resolutionScale / 16f) * 16);
            if (_target == null || !_target.IsCreated() || _target.width != width || _target.height != height)
            {
                ReleaseTarget();
                _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Narrator portrait", filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp, antiAliasing = 1
                };
                if (!_target.Create())
                {
                    Debug.LogWarning("[NarratorPortrait] Could not allocate the portrait texture; retaining the text lesson.");
                    Close();
                    return;
                }
                _camera.targetTexture = _target;
                _picture.texture = _target;
                _camera.aspect = (float)width / height;
                _camera.orthographicSize = Mathf.Max(_bounds.extents.y, _bounds.extents.x / _camera.aspect) * 1.12f;
                _nextFrame = 0f;
            }
            if (Time.unscaledTime < _nextFrame) return;
            try { _camera.Render(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NarratorPortrait] Could not render the portrait: {ex.Message}");
                Close();
                return;
            }
            _nextFrame = Time.unscaledTime + 1f / 30f;
        }

        /// <summary>Immediately hide and stop animation; release the loaded model and GPU texture.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            NotifyClosed();
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        private void OnDetached(DetachFromPanelEvent evt) => Close();

        private void NotifyClosed()
        {
            Action<bool> changed = _onVisibilityChanged;
            _onVisibilityChanged = null;
            changed?.Invoke(false);
        }

        private void ReleaseTarget()
        {
            if (_camera != null) _camera.targetTexture = null;
            if (_picture != null) _picture.texture = null;
            if (_target == null) return;
            _target.Release();
            Destroy(_target);
            _target = null;
        }

        private void OnDestroy()
        {
            _closed = true;
            _slot?.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            NotifyClosed();
            ReleaseTarget();
            if (_stageSlot >= 0) StageSlots.Remove(_stageSlot);
            if (_load.IsValid()) Addressables.Release(_load);
        }
    }
}
