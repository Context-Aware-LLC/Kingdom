using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberfall.Presentation;

namespace Emberfall.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/BoardNetworked.unity from Board.unity (b3 §14 step 4 — the
    /// C1-shaped verify needs a scene that boots NetworkedGameBootstrap). Same hierarchy;
    /// the hot-seat GameBootstrap is swapped for NetworkedGameBootstrap with the serialized
    /// wiring copied 1:1 (the two boots declare identical field names by design). The
    /// serialized serverUrl keeps its default: the live droplet wss endpoint.
    /// Re-runnable: deletes and rebuilds the target scene each time.
    /// </summary>
    public static class CreateNetworkedScene
    {
        private const string Source = "Assets/Scenes/Board.unity";
        private const string Target = "Assets/Scenes/BoardNetworked.unity";

        private static readonly string[] CopiedFields =
        {
            "matchRunner", "boardAnchors", "boardController",
            "inputRouter", "proceduralBoardSetup", "vfxDirector",
        };

        [MenuItem("Emberfall/Net/Create Networked Scene")]
        public static void Run()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.DeleteAsset(Target);
            if (!AssetDatabase.CopyAsset(Source, Target))
            {
                Debug.LogError($"[CreateNetworkedScene] CopyAsset failed ({Source} → {Target}).");
                return;
            }

            var scene = EditorSceneManager.OpenScene(Target, OpenSceneMode.Single);

            var local = Object.FindFirstObjectByType<GameBootstrap>(FindObjectsInactive.Include);
            if (local == null)
            {
                Debug.LogError("[CreateNetworkedScene] copied scene has no GameBootstrap — aborting.");
                return;
            }

            var net = local.gameObject.AddComponent<NetworkedGameBootstrap>();
            var src = new SerializedObject(local);
            var dst = new SerializedObject(net);
            foreach (string field in CopiedFields)
            {
                SerializedProperty from = src.FindProperty(field);
                SerializedProperty to   = dst.FindProperty(field);
                if (from == null || to == null)
                {
                    Debug.LogError($"[CreateNetworkedScene] field '{field}' missing on one side — field names drifted; fix before trusting this scene.");
                    return;
                }
                to.objectReferenceValue = from.objectReferenceValue;
            }
            dst.ApplyModifiedPropertiesWithoutUndo();

            Object.DestroyImmediate(local);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("[CreateNetworkedScene] GREEN — BoardNetworked.unity created: bootstrap swapped, "
                    + "wiring copied, serverUrl = droplet default. Play it to run b3 §14 step 4.");
        }
    }
}
