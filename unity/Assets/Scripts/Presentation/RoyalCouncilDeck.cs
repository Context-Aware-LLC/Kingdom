using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>One combined edge mesh per stack, topped by the owner's public card-back art.</summary>
    public sealed class RoyalCouncilDeck : MonoBehaviour, IPointerHoverTarget
    {
        private MeshFilter _source;
        private Renderer _sourceRenderer;
        private Mesh _stack;
        private Renderer _stackRenderer;
        private Transform _top;
        private MeshFilter _topFilter;
        private Renderer _topRenderer;
        private Mesh _backMesh;
        private Sprite _backSprite;
        private MaterialPropertyBlock _backProperties;
        private Vector3 _topRest;
        private Transform _drawAnchor;
        private TextMeshPro _label;
        private BoxCollider _hit;
        private float _step;
        private Vector3 _stackDirection;
        private int _count = -1;
        public int Count => _count;

        public void Initialize(Transform card, Transform top, float modelScale)
        {
            _source = card.GetComponent<MeshFilter>();
            _sourceRenderer = card.GetComponent<Renderer>();
            _sourceRenderer.enabled = false;
            _top = top; _topRest = top.position; _step = 0.004f * modelScale;
            _topFilter = top.GetComponent<MeshFilter>();
            _topRenderer = top.GetComponent<Renderer>();
            // Imported card thickness is local Y. Follow the authored rack orientation so
            // a recessed, upright deck grows within its slot as the visible count changes.
            _stackDirection = card.up.normalized;
            _stack = new Mesh { name = "Live deck edges" };
            gameObject.AddComponent<MeshFilter>().sharedMesh = _stack;
            _stackRenderer = gameObject.AddComponent<MeshRenderer>();
            _stackRenderer.sharedMaterials = _sourceRenderer.sharedMaterials;
            _hit = gameObject.AddComponent<BoxCollider>();
            var label = new GameObject("Deck count");
            label.transform.SetParent(transform, false);
            _label = label.AddComponent<TextMeshPro>();
            BoardController.ApplyUiFont(_label);
            _label.fontSize = 5f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.rectTransform.sizeDelta = new Vector2(2.1f, 0.9f);
            _label.GetComponent<Renderer>().sortingOrder = 30;
            _label.gameObject.SetActive(false);
        }

        public void SetDrawAnchor(Transform anchor) => _drawAnchor = anchor;

        /// <summary>Dress the existing top card without changing the stack's geometry or count.</summary>
        public void SetCardBack(Sprite sprite)
        {
            if (sprite == null || sprite == _backSprite) return;
            Material art = MaterialCatalog.Get(MaterialCatalog.CardArt);
            if (art == null) return;

            if (_backMesh == null)
            {
                // The imported top is a thin box with packed box UVs. The upright rack puts
                // local Z across the card and -X up it. Map that face to one complete image
                // on a private mesh; the shared model and gold rim stay intact.
                _backMesh = Instantiate(_topFilter.sharedMesh);
                _backMesh.name = "Deck card-back face";
                Bounds bounds = _backMesh.bounds;
                Vector3[] vertices = _backMesh.vertices;
                var uv = new Vector2[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    uv[i] = new Vector2(
                        Mathf.InverseLerp(bounds.min.z, bounds.max.z, vertices[i].z),
                        1f - Mathf.InverseLerp(bounds.min.x, bounds.max.x, vertices[i].x));
                _backMesh.uv = uv;
                _topFilter.sharedMesh = _backMesh;

                Material[] materials = _topRenderer.sharedMaterials;
                materials[0] = art;
                _topRenderer.sharedMaterials = materials;
                _backProperties = new MaterialPropertyBlock();
            }

            Rect rect = sprite.textureRect;
            Texture2D texture = sprite.texture;
            _backProperties.SetTexture("_BaseMap", texture);
            _backProperties.SetColor("_BaseColor", Color.white);
            _backProperties.SetVector("_BaseMap_ST", new Vector4(
                rect.width / texture.width, rect.height / texture.height,
                rect.x / texture.width, rect.y / texture.height));
            // Only the card-back submesh changes; the second submesh keeps its metal border.
            _topRenderer.SetPropertyBlock(_backProperties, 0);
            _backSprite = sprite;
        }

        public void SetCount(int count)
        {
            count = Mathf.Max(0, count);
            if (_count == count) return;
            _count = count;
            _top.gameObject.SetActive(count > 0);
            _stackRenderer.enabled = count > 0;
            _label.text = count.ToString();
            if (count == 0) _label.gameObject.SetActive(false);
            int visible = Mathf.Min(40, count);
            _top.position = _topRest + _stackDirection * (Mathf.Max(0, visible - 1) * _step);
            _stack.Clear();
            if (visible > 0)
            {
                var cards = new CombineInstance[visible];
                for (int i = 0; i < visible; i++)
                    cards[i] = new CombineInstance { mesh = _source.sharedMesh, transform = transform.worldToLocalMatrix * Matrix4x4.Translate(_stackDirection * (_step * i)) * _source.transform.localToWorldMatrix };
                _stack.CombineMeshes(cards, true, true, false);
            }
            Bounds bounds = _sourceRenderer.bounds;
            bounds.Encapsulate(_topRenderer.bounds);
            _hit.center = transform.InverseTransformPoint(bounds.center);
            _hit.size = new Vector3(bounds.size.x, bounds.size.y, Mathf.Max(0.12f, bounds.size.z));
            _label.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - 0.1f);
            if (_drawAnchor != null) _drawAnchor.position = _label.transform.position;
        }

        void IPointerHoverTarget.OnPointerEnter() { if (_count > 0) _label.gameObject.SetActive(true); }
        void IPointerHoverTarget.OnPointerExit() => _label.gameObject.SetActive(false);
        private void OnDestroy()
        {
            if (Application.isPlaying)
            {
                if (_stack != null) Destroy(_stack);
                if (_backMesh != null) Destroy(_backMesh);
            }
            else
            {
                if (_stack != null) DestroyImmediate(_stack);
                if (_backMesh != null) DestroyImmediate(_backMesh);
            }
        }
    }
}
