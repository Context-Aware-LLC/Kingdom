using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberfall.Editor
{
    /// <summary>
    /// Authors the standalone 2.5D board look-development scene. The scene intentionally stays
    /// out of Build Settings until it is ready to replace BoardNetworked.
    ///
    /// The tabletop remains on the gameplay board's XY plane. Depth projects toward the camera
    /// on negative Z, matching BoardBackdrop and the existing card presentation conventions.
    /// </summary>
    public static class CreateBoardTabletopPrototypeScene
    {
        public const string ScenePath = "Assets/Scenes/BoardTabletopPrototype.unity";

        private const string MaterialFolder = "Assets/Materials/TabletopPrototype";
        private const string LitShaderName = "Universal Render Pipeline/Lit";

        private static readonly Color DarkWood = new(0.18f, 0.065f, 0.035f, 1f);
        private static readonly Color Felt = new(0.035f, 0.12f, 0.115f, 1f);
        private static readonly Color Brass = new(0.62f, 0.31f, 0.075f, 1f);
        private static readonly Color CardEdge = new(0.075f, 0.04f, 0.035f, 1f);
        private static readonly Color Ember = new(1.0f, 0.22f, 0.035f, 1f);

        [MenuItem("Emberfall/Board/Rebuild 2.5D Tabletop Prototype")]
        public static void Build()
        {
            EnsureFolder(MaterialFolder);

            Material wood = CreateMaterial(
                $"{MaterialFolder}/M_TabletopWood.mat", DarkWood, metallic: 0.02f,
                smoothness: 0.32f, texturePath: "Assets/Art/VFX/Textures/Wood.png",
                textureScale: new Vector2(4f, 2f));
            Material felt = CreateMaterial(
                $"{MaterialFolder}/M_TabletopFelt.mat", Felt, metallic: 0f, smoothness: 0.18f);
            Material brass = CreateMaterial(
                $"{MaterialFolder}/M_TabletopBrass.mat", Brass, metallic: 0.82f, smoothness: 0.48f);
            Material cardEdge = CreateMaterial(
                $"{MaterialFolder}/M_CardEdge.mat", CardEdge, metallic: 0.05f, smoothness: 0.36f);
            Material gem = CreateMaterial(
                $"{MaterialFolder}/M_CardGem.mat", new Color(0.08f, 0.32f, 0.9f, 1f),
                metallic: 0.3f, smoothness: 0.72f, emission: new Color(0.015f, 0.08f, 0.4f));
            Material ember = CreateMaterial(
                $"{MaterialFolder}/M_EmberGlow.mat", new Color(0.5f, 0.055f, 0.01f, 1f),
                metallic: 0.05f, smoothness: 0.5f, emission: Ember * 2.4f);

            Material[] cardFaces =
            {
                CreateCardFaceMaterial("EmberBlazewing", "Assets/Art/Cards/EMBERFALL/ember_blazewing.png"),
                CreateCardFaceMaterial("ClockworkKnight", "Assets/Art/Cards/REKINDLING/clock_clockwork_knight.png"),
                CreateCardFaceMaterial("TombWarden", "Assets/Art/Cards/REKINDLING/grave_tomb_warden.png")
            };

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "BoardTabletopPrototype";

            ConfigureEnvironment();
            CreateCamera();
            CreateLighting();

            var stage = new GameObject("Tabletop Stage");
            CreateTabletop(stage.transform, wood, felt, brass);
            CreateCardRows(stage.transform, cardEdge, gem, cardFaces);
            CreateDeckPiles(stage.transform, cardEdge, cardFaces[1]);
            CreateBraziers(stage.transform, brass, ember);

            ValidateAuthoredScene(stage);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Unity could not save {ScenePath}.");

            AssetDatabase.SaveAssets();
            Debug.Log($"[CreateBoardTabletopPrototypeScene] Authored {ScenePath}.");
        }

        private static void ConfigureEnvironment()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.14f, 0.17f, 0.24f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.075f, 0.06f, 0.075f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.018f, 0.013f, 0.016f, 1f);
            RenderSettings.ambientIntensity = 0.72f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.018f, 0.022f, 0.035f, 1f);
            RenderSettings.fogDensity = 0.012f;
        }

        private static void CreateCamera()
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, -5.15f, -18f);
            cameraObject.transform.LookAt(new Vector3(0f, 0.35f, 0.45f));

            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = false;
            camera.fieldOfView = 36f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 80f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.012f, 0.015f, 0.025f, 1f);
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.depthTextureMode = DepthTextureMode.Depth;
            cameraObject.AddComponent<AudioListener>();
        }

        private static void CreateLighting()
        {
            var keyObject = new GameObject("Key Light");
            keyObject.transform.rotation = Quaternion.Euler(24f, -28f, -8f);
            var key = keyObject.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.76f, 0.56f);
            key.intensity = 2.15f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.78f;
            key.shadowBias = 0.025f;
            key.shadowNormalBias = 0.18f;

            CreatePointLight("Cool Fill", new Vector3(0f, 4.4f, -4.5f),
                new Color(0.22f, 0.46f, 1f), intensity: 3.2f, range: 14f);
            CreatePointLight("Warm Rim", new Vector3(-7.2f, -3.6f, -2.2f),
                new Color(1f, 0.25f, 0.055f), intensity: 2.8f, range: 9f);
        }

        private static void CreateTabletop(Transform parent, Material wood, Material felt, Material brass)
        {
            var environment = new GameObject("Board");
            environment.transform.SetParent(parent, false);

            CreatePrimitive("Wood Tabletop", PrimitiveType.Cube, environment.transform,
                new Vector3(0f, 0f, 1.2f), new Vector3(18.4f, 9.4f, 1.55f), wood);
            CreatePrimitive("Felt Play Surface", PrimitiveType.Cube, environment.transform,
                new Vector3(0f, 0f, 0.34f), new Vector3(15.65f, 7.15f, 0.18f), felt);

            CreatePrimitive("Top Brass Rail", PrimitiveType.Cube, environment.transform,
                new Vector3(0f, 3.83f, 0.18f), new Vector3(16.1f, 0.18f, 0.24f), brass);
            CreatePrimitive("Bottom Brass Rail", PrimitiveType.Cube, environment.transform,
                new Vector3(0f, -3.83f, 0.18f), new Vector3(16.1f, 0.18f, 0.24f), brass);
            CreatePrimitive("Left Brass Rail", PrimitiveType.Cube, environment.transform,
                new Vector3(-8.04f, 0f, 0.18f), new Vector3(0.18f, 7.84f, 0.24f), brass);
            CreatePrimitive("Right Brass Rail", PrimitiveType.Cube, environment.transform,
                new Vector3(8.04f, 0f, 0.18f), new Vector3(0.18f, 7.84f, 0.24f), brass);
            CreatePrimitive("Center Divider", PrimitiveType.Cube, environment.transform,
                new Vector3(0f, 0f, 0.18f), new Vector3(14.8f, 0.045f, 0.08f), brass);

            CreateCornerStuds(environment.transform, brass);
        }

        private static void CreateCornerStuds(Transform parent, Material brass)
        {
            foreach (float x in new[] { -7.72f, 7.72f })
            foreach (float y in new[] { -3.53f, 3.53f })
            {
                GameObject stud = CreatePrimitive("Brass Stud", PrimitiveType.Sphere, parent,
                    new Vector3(x, y, 0.02f), new Vector3(0.28f, 0.28f, 0.16f), brass);
                RemoveCollider(stud);
            }
        }

        private static void CreateCardRows(
            Transform parent, Material edge, Material gem, Material[] faces)
        {
            var cards = new GameObject("Raised Card Studies");
            cards.transform.SetParent(parent, false);

            Vector3[] positions =
            {
                new(-3.05f, 1.42f, -0.03f),
                new(-1.48f, 1.42f, -0.04f),
                new(0.12f, 1.42f, -0.06f),
                new(-1.72f, -1.45f, -0.04f),
                new(0f, -1.55f, -0.58f),
                new(1.72f, -1.45f, -0.05f)
            };
            Vector3[] rotations =
            {
                new(2.5f, -4f, 2f),
                new(-1f, 3f, -1.5f),
                new(2f, 2f, 1f),
                new(-2f, -3f, 2.5f),
                new(-5f, 5f, -3.5f),
                new(2.5f, 3f, 1.5f)
            };

            for (int i = 0; i < positions.Length; i++)
            {
                float scale = i == 4 ? 1.08f : 1f;
                CreateCard($"Card Study {i + 1}", cards.transform, positions[i], rotations[i],
                    scale, edge, gem, faces[i % faces.Length]);
            }
        }

        private static void CreateCard(
            string name, Transform parent, Vector3 position, Vector3 rotation, float scale,
            Material edge, Material gem, Material face)
        {
            const float width = 1.3f;
            const float height = 1.88f;
            const float thickness = 0.16f;

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = position;
            root.transform.localRotation = Quaternion.Euler(rotation);
            root.transform.localScale = Vector3.one * scale;

            CreatePrimitive("Card Body", PrimitiveType.Cube, root.transform,
                Vector3.zero, new Vector3(width, height, thickness), edge);

            GameObject faceObject = CreatePrimitive("Art Face", PrimitiveType.Quad, root.transform,
                new Vector3(0f, 0.02f, -thickness * 0.51f),
                new Vector3(width * 0.88f, height * 0.87f, 1f), face);
            RemoveCollider(faceObject);
            var faceRenderer = faceObject.GetComponent<Renderer>();
            faceRenderer.shadowCastingMode = ShadowCastingMode.Off;
            faceRenderer.receiveShadows = false;

            GameObject manaGem = CreatePrimitive("Mana Gem", PrimitiveType.Sphere, root.transform,
                new Vector3(-width * 0.37f, height * 0.38f, -thickness * 0.82f),
                new Vector3(0.24f, 0.24f, 0.11f), gem);
            RemoveCollider(manaGem);
        }

        private static void CreateDeckPiles(Transform parent, Material edge, Material face)
        {
            var decks = new GameObject("Deck Piles");
            decks.transform.SetParent(parent, false);

            CreateDeckPile("Opponent Deck", decks.transform, new Vector3(6.65f, 2.65f, 0.02f),
                new Vector3(2f, -3f, -2f), edge, face);
            CreateDeckPile("Player Deck", decks.transform, new Vector3(-6.65f, -2.65f, 0.02f),
                new Vector3(-2f, 4f, 2f), edge, face);
        }

        private static void CreateDeckPile(
            string name, Transform parent, Vector3 position, Vector3 rotation,
            Material edge, Material face)
        {
            var pile = new GameObject(name);
            pile.transform.SetParent(parent, false);
            pile.transform.localPosition = position;
            pile.transform.localRotation = Quaternion.Euler(rotation);

            for (int i = 0; i < 5; i++)
            {
                float z = -i * 0.045f;
                GameObject card = CreatePrimitive($"Deck Card {i + 1}", PrimitiveType.Cube, pile.transform,
                    new Vector3(0f, 0f, z), new Vector3(1.26f, 1.82f, 0.07f), edge);
                if (i != 4) continue;

                GameObject top = CreatePrimitive("Deck Top", PrimitiveType.Quad, card.transform,
                    new Vector3(0f, 0f, -0.52f), new Vector3(0.88f, 0.88f, 1f), face);
                RemoveCollider(top);
                top.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static void CreateBraziers(Transform parent, Material brass, Material ember)
        {
            var props = new GameObject("Dimensional Edge Props");
            props.transform.SetParent(parent, false);

            CreateBrazier("Left Brazier", props.transform, new Vector3(-8.15f, 3.95f, -0.15f), brass, ember);
            CreateBrazier("Right Brazier", props.transform, new Vector3(8.15f, -3.95f, -0.15f), brass, ember);
        }

        private static void CreateBrazier(
            string name, Transform parent, Vector3 position, Material brass, Material ember)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = position;

            GameObject bowl = CreatePrimitive("Bowl", PrimitiveType.Cylinder, root.transform,
                Vector3.zero, new Vector3(0.62f, 0.16f, 0.62f), brass);
            // A Unity cylinder grows along local Y; turn that axis toward the camera so the
            // brazier reads as board-mounted relief instead of a bar lying across the rail.
            bowl.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            GameObject coal = CreatePrimitive("Ember", PrimitiveType.Sphere, root.transform,
                new Vector3(0f, 0f, -0.34f), new Vector3(0.44f, 0.44f, 0.22f), ember);
            RemoveCollider(coal);

            CreatePointLight("Ember Light", position + new Vector3(0f, 0f, -1.2f),
                new Color(1f, 0.16f, 0.025f), intensity: 2.2f, range: 5.5f, parent);
        }

        private static Light CreatePointLight(
            string name, Vector3 position, Color color, float intensity, float range,
            Transform parent = null)
        {
            var lightObject = new GameObject(name);
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.position = position;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.42f;
            return light;
        }

        private static GameObject CreatePrimitive(
            string name, PrimitiveType primitiveType, Transform parent,
            Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject gameObject = GameObject.CreatePrimitive(primitiveType);
            gameObject.name = name;
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.localPosition = localPosition;
            gameObject.transform.localRotation = Quaternion.identity;
            gameObject.transform.localScale = localScale;
            gameObject.GetComponent<Renderer>().sharedMaterial = material;
            return gameObject;
        }

        private static Material CreateCardFaceMaterial(string name, string texturePath)
        {
            return CreateMaterial($"{MaterialFolder}/M_CardFace_{name}.mat", Color.white,
                metallic: 0f, smoothness: 0.24f, texturePath: texturePath, textureScale: Vector2.one);
        }

        private static Material CreateMaterial(
            string path, Color color, float metallic, float smoothness,
            string texturePath = null, Vector2? textureScale = null, Color? emission = null)
        {
            Shader shader = Shader.Find(LitShaderName);
            if (shader == null)
                throw new InvalidOperationException($"Shader '{LitShaderName}' is unavailable.");

            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.name = Path.GetFileNameWithoutExtension(path);
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.enableInstancing = true;

            if (!string.IsNullOrEmpty(texturePath))
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                    throw new InvalidOperationException($"No texture found at {texturePath}.");
                material.SetTexture("_BaseMap", texture);
                material.SetTexture("_MainTex", texture);
                material.SetTextureScale("_BaseMap", textureScale ?? Vector2.one);
                material.SetTextureScale("_MainTex", textureScale ?? Vector2.one);
            }

            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static void EnsureFolder(string path)
        {
            string[] segments = path.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = $"{current}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        private static void RemoveCollider(GameObject gameObject)
        {
            Collider collider = gameObject.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
        }

        private static void ValidateAuthoredScene(GameObject stage)
        {
            Camera camera = Camera.main;
            if (camera == null || camera.orthographic)
                throw new InvalidOperationException("The prototype requires a perspective Main Camera.");
            if (stage.transform.Find("Board/Wood Tabletop") == null)
                throw new InvalidOperationException("The tabletop mesh was not authored.");
            if (stage.transform.Find("Raised Card Studies")?.childCount < 6)
                throw new InvalidOperationException("The raised card studies were not authored.");
            if (UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length < 5)
                throw new InvalidOperationException("The prototype lighting rig is incomplete.");
        }
    }
}
