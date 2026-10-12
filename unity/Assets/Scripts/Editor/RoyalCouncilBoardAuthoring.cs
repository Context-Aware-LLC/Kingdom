using System;
using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Rebuilds the runtime prefab from the preserved v06 glTF model using Unity APIs.</summary>
    public static class RoyalCouncilBoardAuthoring
    {
        public const string ModelPath = "Assets/Art/Boards/RoyalCouncil/RoyalCouncil.glb";
        public const string PrefabPath = "Assets/Resources/Boards/RoyalCouncil.prefab";
        public const string PlaytestPath = "Assets/Scenes/Dev/RoyalCouncilPlaytest.unity";

        [MenuItem("Kingdom/Board/Open Royal Council Playtest")]
        public static void OpenPlaytest()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play before opening the board test scene.");
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(PlaytestPath)) CreatePlaytest();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(PlaytestPath);
        }

        public static void CreatePlaytest()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
            var root = new GameObject("Royal Council Playtest");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<RoyalCouncilPlaytest>();
            Directory.CreateDirectory("Assets/Scenes/Dev");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, PlaytestPath);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.Refresh();
        }

        [MenuItem("Kingdom/Board/Rebuild Royal Council 3D Prefab")]
        public static void BuildPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (source == null) throw new InvalidOperationException("Royal Council glTF model has not imported.");
            Directory.CreateDirectory("Assets/Resources/Boards");
            Directory.CreateDirectory("Assets/Art/Boards/RoyalCouncil/Materials");
            AssetDatabase.Refresh();
            const string glassPath = "Assets/Art/Boards/RoyalCouncil/Materials/TableGlass.mat";
            Material glass = AssetDatabase.LoadAssetAtPath<Material>(glassPath);
            if (glass == null)
            {
                glass = new Material(Shader.Find("Kingdom/Royal Council Glass")) { name = "Table Glass", enableInstancing = true };
                AssetDatabase.CreateAsset(glass, glassPath);
            }
            var root = new GameObject("Royal Council");
            try
            {
                var board = root.AddComponent<RoyalCouncilBoard>();
                var serialized = new SerializedObject(board);
                serialized.FindProperty("glassMaterial").objectReferenceValue = glass;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var model = UnityEngine.Object.Instantiate(source, root.transform);
                model.name = "Royal Council Model";
                // glTFast changes handedness by reflecting X. This proper rotation restores
                // the approved seat orientation and lays the surface in the existing XY plane.
                model.transform.localRotation = Quaternion.AngleAxis(-90f, Vector3.right) * Quaternion.AngleAxis(180f, Vector3.up);
                model.transform.localPosition = new Vector3(0f, 0f, 0.66f);
                model.transform.localScale = Vector3.one * RoyalCouncilBoard.ModelScale;
                RefitTimerPlatform(model);
                foreach (Collider collider in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RoyalCouncil] Runtime prefab rebuilt from v06; 48 semantic mounts retained.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void RefitTimerPlatform(GameObject model)
        {
            // v06 deliberately retained the larger v05 platform in the merged rail mesh.
            // These vertices are the isolated circular platform (radius <= .252 m); the next
            // furniture starts at .276 m. Resize its footprint, preserving height and UVs.
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.name != "RC.Static.Wood.Rail") continue;
                Mesh mesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
                mesh.name = "Royal Council fitted timer platform";
                Vector3[] vertices = mesh.vertices;
                int changed = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 p = vertices[i];
                    var delta = new Vector2(p.x - 1.39f, p.z);
                    if (delta.magnitude >= 0.266f) continue;
                    p.x = 1.39f + delta.x * 0.64f;
                    p.z = delta.y * 0.64f;
                    vertices[i] = p; changed++;
                }
                if (changed < 500) throw new InvalidOperationException("Timer platform geometry changed; review the v06 resize region.");
                mesh.vertices = vertices; mesh.RecalculateBounds();
                const string path = "Assets/Art/Boards/RoyalCouncil/TimerPlatformRail.asset";
                Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null) { EditorUtility.CopySerialized(mesh, existing); UnityEngine.Object.DestroyImmediate(mesh); mesh = existing; }
                else AssetDatabase.CreateAsset(mesh, path);
                filter.sharedMesh = mesh;
            }
        }
    }
}
