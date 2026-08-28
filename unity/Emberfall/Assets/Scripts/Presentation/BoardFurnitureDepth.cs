using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Creates the shared shallow backing used by cards and world-space board HUD.</summary>
    internal static class BoardFurnitureDepth
    {
        internal static Renderer Create(
            Transform parent,
            string name,
            Vector2 faceSize,
            float depth,
            float inset = 0.96f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f);
            go.transform.localScale = new Vector3(faceSize.x * inset, faceSize.y * inset, depth);

            Collider generatedCollider = go.GetComponent<Collider>();
            if (generatedCollider != null)
            {
                if (Application.isPlaying) Object.Destroy(generatedCollider);
                else Object.DestroyImmediate(generatedCollider);
            }

            Renderer renderer = go.GetComponent<Renderer>();
            MaterialCatalog.Assign(renderer, MaterialCatalog.CardEdge);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            return renderer;
        }
    }
}
