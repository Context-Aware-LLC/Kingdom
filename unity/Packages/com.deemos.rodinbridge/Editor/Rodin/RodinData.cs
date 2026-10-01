using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using System;
using UnityEngine.Rendering;
using System.ComponentModel;



namespace Rodin
{
    public class RodinImage
    {
        public byte[] raw_data;
        public Texture2D tex;

        public RodinImage(byte[] input_data, Texture2D input_tex)
        {
            raw_data = input_data;
            tex = input_tex;
        }
    }

    public class RodinData
    {
        //Global Properties
        public enum MaterialMode { Rodin_Shaded, Rodin_PBR }
        public enum Resolution { Rodin_2K, Rodin_4K }
        public enum ResolutionFast { Rodin_1K }
        public enum LayoutMode { None, BoundingBox, Voxel, PointCloud }
        public enum GenType { OneClick, Manual }
        public enum PolygonOption { Quad, Pro }

        public enum GenMode { Default, Detail, Smooth, Fast }
        public enum  TextTo { Image, Text }

        // Gen250 置于首位，使版本 Toolbar 顺序为 Gen-2.5 / Gen-2 / Gen-1.5；默认仍为 Gen200。
        public enum GenVersion { Gen250, Gen200, Gen150 }

        // Gen-2.5 的 Thinking Effort 五档，映射到 config.brilliant_mode 字符串。
        public enum BrilliantMode { ExtremeLow, Low, Medium, High, ExtremeHigh }

        public MaterialMode materialMode = MaterialMode.Rodin_Shaded;
        public object resolution = null;
        public LayoutMode layoutMode = LayoutMode.None;
        public GenType genType = GenType.OneClick;
        public PolygonOption polygonOption = PolygonOption.Pro;
        public GenMode genMode = GenMode.Fast;
        public GenVersion genVersion = GenVersion.Gen200;
        public BrilliantMode brilliantMode = BrilliantMode.Medium;

        //Image Properties
        public enum AlignOption { Center, Bottom }
        public AlignOption alignOption = AlignOption.Bottom;
        public float height = 100.0f;

        //Voxel Properties
        public enum VoxelMode { Strict, Rough }
        public VoxelMode voxelMode = VoxelMode.Strict;
        public float scale = 1.0f;

        //Pointcloud Properties
        public float uncertainty = 0.01f;

        public List<RodinImage> images = new List<RodinImage>();
        //If not Image, selected go needed to be processed
        public GameObject go;

        // Feature 1.1
        public TextTo textTo = TextTo.Image;
        public int quality = 18000;
        public bool bypass = true;
        public string textInput = "";

        // Functions
        public RodinData()
        {
            resolution = Resolution.Rodin_2K;
        }

        // 仅 Gen-2.5 使用：将 Thinking Effort 档位映射为协议中的 brilliant_mode 字符串。
        public static string GetBrilliantModeValue(BrilliantMode mode)
        {
            switch (mode)
            {
                case BrilliantMode.ExtremeLow: return "gen-2.5-extreme-low";
                case BrilliantMode.Low: return "gen-2.5-low";
                case BrilliantMode.Medium: return "gen-2.5-medium";
                case BrilliantMode.High: return "gen-2.5-high";
                case BrilliantMode.ExtremeHigh: return "gen-2.5-extreme-high";
                default: return "gen-2.5-medium";
            }
        }
    }

    public class RodinModel
    {
        List<string> _MODELFMT = new List<string> { "obj", "fbx", "glb", "gltf", "usdz", "stl" };
        List<string> _IMAGEFMT = new List<string> { "png" };
        public Vector3 _currentLocation { get; private set; } = Vector3.zero;
        private string _dataPath => Application.dataPath;
        private string ImportDir => Path.Combine(_dataPath, "TempImport");

        // USD
        #region USD
        //public (string usdPath, List<string> texturePaths) ExtractUSDZ(string usdzPath, string outputDir)
        //{
        //    string usdFilePath = "";
        //    List<string> texturePngPaths = new List<string>();

