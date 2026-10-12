using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>One combined edge mesh per stack; only the redacted count enters this view.</summary>
    public sealed class RoyalCouncilDeck : MonoBehaviour, IPointerHoverTarget
    {
        private MeshFilter _source;
        private Renderer _sourceRenderer;
        private Mesh _stack;
        private Renderer _stackRenderer;
        private Transform _top;
        private Vector3 _topRest;
        private Transform _drawAnchor;
        private TextMeshPro _label;
        private BoxCollider _hit;
        private float _step;
        private int _count = -1;
        public int Count => _count;

        public void Initialize(Transform card, Transform top, float modelScale)
        {
            _source = card.GetComponent<MeshFilter>();
            _sourceRenderer = card.GetComponent<Renderer>();
            _sourceRenderer.enabled = false;
            _top = top; _topRest = top.position; _step = 0.004f * modelScale;
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
            _top.position = _topRest + Vector3.back * (Mathf.Max(0, visible - 1) * _step);
            _stack.Clear();
            if (visible > 0)
            {
                var cards = new CombineInstance[visible];
                for (int i = 0; i < visible; i++)
                    cards[i] = new CombineInstance { mesh = _source.sharedMesh, transform = transform.worldToLocalMatrix * Matrix4x4.Translate(Vector3.back * (_step * i)) * _source.transform.localToWorldMatrix };
                _stack.CombineMeshes(cards, true, true, false);
            }
            Bounds bounds = _sourceRenderer.bounds;
            bounds.Encapsulate(_top.GetComponent<Renderer>().bounds);
            _hit.center = transform.InverseTransformPoint(bounds.center);
            _hit.size = new Vector3(bounds.size.x, bounds.size.y, Mathf.Max(0.12f, bounds.size.z));
            _label.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - 0.1f);
            if (_drawAnchor != null) _drawAnchor.position = _label.transform.position;
        }

        void IPointerHoverTarget.OnPointerEnter() { if (_count > 0) _label.gameObject.SetActive(true); }
        void IPointerHoverTarget.OnPointerExit() => _label.gameObject.SetActive(false);
        private void OnDestroy() { if (_stack != null) Destroy(_stack); }
    }
}
