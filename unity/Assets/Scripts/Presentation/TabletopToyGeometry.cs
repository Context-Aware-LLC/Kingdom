using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    internal static class TabletopToyGeometry
    {
        public static Material Material(string name, Color color, float metal = 0f)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metal);
            material.SetFloat("_Smoothness", 0.32f);
            return material;
        }

        public static GameObject Solid(Transform parent, string name, PrimitiveType shape,
                                       Vector3 position, Vector3 size, Material material)
        {
            var part = GameObject.CreatePrimitive(shape);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = size;
            Collider collider = part.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part;
        }

        public static TextMeshPro Label(Transform parent, string text, Vector3 position,
                                        Vector2 size, float fontSize)
        {
            var go = new GameObject("Printed paper");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            var label = go.AddComponent<TextMeshPro>();
            BoardController.ApplyUiFont(label);
            label.text = text;
            label.fontSize = fontSize;
            label.color = new Color(0.18f, 0.10f, 0.06f);
            label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.sizeDelta = size;
            label.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return label;
        }

        public static void Destroy(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
