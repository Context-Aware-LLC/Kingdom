using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Board-only finishes, rebuilt from the preserved source model.</summary>
    internal static class RoyalCouncilFinishAuthoring
    {
        private const string Root = "Assets/Art/Boards/RoyalCouncil/";
        private const float CapScale = 0.85f;

        public static void Apply(GameObject root)
        {
            Transform[] parts = root.GetComponentsInChildren<Transform>(true);
            Transform Part(string name) => parts.First(p => p.name == name);
            Shader finish = Shader.Find("Kingdom/Royal Council Finish");
            Shader glass = Shader.Find("Kingdom/Royal Council Glass");
            if (finish == null || glass == null) throw new InvalidOperationException("Royal Council finish shaders have not imported.");

            Material stock = Material("CardStock", finish, new Color(0.78f,0.71f,0.56f),0f,0.23f,0.12f,230f);
            stock.SetFloat("_EdgeShade",0.65f);
            for (int seat = 0; seat < 2; seat++)
            {
                var card = Part($"RC.P{seat}.Pile.Card").GetComponent<MeshFilter>();
                Mesh mesh = UnityEngine.Object.Instantiate(card.sharedMesh);
                mesh.name = $"Layered card stock {seat}";
                Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                var edges = new Vector2[vertices.Length];
                Bounds bounds = mesh.bounds;
                for (int i = 0; i < edges.Length; i++)
                    edges[i] = new Vector2(Mathf.Abs(normals[i].y) < 0.5f ? 1f : 0f,
                        Mathf.InverseLerp(bounds.min.y,bounds.max.y,vertices[i].y));
                mesh.uv2 = edges;
                card.sharedMesh = SaveMesh(mesh,$"CardStock{seat}.asset");
                card.GetComponent<Renderer>().sharedMaterial = stock;
            }

            var caps = Part("RC.Prop.Hourglass").GetComponent<MeshFilter>();
            Mesh capMesh = UnityEngine.Object.Instantiate(caps.sharedMesh);
            capMesh.name = "Narrow hourglass caps";
            Vector3[] capVertices = capMesh.vertices, capNormals = capMesh.normals;
            for (int i = 0; i < capVertices.Length; i++)
            {
                capVertices[i] = Vector3.Scale(capVertices[i],new Vector3(CapScale,1f,CapScale));
                capNormals[i] = Vector3.Scale(capNormals[i],new Vector3(1f/CapScale,1f,1f/CapScale)).normalized;
            }
            capMesh.vertices = capVertices;
            capMesh.normals = capNormals;
            capMesh.RecalculateBounds();
            float capRadius = capMesh.bounds.extents.x;
            caps.sharedMesh = SaveMesh(capMesh,"HourglassCaps.asset");
            Material brass = Material("HourglassBrass",finish,new Color(0.64f,0.40f,0.14f),0.68f,0.66f,0.13f,360f);
            brass.SetFloat("_TurnedRings",0.8f);
            brass.SetFloat("_CapRadius",capRadius);
            caps.GetComponent<Renderer>().sharedMaterial = brass;

            Material sand = Material("HourglassSand",finish,new Color(0.83f,0.63f,0.32f),0f,0.16f,0.24f,1700f);
            foreach (string name in new[] { "Upper", "Lower", "Stream" })
                Part("RC.Prop.Hourglass.Sand."+name).GetComponent<Renderer>().sharedMaterial = sand;

            Material mana = Material("ManaCloud",glass,new Color(0.035f,0.28f,0.6f,0.64f),0f,0f,0f,0f);
            mana.renderQueue = 2990; // Cloudy interior before the clear outer shell.
            mana.SetColor("_CloudColor",new Color(0.48f,0.82f,1f,1f));
            mana.SetColor("_EmissionColor",new Color(0.015f,0.075f,0.13f));
            mana.SetFloat("_Cloudiness",0.48f);
            mana.SetFloat("_CloudSpeed",0.06f);
            mana.SetFloat("_Rim",0.16f);
            for (int seat = 0; seat < 2; seat++)
                for (int i = 0; i < 10; i++)
                    Part($"RC.P{seat}.Mana.FullCore.{i:00}").GetComponent<Renderer>().sharedMaterial = mana;
        }

        private static Material Material(string name, Shader shader, Color colour, float metallic,
            float smoothness, float grain, float scale)
        {
            string path = Root+"Materials/"+name+".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name, enableInstancing = true };
                AssetDatabase.CreateAsset(material,path);
            }
            material.shader = shader;
            material.SetColor("_BaseColor",colour);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic",metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness",smoothness);
            if (material.HasProperty("_Grain")) material.SetFloat("_Grain",grain);
            if (material.HasProperty("_GrainScale")) material.SetFloat("_GrainScale",scale);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Mesh SaveMesh(Mesh mesh, string file)
        {
            string path = Root+file;
            Mesh saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (saved == null) { AssetDatabase.CreateAsset(mesh,path); return mesh; }
            EditorUtility.CopySerialized(mesh,saved);
            saved.vertices = mesh.vertices;
            saved.normals = mesh.normals;
            saved.uv2 = mesh.uv2;
            saved.triangles = mesh.triangles;
            saved.RecalculateBounds();
            EditorUtility.SetDirty(saved);
            UnityEngine.Object.DestroyImmediate(mesh);
            return saved;
        }
    }
}
