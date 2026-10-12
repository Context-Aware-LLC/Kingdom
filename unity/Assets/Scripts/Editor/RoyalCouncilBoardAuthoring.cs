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
                RefitControls(model);
                foreach (Collider collider in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RoyalCouncil] Runtime prefab rebuilt from v06; 48 semantic mounts retained.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private readonly struct ControlFit
        {
            public readonly Vector2 From, To;
            public readonly float Radius, PlatformScale;
            public readonly string Mount;
            public readonly string[] Props;

            public ControlFit(Vector2 from, Vector2 to, float radius, float platformScale,
                string mount, params string[] props)
            {
                From = from; To = to; Radius = radius; PlatformScale = platformScale;
                Mount = mount; Props = props;
            }
        }

        private static void RefitControls(GameObject model)
        {
            // Swap the two right-side controls so the tall stamper sits inboard of the
            // mana rail. Keep the hourglass and all prop sizes in their approved positions.
            // Work from the untouched v06 model on every rebuild, never from fitted vertices.
            var fits = new[]
            {
                new ControlFit(new Vector2(9.12f, 0f), new Vector2(7.56f, 0f), 0.73f, 1f,
                    "RC.Mount.EndTurn.Stamper", "RC.Prop.Stamper"),
                new ControlFit(new Vector2(7.56f, 0f), new Vector2(9.12f, 0f), 0.81f, 1f,
                    "RC.Mount.EndTurn.Seal", "RC.Prop.WaxSeal"),
                // v06 kept the oversized v05 clock platform; retain its earlier .64 refit.
                new ControlFit(new Vector2(-8.34f, 0f), new Vector2(-8.34f, 0f), 1.596f, 0.64f,
                    "RC.Mount.Timer", "RC.Prop.Hourglass", "RC.Prop.Hourglass.Glass",
                    "RC.Prop.Hourglass.Sand.Lower", "RC.Prop.Hourglass.Sand.Stream", "RC.Prop.Hourglass.Sand.Upper")
            };

            // Platforms were material-batched in the export. Move their isolated geometry
            // together with the props, including the purple inset and the stamper's brass rim.
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                string path = filter.name switch
                {
                    "RC.Static.Wood.Rail" => "Assets/Art/Boards/RoyalCouncil/TimerPlatformRail.asset",
                    "RC.Static.Base.Liner" => "Assets/Art/Boards/RoyalCouncil/ControlPlatformLiner.asset",
                    "RC.Static.Gold.Highlight" => "Assets/Art/Boards/RoyalCouncil/ControlPlatformBrass.asset",
                    _ => null
                };
                if (path == null) continue;
                Vector3[] vertices = filter.sharedMesh.vertices;
                Vector3[] normals = filter.sharedMesh.normals;
                int[] regions = new int[vertices.Length];
                int changed = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 world = filter.transform.TransformPoint(vertices[i]);
                    for (int f = 0; f < fits.Length; f++)
                    {
                        ControlFit fit = fits[f];
                        Vector2 delta = new Vector2(world.x, world.y) - fit.From;
                        if (delta.sqrMagnitude >= fit.Radius * fit.Radius) continue;
                        regions[i] = f + 1;
                        Vector2 xy = fit.To + delta * fit.PlatformScale;
                        vertices[i] = filter.transform.InverseTransformPoint(new Vector3(xy.x, xy.y, world.z));
                        // Only the footprint changes: preserve platform height, UVs and hard edges.
                        Vector3 normal = filter.transform.TransformDirection(normals[i]);
                        normal = new Vector3(normal.x / fit.PlatformScale, normal.y / fit.PlatformScale, normal.z).normalized;
                        normals[i] = filter.transform.InverseTransformDirection(normal);
                        changed++;
                        break;
                    }
                }
                int[] triangles = filter.sharedMesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                    if (regions[triangles[i]] != regions[triangles[i + 1]] || regions[triangles[i]] != regions[triangles[i + 2]])
                        throw new InvalidOperationException($"{filter.name}: control platform region cuts a triangle; review the imported geometry.");
                if (changed == 0) throw new InvalidOperationException($"{filter.name}: control platform geometry was not found.");
                Mesh mesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
                mesh.name = "Royal Council fitted control platforms " + filter.name;
                mesh.vertices = vertices; mesh.normals = normals; mesh.RecalculateBounds();
                Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null) { EditorUtility.CopySerialized(mesh, existing); UnityEngine.Object.DestroyImmediate(mesh); mesh = existing; }
                else AssetDatabase.CreateAsset(mesh, path);
                filter.sharedMesh = mesh;
            }

            Transform[] parts = model.GetComponentsInChildren<Transform>(true);
            foreach (ControlFit fit in fits)
            {
                if (fit.From == fit.To) continue;
                Transform mount = Array.Find(parts, part => part.name == fit.Mount);
                Vector3 before = mount.position;
                Vector3 after = new Vector3(fit.To.x, fit.To.y, before.z);
                foreach (string name in fit.Props)
                {
                    Transform prop = Array.Find(parts, part => part.name == name);
                    prop.position += after - before;
                }
                mount.position = after;
            }
        }
    }
}
