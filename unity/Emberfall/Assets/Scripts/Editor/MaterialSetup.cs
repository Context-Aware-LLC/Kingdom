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

            AssetDatabase.SaveAssets();
            Debug.Log("[MaterialSetup] shared materials ready under Assets/Materials");
        }

        private static void Create(string name, string shaderName)
        {
            string path = $"{Folder}/{name}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;

            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[MaterialSetup] shader '{shaderName}' not found — {name}.mat NOT created");
                return;
            }
            AssetDatabase.CreateAsset(new Material(shader), path);
            Debug.Log($"[MaterialSetup] created {path} ({shaderName})");
        }
    }
}
