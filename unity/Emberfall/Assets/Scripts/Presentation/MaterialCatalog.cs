using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Resolves the shared materials for runtime-built board geometry (M2 D2, shaders/README.md §3).
    // Runtime primitives ship with Unity's built-in Default-Material, whose Standard shader is
    // stripped from URP player builds — every quad renders magenta unless a real URP material is
    // assigned. Editor loads from Assets/Materials directly; builds load through the UI_Core
    // Addressables group ("materials/<name>"), which is what gets the shaders compiled into bundles.
    //
    // Materials are few and always resident; misses are cached and logged once. Callers keep the
    // primitive's default material when null (editor-only fallback so a missing asset degrades
    // visibly rather than throwing).
    public static class MaterialCatalog
    {
        public const string CardBody = "M_CardBody";  // SG_CardComposite
        public const string CardArt  = "M_CardArt";   // URP Unlit — CardPresenter/HeroPlateView push _BaseMap
        public const string Glow     = "M_Glow";      // SG_Glow
        public const string Aegis    = "M_Aegis";     // SG_Aegis
        public const string ManaFill = "M_ManaFill";  // SG_ManaFill — ManaTrayView pushes _FillLevel
        public const string Plate    = "M_Plate";     // URP Unlit — tinted per-instance via _BaseColor
        public const string Dissolve = "M_Dissolve";  // SG_Dissolve — CardDeathDissolve instances it per death

        private static readonly Dictionary<string, Material> _cache = new();

        public static Material Get(string name)
        {
            if (_cache.TryGetValue(name, out Material cached)) return cached;

            Material mat;
#if UNITY_EDITOR
            mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>($"Assets/Materials/{name}.mat");
#else
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Material>($"materials/{name}");
            mat = handle.WaitForCompletion();
#endif
            if (mat == null)
                Debug.LogWarning($"[MaterialCatalog] material '{name}' not found — run Emberfall/Addressables setup after Emberfall/Materials setup");

            _cache[name] = mat;
            return mat;
        }

        /// <summary>Assigns the named material to the renderer; keeps the current one on a miss.</summary>
        public static void Assign(Renderer renderer, string name)
        {
            if (renderer == null) return;
            Material mat = Get(name);
            if (mat != null) renderer.sharedMaterial = mat;
        }
    }
}