        //    using (FileStream fs = File.OpenRead(usdzPath))
        //    using (ZipFile zf = new ZipFile(fs))
        //    {
        //        foreach (ZipEntry entry in zf)
        //        {
        //            string filePath = Path.Combine(outputDir, entry.Name);
        //            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

        //            if (!entry.IsDirectory)
        //            {
        //                using (Stream zipStream = zf.GetInputStream(entry))
        //                using (FileStream outputStream = File.Create(filePath))
        //                {
        //                    zipStream.CopyTo(outputStream);
        //                }

        //                if (Path.GetExtension(filePath).Equals(".usd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(filePath).Equals(".usdc", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(filePath).Equals(".usda", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    usdFilePath = filePath;
        //                }

        //                if (filePath.Replace("\\", "/").ToLower().Contains("/textures/") &&
        //                    Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    texturePngPaths.Add(filePath);
        //                }
        //            }
        //        }
        //    }
        //    return (usdFilePath, texturePngPaths);
        //}

        //public static Scene InitForOpen(string path)
        //{
        //    //path = EditorUtility.OpenFilePanel("Import USD File", "", "usd,usda,usdc,abc");
        //    if (path == null || path.Length == 0 || !path.Contains(".usd"))
        //    {
        //        return null;
        //    }

        //    InitUsd.Initialize();
        //    var stage = pxr.UsdStage.Open(path, pxr.UsdStage.InitialLoadSet.LoadNone);
        //    return Scene.Open(stage);
        //}
        //public static GameObject ImportAsGameObjects(string filepath)
        //{
        //    var scene = InitForOpen(filepath);
        //    if (scene == null)
        //    {
        //        return null;
        //    }
        //    GameObject root = ImportSceneAsGameObject(scene);
        //    scene.Close();
        //    return root;
        //}
        //static private pxr.SdfPath GetDefaultRoot(Scene scene)
        //{
        //    // We can't safely assume the default prim is the model root, because Alembic files will
        //    // always have a default prim set arbitrarily.

        //    // If there is only one root prim, reference this prim.
        //    var children = scene.Stage.GetPseudoRoot().GetChildren().ToList();
        //    if (children.Count == 1)
        //    {
        //        return children[0].GetPath();
        //    }

        //    // Otherwise there are 0 or many root prims, in this case the best option is to reference
        //    // them all, to avoid confusion.
        //    return pxr.SdfPath.AbsoluteRootPath();
        //}
        //private static string GetObjectName(pxr.SdfPath rootPrimName, string path)
        //{
        //    return pxr.UsdCs.TfIsValidIdentifier(rootPrimName.GetName())
        //         ? rootPrimName.GetName()
        //         : GetObjectName(path);
        //}

        //private static string GetObjectName(string path)
        //{
        //    return UnityTypeConverter.MakeValidIdentifier(Path.GetFileNameWithoutExtension(path));
        //}

        //private static string GetPrefabName(string path)
        //{
        //    var fileName = GetObjectName(path);
        //    return fileName + "_prefab";
        //}
        //public static GameObject UsdToGameObject(GameObject parent,
        //                                     Scene scene,
        //                                     SceneImportOptions importOptions)
        //{
        //    try
        //    {
        //        SceneImporter.ImportUsd(parent, scene, new PrimMap(), importOptions);
        //    }
        //    finally
        //    {
        //        scene.Close();
        //    }

        //    return parent;
        //}
        //public static GameObject ImportSceneAsGameObject(Scene scene, SceneImportOptions importOptions = null)
        //{
        //    string path = scene.FilePath;

        //    // Time-varying data is not supported and often scenes are written without "Default" time
        //    // values, which makes setting an arbitrary time safer (because if only default was authored
        //    // the time will be ignored and values will resolve to default time automatically).
        //    scene.Time = 1.0;

