using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// M2 D2 (shader-to-material wiring): creates the shared .mat assets under Assets/Materials
    /// that MaterialCatalog serves to the runtime-built board geometry. Idempotent — existing
    /// materials are left untouched so hand-tuned inspector values survive re-runs.
    /// Run Emberfall/Addressables/Setup afterwards to sync the folder into the UI_Core group
    /// (that reference is what compiles the SG shaders into player builds).
    /// </summary>
    public static class MaterialSetup
    {
        private const string Folder = "Assets/Materials";

        [MenuItem("Emberfall/Materials/Create Shared Materials")]
        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "Materials");

            Create("M_CardBody", "Shader Graphs/SG_CardComposite");
            Create("M_CardArt",  "Universal Render Pipeline/Unlit");
            Create("M_Glow",     "Shader Graphs/SG_Glow");
            Create("M_Aegis",    "Shader Graphs/SG_Aegis");
            Create("M_ManaFill", "Shader Graphs/SG_ManaFill");
            Create("M_Plate",    "Universal Render Pipeline/Unlit");

            // Noise scale is tuned to the board, not to the graph default. A card is 137x194 px
            // on screen (ortho 4.6 => ~117 px/world unit), so SG_Dissolve's default _NoiseScale of
            // 20 puts ~7 px between noise cells and the burn reads as fizz rather than as fire.
            // 12 gives a cell the eye can resolve; the wider edge carries the ember colour.
            // Emberfall/SG_Dissolve is the hand-HLSL shader, NOT Shader Graphs/SG_Dissolve —
            // the .shadergraph has never compiled (see the .shader header) and any material
            // built on it renders magenta.
            Create("M_Dissolve", "Emberfall/SG_Dissolve", m =>
            {
                m.SetFloat("_NoiseScale", 12f);
                m.SetFloat("_EdgeWidth",  0.08f);
            });

            AssetDatabase.SaveAssets();
            Debug.Log("[MaterialSetup] shared materials ready under Assets/Materials");
        }

        // configure runs ONLY on first creation, for the same reason the whole method is a no-op on
        // an existing asset: hand-tuned inspector values have to survive a re-run.
        private static void Create(string name, string shaderName, System.Action<Material> configure = null)
        {
            string path = $"{Folder}/{name}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;

            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[MaterialSetup] shader '{shaderName}' not found — {name}.mat NOT created");
                return;
            }
            var mat = new Material(shader);
            configure?.Invoke(mat);
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"[MaterialSetup] created {path} ({shaderName})");
        }
    }
}
