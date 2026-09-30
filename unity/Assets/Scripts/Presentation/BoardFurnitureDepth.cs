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
            // Leave a small air gap behind the painted quad. Putting the cube's front cap at
            // exactly z=0 makes it coplanar with the face and lets the depth buffer choose which
            // one wins frame-to-frame (most visible as a dark flicker inside green buttons).
            const float faceClearance = 0.015f;
            go.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f + faceClearance);
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