        //    if (importOptions == null)
        //    {
        //        importOptions = new SceneImportOptions();
        //        importOptions.changeHandedness = BasisTransformation.SlowAndSafe;
        //        importOptions.materialImportMode = MaterialImportMode.ImportDisplayColor;
        //        importOptions.usdRootPath = GetDefaultRoot(scene);
        //    }

        //    GameObject root = new GameObject(GetObjectName(importOptions.usdRootPath, path));

        //    if (Selection.gameObjects.Length > 0)
        //    {
        //        root.transform.SetParent(Selection.gameObjects[0].transform);
        //    }

        //    try
        //    {
        //        UsdToGameObject(root, scene, importOptions);
        //        return root;
        //    }
        //    catch (SceneImporter.ImportException)
        //    {
        //        GameObject.DestroyImmediate(root);
        //        return null;
        //    }
        //}

        #endregion
        public static GameObject ImportUSDZ(string sourceFilePath, string relativeTargetDir)
        {
            // sourceFilePath: absolute file path, must in ProjectDir/Assets/ID/
            // relativeTargetDir: relative path of Assets, e.g. "Assets\0123456789ABCDEF"
#if UNITY_EDITOR
            if (!File.Exists(sourceFilePath))
            {
                Debug.LogError("File not exist: " + sourceFilePath);
                return null;
            }


            string fileName = Path.GetFileName(sourceFilePath);
            string targetAssetPath = Path.Combine(relativeTargetDir, fileName).Replace("\\", "/");

            try
            {
                AssetDatabase.ImportAsset(targetAssetPath, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Debug.LogWarning(e);
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetAssetPath);
            if (prefab == null)
            {
                Debug.LogError("Import Failed: " + targetAssetPath);
                return null;
            }

            GameObject instance = GameObject.Instantiate(prefab);
            instance.name = prefab.name + "_Instance";
            return instance;
#else
        Debug.LogError("只能在Unity Editor中导入模型。");
        return null;
#endif
        }


        public void LoadRodinModel(JObject message)
        {
            Debug.Log("Server Received Task:\n" + JsonConvert.SerializeObject(message, Formatting.Indented));
            File.WriteAllText(Path.Combine(RodinUtils.GetCachePath(), "cache.json"), JsonConvert.SerializeObject(message, Formatting.Indented));
            Debug.Log($"Cached json: {Path.Combine(RodinUtils.GetCachePath(), "cache.json")}");

            var dataRecv = message["data"];
            if (!RodinUtils.isDataValid(dataRecv))
            {
                RodinLogger.Warn("LoadRodinModel: received message has no valid data.");
                return;
            }
            var filesRecv = dataRecv["files"];

            JArray location = RodinUtils.isDataValid(dataRecv["location"]) ? dataRecv["location"] as JArray : null;
            if (location == null || location.Count != 3)
            {
                _currentLocation = Vector3.zero;
            }
            else
            {
                _currentLocation = new Vector3(0f-location[0].ToObject<float>(), location[2].ToObject<float>(), 0f-location[1].ToObject<float>()); // Fix: Simulate the effect of rotating 90 degrees along X axis
            }

            if (!RodinUtils.isDataValid(filesRecv))
            {
                return;
            }

            string sid = dataRecv["sid"]?.ToString();
            sid = string.IsNullOrEmpty(sid) ? dataRecv["request_id"]?.ToString() : sid;
            sid = string.IsNullOrEmpty(sid) ? Guid.NewGuid().ToString("N") : sid;
            RodinLogger.Info("LoadRodinModel: start importing received model.", sid);
            if (!System.IO.Directory.Exists(Path.Combine(Application.dataPath, sid)))
            {
                System.IO.Directory.CreateDirectory(Path.Combine(Application.dataPath, sid));
            }
            if (filesRecv.Type == JTokenType.Array)
            {
                int texMode = 0; // 0: noTex(usdz); 1: shaded; 2: pbr
                Dictionary<string, Texture2D> texDict = new Dictionary<string, Texture2D>();
                GameObject importedObj = null;
                JArray fileArray = (JArray)filesRecv;
                importedObj = LoadModel(fileArray[0], sid);
                foreach (var file in fileArray)
                {
                    if (_IMAGEFMT.Contains(file["format"]?.ToString()))
                    {
                        if (file["filename"].ToString().Contains("shaded"))
                        {
                            texMode = 1;
                        }
                        else if (file["filename"].ToString().Contains("diffuse"))
                        {
                            texMode = 2;
                        }

                        texDict.Add(file["filename"].ToString().Replace("texture_", "").Replace(".png", ""), LoadImage(file, sid));
                    }
                    //else if (_MODELFMT.Contains(file["format"]?.ToString()))
                    //{
                    //    importedObj = LoadModel(file, sid);
                    //}
                }
                if (texMode != 0)
                {
                    string matPath = CreateTimestampedAssetPath(Path.Combine("Assets", sid, "tex"), "baseMat", "mat");
                    RodinLogger.Info($"ApplyTextures: material={matPath}", sid);
                    ApplyTextures(importedObj, texDict, matPath);
                }
            }
            else if (filesRecv.Type == JTokenType.Object)
            {
                LoadModelPBR((JArray)filesRecv["pbr"]?? (JArray)JsonConvert.DeserializeObject("[]"), sid);
                LoadModelShaded((JArray)filesRecv["shaded"] ?? (JArray)JsonConvert.DeserializeObject("[]"), sid);
            }
        }

        private (string md5Hex, string contentB64) GetContent(JToken file)
        {
            string md5 = file["md5"]?.ToString() ?? "";
            string content = file["content"]?.ToString() ?? "";
            return (md5, content);
        }

        private GameObject ImportModel(string filePath)
        {
#if UNITY_EDITOR
            //string relativePath = "Assets" + filePath.Replace(Application.dataPath, "").Replace("\\", "/");

            AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(filePath);

            if (prefab != null)
            {
                GameObject instance = GameObject.Instantiate(prefab);
                return instance;
            }
#endif
            return null;
        }

        public GameObject LoadModelFromJson(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"Rodin: [加载数据] 文件不存在 -> {filePath}");
                return null;
            }
            string jsonContent = File.ReadAllText(filePath);
            JObject obj = JObject.Parse(jsonContent);
            return LoadModel(((JArray)obj["data"]["files"])[0], "0123456789ABCDEF");
        }

