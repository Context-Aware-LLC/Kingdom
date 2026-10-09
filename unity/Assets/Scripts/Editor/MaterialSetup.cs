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
            Create("M_CardEdge", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.075f, 0.04f, 0.035f, 1f));
                m.SetFloat("_Metallic", 0.05f);
                m.SetFloat("_Smoothness", 0.36f);
                m.enableInstancing = true;
            });
            Create("M_CardArt",  "Universal Render Pipeline/Unlit");
            Create("M_Glow",     "Shader Graphs/SG_Glow");
            Create("M_Aegis", "Emberfall/AegisDome", m =>
            {
                m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/Art/UI/Board/effect_aegis_golden_dome.png"));
            });
            Create("M_ManaFill", "Shader Graphs/SG_ManaFill");
            Create("M_Plate",    "Universal Render Pipeline/Unlit");

            // Noise scale is tuned to the board, not to the graph default. A card is 137x194 px
            // on screen (ortho 4.6 => ~117 px/world unit), so SG_Dissolve's default _NoiseScale of
            // 20 puts ~7 px between noise cells and the burn reads as fizz rather than as fire.
            // 12 gives a cell the eye can resolve; the wider edge carries the ember colour.
            // Emberfall/SG_Dissolve is the hand-HLSL shader, NOT Shader Graphs/SG_Dissolve —
            // the .shader is transparent where the graph is opaque with alpha clip (see the
            // .shader header for why that matters on the burn's first frame).
            Create("M_Dissolve", "Emberfall/SG_Dissolve", m =>
            {
                m.SetFloat("_NoiseScale", 12f);
                m.SetFloat("_EdgeWidth",  0.08f);
            });

            // Graph-backed like M_CardBody and M_Glow; verified in-editor 2026-07-26 —
            // SG_Frost compiles and the material carries all 13 of its properties.
            //
            // NEVER HAND-AUTHOR A .mat FOR A SHADER GRAPH. A graph's shader is a generated
            // SUB-ASSET, so m_Shader needs the sub-asset's fileID, not the 4800000 a plain
            // .shader uses. Get that number wrong and the guid still resolves, Unity falls back to
            // Hidden/InternalErrorShader without a word, and every SetFloat below silently misses.
            // Let this menu item author it — Shader.Find hands Unity the reference to write.
            Create("M_Frost", "Shader Graphs/SG_Frost", m =>
            {
                m.SetFloat("_FrostAmount",     0f);     // CardMaterialBinder tweens this 0<->1
                m.SetFloat("_FrostOpacity",    0.85f);  // graph defaults, carried deliberately
                m.SetFloat("_RefractStrength", 0.02f);
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