        public GameObject LoadModel(JToken file, string id)
        {
            string format = file["format"]?.ToString();
            if (!_MODELFMT.Contains(format))
            {
                throw new System.Exception($"Format not supported: {format}");
            }

            string prefix = $"data:model/{format};base64,";
            (string md5Hex, string contentB64) = GetContent(file);

            if (contentB64.StartsWith(prefix))
            {
                contentB64 = contentB64.Substring(prefix.Length);
            }
            else
            {
                Debug.LogError($"Rodin: [加载数据] 数据格式错误 -> {contentB64.Substring(0, Math.Min(50, contentB64.Length))}");
                RodinLogger.Error("Load data failed: invalid base64 prefix/format.");
                return null;
            }

            byte[] content = Convert.FromBase64String(contentB64);
            string fileName = file["filename"]?.ToString() ?? $"rodin_recv_model.{format}";
            fileName = $"{md5Hex}_{fileName}";

            //string modelPath = Path.Combine(RodinUtils.GetCachePath(), id, fileName);
            string modelPath = Path.Combine(Application.dataPath, id, fileName);
            if (!Directory.Exists(Path.GetDirectoryName(modelPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(modelPath));
            }
            File.WriteAllBytes(modelPath, content);

            //string relativePath = Path.GetRelativePath(Path.GetDirectoryName(Application.dataPath), Path.GetDirectoryName(modelPath));
            string relativePath = "Assets/" + Path.GetRelativePath(Application.dataPath, modelPath).Replace("\\", "/");
            try
            {
                AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Debug.LogWarning(e);
                RodinLogger.Warn($"ImportAsset failed for {relativePath}: {e.Message}");
            }
            AssetDatabase.Refresh();

            GameObject importedObj = null;

            if (format == "obj")
            {
                importedObj = ImportModel(relativePath);
            }
            else if (format == "fbx" || format == "gltf" || format == "glb" || format == "stl")
            {
                importedObj = ImportModel(relativePath);
            }
            else if (format == "usdz")
            {
                importedObj = ImportUSDZ(modelPath, Path.GetRelativePath(Path.GetDirectoryName(Application.dataPath), Path.GetDirectoryName(modelPath)));
            }
            else
            {
                throw new Exception($"不支持的模型格式: {format}");
            }

            if (importedObj == null)
                throw new Exception("没有导入任何模型");

            importedObj.transform.position = _currentLocation;
            importedObj.transform.Rotate(Vector3.up, 180f, Space.World);

            //DeleteFileSafe(modelPath);

            return importedObj;
        }

        public Texture2D LoadImage(JToken file, string id)
        {
            if (!RodinUtils.isDataValid(file))
                return null;

            string fmt = file["format"]?.ToString();
            if (!_IMAGEFMT.Contains(fmt))
            {
                throw new System.Exception($"Format not supported: {fmt}");
            }
            string prefix = $"data:image/{fmt};base64,";
            (string md5Hex, string contentB64) = GetContent(file);

            if (contentB64.StartsWith(prefix))
            {
                contentB64 = contentB64.Substring(prefix.Length);
            }
            else
            {
                Debug.LogError($"Rodin: [加载数据] 数据格式错误 -> {contentB64.Substring(0, Math.Min(50, contentB64.Length))}");
                RodinLogger.Error("Load data failed: invalid base64 prefix/format.");
                return null;
            }

            byte[] content = Convert.FromBase64String(contentB64);
            string fileName = $"{md5Hex}_{file["filename"]?.ToString() ?? $"rodin_recv_image_{md5Hex}.{fmt}"}";
            if (!Directory.Exists(Path.Combine(Application.dataPath, id, "tex")))
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, id, "tex"));
            }
            string imagePath = Path.Combine(Application.dataPath, id, "tex", fileName);

            File.WriteAllBytes(imagePath, content);
            AssetDatabase.Refresh();

            return LoadImageFromFile(Path.Combine("Assets", id, "tex", fileName));
        }

        public Texture2D LoadImageFromFile(string path)
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (tex != null)
            {
                return tex;
            }
            else
            {
                Debug.LogWarning("Rodin: Cannot access texture through AssetDatabase, trying accessing from byte stream.");

                byte[] rawData = File.ReadAllBytes(path);
                Texture2D fallbackTex = new Texture2D(2, 2);
                if (fallbackTex.LoadImage(rawData))
                    return fallbackTex;

                Debug.LogError("Rodin: Failed to decode texture.");
                return null;
            }
        }

        public void LoadModelPBR(JArray file, string id)
        {
            if (!RodinUtils.isDataValid(file))
            {
                throw new Exception($"PBR data with invalid count: {file.Count}");
            }

            GameObject importedObj;
            List<Texture2D> images = new List<Texture2D>();
            foreach (JToken f in file)
            {
                string format = file["format"]?.ToString();
                if (_MODELFMT.Contains(format))
                {
                    importedObj = LoadModel(file, id);
                }
                else if (_IMAGEFMT.Contains(format))
                {
                    Texture2D tex = LoadImage(f, id);
                    if (tex != null)
                    {
                        images.Add(tex);
                    }
                }
            }
            // TODO: Set PBR mat

        }

        public void LoadModelShaded(JArray file, string id)
        {
            if (!RodinUtils.isDataValid(file))
            {
                throw new Exception($"PBR data with invalid count: {file.Count}");
            }

            GameObject importedObj;
            List<Texture2D> images = new List<Texture2D>();
            foreach (JToken f in file)
            {
                string format = file["format"]?.ToString();
                if (_MODELFMT.Contains(format))
                {
                    importedObj = LoadModel(file, id);
                }
                else if (_IMAGEFMT.Contains(format))
                {
                    Texture2D tex = LoadImage(f, id);
                    if (tex != null)
                    {
                        images.Add(tex);
                    }
                }
            }
            // TODO: Set PBR mat
        }

        // Set Mat
        #region Set Mat
                public static Material CreateSafeDefaultMaterial()
        {
            Shader shader = null;

            switch (GetActivePipeline())
            {
                case PipelineType.BuiltIn:
#if UNITY_EDITOR
                    if (TexturePreprocessor.standard != null)
                        return new Material(TexturePreprocessor.standard);
#endif
                    shader = Shader.Find("Standard");
                    break;

                case PipelineType.URP:
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                    break;

                case PipelineType.HDRP:
                    shader = Shader.Find("HDRP/Lit");
                    break;

                case PipelineType.Custom:
#if UNITY_EDITOR
                    var builtinMat = EditorGUIUtility.LoadRequired("Default-Material.mat") as Material;
                    if (builtinMat != null)
                        return new Material(builtinMat);
#endif
                    shader = Shader.Find("Unlit/Color");
                    break;
            }

            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            return new Material(shader);
        }

        private enum PipelineType { BuiltIn, URP, HDRP, Custom }

        private static string CreateTimestampedAssetPath(string assetDir, string baseName, string extension)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string path = Path.Combine(assetDir, $"{baseName}_{timestamp}.{extension}").Replace("\\", "/");
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        private static string CreateDerivedAssetPath(string sourceAssetPath, string suffix, string extension)
        {
            string dir = Path.GetDirectoryName(sourceAssetPath);
            string name = Path.GetFileNameWithoutExtension(sourceAssetPath);
            string path = Path.Combine(dir, $"{name}_{suffix}.{extension}").Replace("\\", "/");
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        private static PipelineType GetActivePipeline()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null)
                return PipelineType.BuiltIn;

            var typeName = asset.GetType().FullName;
            if (typeName.Contains("HDRenderPipelineAsset"))
                return PipelineType.HDRP;
            if (typeName.Contains("UniversalRenderPipelineAsset"))
                return PipelineType.URP;

            return PipelineType.Custom;
        }
        public static void ApplyTextures(GameObject target, Dictionary<string, Texture2D> textures, string matPath)
        {
            if (target == null || textures == null || textures.Count == 0)
            {
                Debug.Log("No go or textures, return.");
                return;
            }

            MeshRenderer[] renderers = target.GetComponentsInChildren<MeshRenderer>();
            //MeshRenderer renderer = target.GetComponentInChildren<MeshRenderer>();
            if (renderers == null) return;

            //Material mat = new Material(Shader.Find("Standard"));
            //Material mat = new Material(TexturePreprocessor.standard);
            Material mat = CreateSafeDefaultMaterial();
            //renderer.material = mat;

            // Shaded
            if (textures.TryGetValue("shaded", out Texture2D shadedTex))
            {
                mat.SetColor("_Color", Color.black);
                mat.SetColor("_BaseColor", Color.black);
                mat.SetTexture("_MainTex", null);

                if (!mat.IsKeywordEnabled("_EMISSION"))
                {
                    mat.EnableKeyword("_EMISSION");
                }
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetTexture("_EmissionMap", shadedTex);
                mat.SetColor("_EmissionColor", Color.white);
                if (GetActivePipeline() == PipelineType.HDRP)
                {
                    mat.SetFloat("_Smoothness", 1.0f);
                    mat.SetColor("_EmissiveColor", Color.white);
                    mat.SetColor("_EmissiveColorLDR", Color.white);
                    mat.SetTexture("_EmissiveColorMap", shadedTex);
                    mat.SetFloat("_EmissiveExposureWeight", 0.0f);
                }
                

                //AssetDatabase.SaveAssets();
                //return;

                AssetDatabase.CreateAsset(mat, matPath);
                AssetDatabase.SaveAssets();
                foreach (MeshRenderer renderer in renderers)
                {
                    renderer.material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                }
                return;
            }

            // PBR
            if (textures.TryGetValue("diffuse", out Texture2D diffuseTex))
            {
                TexturePreprocessor.MakeTextureReadableAndSetType(diffuseTex, false);
                mat.SetTexture("_MainTex", diffuseTex);
                mat.SetTexture("_BaseMap", diffuseTex);
                mat.SetTexture("_BaseColorMap", diffuseTex);
            }

            if (textures.TryGetValue("normal", out Texture2D normalTex))
            {
                TexturePreprocessor.MakeTextureReadableAndSetType(normalTex, true);
                mat.SetTexture("_BumpMap", normalTex);
                mat.SetTexture("_NormalMap", normalTex);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (textures.TryGetValue("metallic", out Texture2D metallicTex))
            {
                TexturePreprocessor.MakeTextureReadableAndSetType(metallicTex, false);
                Texture2D finalMetallic = metallicTex;

                if (textures.TryGetValue("roughness", out Texture2D roughnessTex))
                {
                    TexturePreprocessor.MakeTextureReadableAndSetType(roughnessTex, false);
                    finalMetallic = CombineMetallicAndRoughness(metallicTex, roughnessTex);

                    byte[] pngData = finalMetallic.EncodeToPNG();
                    string finalMetalPath = CreateDerivedAssetPath(matPath, "finalMetal", "png");
                    File.WriteAllBytes(finalMetalPath, pngData);
                    AssetDatabase.ImportAsset(finalMetalPath);
                    AssetDatabase.Refresh();
                    finalMetallic = AssetDatabase.LoadAssetAtPath<Texture2D>(finalMetalPath);
                }

                mat.SetTexture("_MetallicGlossMap", finalMetallic);
                mat.SetTexture("_MaskMap", finalMetallic);
                mat.EnableKeyword("_METALLICGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
            }
            else if (textures.TryGetValue("pbr", out Texture2D pbrTex))
            {
                mat.SetTexture("_MetallicGlossMap", pbrTex);
                mat.EnableKeyword("_METALLICGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
            }
            else if (textures.TryGetValue("roughness", out Texture2D roughnessTex))
            {
                TexturePreprocessor.MakeTextureReadableAndSetType(roughnessTex, false);
                Texture2D finalMetallic = CombineMetallicAndRoughness(null, roughnessTex);

                byte[] pngData = finalMetallic.EncodeToPNG();
                string finalMetalPath = CreateDerivedAssetPath(matPath, "finalMetal", "png");
                File.WriteAllBytes(finalMetalPath, pngData);
                AssetDatabase.ImportAsset(finalMetalPath);
                AssetDatabase.Refresh();
                finalMetallic = AssetDatabase.LoadAssetAtPath<Texture2D>(finalMetalPath);

                mat.SetTexture("_MetallicGlossMap", finalMetallic);
                mat.EnableKeyword("_METALLICGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
            }
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            foreach (MeshRenderer renderer in renderers)
            {
                renderer.material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            }
        }

        private static Texture2D CombineMetallicAndRoughness(Texture2D metallic, Texture2D roughness)
        {
            int width = Mathf.Max(metallic ? metallic.width : 0, roughness.width);
            int height = Mathf.Max(metallic ? metallic.height : 0, roughness.height);

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);

            Color[] metallicPixels = metallic ? metallic.GetPixels() : new Color[width * height];
            Color[] roughnessPixels = roughness.GetPixels();

            Color[] finalPixels = new Color[width * height];
            for (int i = 0; i < finalPixels.Length; i++)
            {
                float m = metallic ? metallicPixels[i].r : 0f;
                float r = roughnessPixels[i].r;
                float smoothness = 1f - r;

                finalPixels[i] = new Color(m, 0f, 0f, smoothness);
            }

            result.SetPixels(finalPixels);
            result.Apply();
            return result;
        }
        #endregion
    }


}
