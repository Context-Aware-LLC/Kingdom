using UnityEditor;
using UnityEngine;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

using Debug = UnityEngine.Debug;
using static Rodin.RodinData;
using WebSocketSharp;

#if RODIN_HAS_UNITYGLTF
using UnityGLTF;
#endif

namespace Rodin
{

    public class PluginWindow : EditorWindow
    {
        [Header("PluginState"), HideInInspector]
        static PluginWindow mainWindow;
        RodinServer rodinServer = null;
        RodinClient rodinClient = null;
        RodinTaskManager taskManager = null;

        [Header("TaskOptions"), HideInInspector]
        RodinData m_taskData = new RodinData();

        List<RodinImage> importedTextures;
        Vector2 scrollPosImages = Vector2.zero;
        string testID;

        RodinData.GenMode lastGenMode = RodinData.GenMode.Default;
        RodinData.GenVersion lastGenVersion = RodinData.GenVersion.Gen200;

        public bool SetApiAddress()
        {
            ////https://5e5f9d21.deemos.pages.dev
            //if (url == null || url.Length == 0)
            //{
            //    rodinServer.apiAddress = "https://hyper3d.ai";
            //}
            //else
            //{
            //    url = url.Last() == '/' ? url.Substring(0, url.Length - 1) : url;
            //    rodinServer.apiAddress = url == "" ? "https://hyper3d.ai" : url;
            //}
            //return true;
            string relativePathInPackage = "Packages/com.deemos.rodinbridge/Resources/config.json";

            string fullPath = Path.GetFullPath(relativePathInPackage);

            if (!File.Exists(fullPath))
            {
                string fullPath1 = Path.GetFullPath("Assets/Resources/config.json");
                if (!File.Exists(fullPath1))
                {
                    Debug.LogError($"Config not found at {fullPath} or {fullPath1}");
                }
                else
                {
                    fullPath = fullPath1;
                }
            }

            string fileContent = File.ReadAllText(fullPath);
            var config = JsonConvert.DeserializeObject<Dictionary<string, string>>(fileContent);
            rodinServer.apiAddress = config.ContainsKey("dev_api") ? config["dev_api"].ToString() : "https://hyper3d.ai";
            return true;
        }
        
        private void ActivateClient()
        {
            if (rodinServer == null)
            {
                rodinServer = new RodinServer(mainWindow);
                rodinServer.parentPW = this;
            }
            if (rodinClient == null)
            {
                rodinClient = new RodinClient();
            }
            if (taskManager == null)
            {
                taskManager = new RodinTaskManager(mainWindow);
                RodinTaskManager.Instance ??= taskManager;
            }
        }

        private void CreateDefaultTexTable()
        {
            importedTextures = new List<RodinImage>();
            Texture2D defaultTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.deemos.rodinbridge/Resources/default.png");
            if (defaultTex != null)
            {
                byte[] defaultBytes = File.ReadAllBytes(Path.GetFullPath("Packages/com.deemos.rodinbridge/Resources/default.png"));
                importedTextures.Add(new RodinImage(defaultBytes, defaultTex));
            }
            else
            {
                defaultTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/default.png");
                if (defaultTex != null)
                {
                    byte[] defaultBytes = File.ReadAllBytes(Path.GetFullPath("Assets/Resources/default.png"));
                    importedTextures.Add(new RodinImage(defaultBytes, defaultTex));
                }
                else
                {
                    Debug.LogWarning("Cannot find default image.");
                }
            }
        }


        [MenuItem("Tools/Rodin/Open Plugin Window")]
        public static void ShowWindow()
        {
            mainWindow = GetWindow<PluginWindow>("Rodin");
            mainWindow.minSize = new Vector2(450, 800);

            RodinLogger.Initialize();

            mainWindow.CreateDefaultTexTable();
            mainWindow.ActivateClient();

            TexturePreprocessor.EnsureShaderIncluded();
        }

        private void OnEnable()
        {
            //if (rodinServer == null)
            //{
            //    rodinServer = new RodinServer(mainWindow);
            //    rodinServer.parentPW = this;
            //}
            //if (rodinClient == null)
            //{
            //    rodinClient = new RodinClient();
            //}
            //if (taskManager == null)
            //{
            //    taskManager = new RodinTaskManager(mainWindow);
            //    RodinTaskManager.Instance ??= taskManager;
            //}
            
        }

        private void OnGUI()
        {
            SetApiAddress();

            // test funcs
            //if (GUILayout.Button("export go to glb"))
            //{
            //    //PrepareMesh(Selection.activeGameObject);
            //    RodinModel rm = new RodinModel();
            //    string jsonContent = File.ReadAllText("E:\\RodinPlugin\\Unity\\RodinPlugin2022\\Temp\\cache.json");
            //    JObject obj = JObject.Parse(jsonContent);
            //    rm.LoadRodinModel(obj);
            //}
            //if (GUILayout.Button("view tasks"))
            //{
            //    var aaa = taskManager.GetTaskInfos();
            //    foreach (var info in aaa)
            //    {
            //        Debug.Log(info);
            //    }

            //}

            GUILayout.Space(10);

#if RODIN_HAS_UNITYGLTF
#else
            GUILayout.Label("UnityGLTF package is required for Conditioning Generation ( BoundingBox / Voxel / PointCloud ). You can continue using but conditioning may not function properly.", EditorStyles.wordWrappedLabel, GUILayout.MinHeight(50));
#endif
            #region Task Config UI
            //EditorGUILayout.LabelField("Material Config", EditorStyles.boldLabel);

            float windowWidth = EditorGUIUtility.currentViewWidth - 10.0f;
            
            // 3. Layout Toolbar
            string[] layoutLabels = { "Image", "BoundingBox", "Voxel", "Point Cloud" };
            m_taskData.layoutMode = (RodinData.LayoutMode)GUILayout.Toolbar((int)m_taskData.layoutMode, layoutLabels, new[] { GUILayout.MinHeight(30), GUILayout.MinWidth(windowWidth - 1.0f), GUILayout.MaxWidth(windowWidth) });
            GUILayout.Space(6);

            // 4.1. Align Popup + Height for None
            if (m_taskData.layoutMode == RodinData.LayoutMode.None)
            {
                m_taskData.alignOption = (RodinData.AlignOption)EditorGUILayout.EnumPopup("Align:", m_taskData.alignOption);
                m_taskData.height = EditorGUILayout.FloatField("Height", Mathf.Clamp(m_taskData.height, 0.0f, float.MaxValue));
            }
            else if (m_taskData.layoutMode == RodinData.LayoutMode.BoundingBox)
            {
                //TODO: Must select only 1 object, not allowed to multi-select.
                //GUILayout.Label("Please select only one ROOT GameObject in the scene, and make sure that this object has no local transform.");
                GUILayout.Label(Selection.activeGameObject != null ? $"Selected GameObject (Root Object): {Selection.activeGameObject.transform.root.gameObject.name}" : "Selected GameObject: None");
                //GUILayout.Label(Selection.activeGameObject != null ? $"Selected GameObject (Root Object): {Selection.activeGameObject.name}" : "Selected GameObject: None");
            }
            else if (m_taskData.layoutMode == RodinData.LayoutMode.Voxel)
            {
                GUILayout.Label(Selection.activeGameObject != null ? $"Selected GameObject (Root Object): {Selection.activeGameObject.transform.root.gameObject.name}" : "Selected GameObject: None");
                m_taskData.voxelMode = (RodinData.VoxelMode)EditorGUILayout.EnumPopup("Mode:", m_taskData.voxelMode);
                m_taskData.scale = EditorGUILayout.FloatField("Scale", Mathf.Clamp(m_taskData.scale, 0.0f, 1.0f));
            }
            else if (m_taskData.layoutMode == RodinData.LayoutMode.PointCloud)
            {
                GUILayout.Label(Selection.activeGameObject != null ? $"Selected GameObject (Root Object): {Selection.activeGameObject.transform.root.gameObject.name}" : "Selected GameObject: None");
                m_taskData.uncertainty = EditorGUILayout.FloatField("Uncertainty", Mathf.Clamp(m_taskData.uncertainty, 0.01f, 0.05f));
            }

            GUILayout.Space(15);
            // 5. Load Image Button
            // Feature 1.1: Image or Text
            GUILayout.BeginHorizontal();
            string[] textToLabel = { "Image", "Text" };
            float textToWidth = m_taskData.textTo == RodinData.TextTo.Image ? windowWidth : windowWidth / 2.0f;
            m_taskData.textTo = (RodinData.TextTo)GUILayout.Toolbar((int)m_taskData.textTo, textToLabel, new[] { GUILayout.MinHeight(30), GUILayout.MinWidth(textToWidth - 2.0f), GUILayout.MaxWidth(textToWidth - 1.0f) });
            if (m_taskData.textTo == RodinData.TextTo.Text)
            {
                m_taskData.bypass = GUILayout.Toggle(m_taskData.genType == RodinData.GenType.Manual ? m_taskData.bypass : true, "Direct to 3D", new[] { GUILayout.MinHeight(30), GUILayout.MinWidth(textToWidth - 2.0f), GUILayout.MaxWidth(textToWidth - 1.0f) });
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            if (m_taskData.textTo == RodinData.TextTo.Image)
            {
                if (GUILayout.Button("Load Image"))
                {
                    string path = EditorUtility.OpenFilePanel("Choose Image", "", "png,jpg,jpeg");
                    if (!string.IsNullOrEmpty(path))
                    {
                        byte[] data = File.ReadAllBytes(path);
                        Texture2D tex = new Texture2D(2, 2);
                        tex.LoadImage(data);
                        importedTextures.Add(new RodinImage(data, tex));  // 添加到列表中
                    }
                }

                float imageSize = 128f;
                float padding = 50f;

                GUILayout.Label("Imported Images:");

                if (importedTextures.Count > 0)
                {
                    using (var scroll = new GUILayout.ScrollViewScope(scrollPosImages, GUILayout.Height(imageSize + padding)))
                    {
                        scrollPosImages = scroll.scrollPosition;
                        GUILayout.BeginVertical();

                        int count = importedTextures.Count;
                        for (int i = 0; i < count; i += 3)  // 3 images per row
                        {
                            GUILayout.BeginHorizontal();

                            // Image 1
                            GUILayout.BeginVertical(GUILayout.Width(imageSize));
                            GUILayout.Label(importedTextures[i].tex, GUILayout.Width(imageSize), GUILayout.Height(imageSize));
                            if (GUILayout.Button("Remove", GUILayout.Width(imageSize)))
                            {
                                importedTextures.RemoveAt(i);
                                GUILayout.EndVertical();
                                GUILayout.EndHorizontal();
                                break;
                            }
                            GUILayout.EndVertical();

                            // Image 2
                            if (i + 1 < count)
                            {
                                GUILayout.BeginVertical(GUILayout.Width(imageSize));
                                GUILayout.Label(importedTextures[i + 1].tex, GUILayout.Width(imageSize), GUILayout.Height(imageSize));
                                if (GUILayout.Button("Remove", GUILayout.Width(imageSize)))
                                {
                                    importedTextures.RemoveAt(i + 1);
                                    GUILayout.EndVertical();
                                GUILayout.EndHorizontal();
                                    break;
                                }
                                GUILayout.EndVertical();

                                // Image 3
                                if (i + 2 < count)
                                {
                                    GUILayout.BeginVertical(GUILayout.Width(imageSize));
                                    GUILayout.Label(importedTextures[i + 2].tex, GUILayout.Width(imageSize), GUILayout.Height(imageSize));
                                    if (GUILayout.Button("Remove", GUILayout.Width(imageSize)))
                                    {
                                        importedTextures.RemoveAt(i + 2);
                                        GUILayout.EndVertical();
                                GUILayout.EndHorizontal();
                                        break;
                                    }
                                    GUILayout.EndVertical();
                                }
                                else
                                {
                                    GUILayout.Space(imageSize);
                                }
                            }
                            else
                            {
                                GUILayout.Space(imageSize);
                            }

                            GUILayout.EndHorizontal();
                        }

                        GUILayout.EndVertical();
                    }
                }
                else
                {
                    GUILayout.Box("No Image Loaded", GUILayout.Height(imageSize + padding), GUILayout.ExpandWidth(true));
                }
            }
            else
            {
                m_taskData.textInput = EditorGUILayout.TextField("Text Input", m_taskData.textInput);
            }

            GUILayout.Space(15);
            // 6. OneClick 区块
            m_taskData.genType = (RodinData.GenType)EditorGUILayout.EnumPopup(m_taskData.genType);

            if (m_taskData.genType == RodinData.GenType.OneClick)
            {
                GUILayout.Space(6);
                GUILayout.Label("Face Type");
                GUILayout.Space(3);
                m_taskData.polygonOption = (RodinData.PolygonOption)GUILayout.Toolbar((int)m_taskData.polygonOption, new string[] { "Quad", "Triangular" }, GUILayout.MinHeight(30));
                GUILayout.Space(6);
                m_taskData.quality = EditorGUILayout.IntField("Quality", Math.Clamp(m_taskData.quality, 2000, 60000));
                GUILayout.Space(6);
            }

            m_taskData.genVersion = (RodinData.GenVersion)GUILayout.Toolbar((int)m_taskData.genVersion, new string[] { "Gen-2.5", "Gen-2", "Gen-1.5" }, GUILayout.MinHeight(30));
            GUILayout.Space(6);

            string[] eventLabels = new string[] { "Default", "Focal", "Zero", "Speedy" };

            RodinData.GenMode[] eventValues = new RodinData.GenMode[] { RodinData.GenMode.Default, RodinData.GenMode.Detail, RodinData.GenMode.Smooth, RodinData.GenMode.Fast };

            //int selectedIndex = System.Array.IndexOf(eventValues, m_taskData.genMode);
            //int newIndex = EditorGUILayout.Popup(selectedIndex, eventLabels);
            //m_taskData.genMode = eventValues[newIndex];

            if (m_taskData.genVersion == GenVersion.Gen150)
            {
                GUILayout.Label("Gen Mode");
                GUILayout.Space(3);
                m_taskData.genMode = (RodinData.GenMode)GUILayout.Toolbar((int)m_taskData.genMode, eventLabels, new[] { GUILayout.MinHeight(30), GUILayout.MinWidth(windowWidth - 1.0f), GUILayout.MaxWidth(windowWidth) });
                GUILayout.Space(6);
            }
            else if (m_taskData.genVersion == GenVersion.Gen250)
            {
                GUILayout.Label("Thinking Effort");
                GUILayout.Space(3);
                // 耗时仅为 UI 提示，不进协议。
                string[] brilliantLabels = new string[] { "Extreme-Low\n(~4s)", "Low\n(~9s)", "Medium\n(~20s)", "High\n(~40s)", "Extreme-High\n(~80s)" };
                m_taskData.brilliantMode = (RodinData.BrilliantMode)GUILayout.Toolbar((int)m_taskData.brilliantMode, brilliantLabels, new[] { GUILayout.MinHeight(40), GUILayout.MinWidth(windowWidth - 1.0f), GUILayout.MaxWidth(windowWidth) });
                GUILayout.Space(6);
            }
            

            // 1. Mat Toggle Buttons
            float matHeight = 50.0f;
            GUILayout.BeginHorizontal();
            m_taskData.materialMode = GUILayout.Toggle(m_taskData.materialMode == RodinData.MaterialMode.Rodin_Shaded, "Shaded", "Button", new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f), GUILayout.MinHeight(matHeight) }) ? RodinData.MaterialMode.Rodin_Shaded : m_taskData.materialMode;
            m_taskData.materialMode = GUILayout.Toggle(m_taskData.materialMode == RodinData.MaterialMode.Rodin_PBR, " PBR ", "Button", new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f), GUILayout.MinHeight(matHeight) }) ? RodinData.MaterialMode.Rodin_PBR : m_taskData.materialMode;

            // 2. Dropdown for Resolution
            GUILayout.BeginVertical();
            if (m_taskData.genMode == GenMode.Fast && m_taskData.genVersion == GenVersion.Gen150)
            {
                if (m_taskData.genMode != lastGenMode)
                {
                    m_taskData.resolution = RodinData.ResolutionFast.Rodin_1K;
                }
                m_taskData.resolution = GUILayout.Toggle((RodinData.ResolutionFast)m_taskData.resolution == RodinData.ResolutionFast.Rodin_1K, "1K", "Button", new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f), GUILayout.MinHeight(matHeight) }) ? RodinData.ResolutionFast.Rodin_1K : m_taskData.resolution;
            }
            else
            {
                // 切入 Gen-2.5 时默认选中 4K（用户仍可自由切回 2K）；其余情况切换 Gen Mode 时回退 2K。
                if (m_taskData.genVersion == GenVersion.Gen250 && lastGenVersion != GenVersion.Gen250)
                {
                    m_taskData.resolution = RodinData.Resolution.Rodin_4K;
                }
                else if (m_taskData.genMode != lastGenMode)
                {
                    m_taskData.resolution = RodinData.Resolution.Rodin_2K;
                }

                m_taskData.resolution = GUILayout.Toggle((RodinData.Resolution)m_taskData.resolution == RodinData.Resolution.Rodin_2K, "2K", "Button", new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f), GUILayout.MinHeight(matHeight / 2 - 1) }) ? RodinData.Resolution.Rodin_2K : m_taskData.resolution;
                m_taskData.resolution = GUILayout.Toggle((RodinData.Resolution)m_taskData.resolution == RodinData.Resolution.Rodin_4K, "4K", "Button", new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f), GUILayout.MinHeight(matHeight / 2 - 1) }) ? RodinData.Resolution.Rodin_4K : m_taskData.resolution;
            }
            
            GUILayout.EndVertical();

            //if (m_taskData.genMode == GenMode.Fast)
            //{
            //    m_taskData.resolution = (RodinData.ResolutionFast)EditorGUILayout.EnumPopup((RodinData.ResolutionFast)m_taskData.resolution, new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f) });
            //}
            //else
            //{
            //    m_taskData.resolution = (RodinData.Resolution)EditorGUILayout.EnumPopup((RodinData.Resolution)m_taskData.resolution, new[] { GUILayout.MinWidth(windowWidth / 3.0f - 1.0f), GUILayout.MaxWidth(windowWidth / 3.0f) });
            //}

            GUILayout.EndHorizontal();
            GUILayout.Space(15);
            #endregion

            GUILayout.Space(12);
            if (GUILayout.Button("Submit", GUILayout.Height(30)))
            {
                Debug.Log("Submitted settings.");
                SubmitTask();
            }

            GUILayout.Space(12);
            EditorGUILayout.LabelField("任务状态列表", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            //List<string> infos = new List<string>(taskManager.GetTaskInfos());
            Dictionary<RodinTask, string> taskInfos = taskManager.GetTaskInfoDict();

            //if (infos.Count == 0)
            if (taskInfos.Count == 0)
            {
                EditorGUILayout.HelpBox("当前无任务运行", MessageType.Info);
            }
            else
            {
                //foreach (var info in infos)
                foreach (var pair in taskInfos)
                {
                    string info = pair.Value;
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(info, EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("X"))
                    {
                        taskManager.RemoveTaskById(pair.Key.Id);
                        rodinServer.PopTaskAll(pair.Key.Id);
                    }
                    GUILayout.EndHorizontal();
                    EditorGUILayout.Space(2);
                }
                Repaint();
            }

            // Update all recorded params
            lastGenMode = m_taskData.genMode;
            lastGenVersion = m_taskData.genVersion;
        }

        private void OnDestroy()
        {
            if (rodinServer != null)
            {
                rodinServer.CloseServer();
                rodinServer = null;
            }
            if (rodinClient != null)
            {
                rodinClient.Disconnect();
                rodinClient = null;
            }
            if (taskManager != null)
            {
                taskManager.ShutdownTaskManager();
                taskManager = null;
            }
            importedTextures.Clear();
        }


        // Submit Task
        #region Submit Task Helper

        public Dictionary<string, object> PrepareMaterialConfig()
        {
            Dictionary<string, object> materialConfig = new Dictionary<string, object>();
            materialConfig["config"] = PrepareConfig();
            materialConfig["condition_type"] = m_taskData.layoutMode == RodinData.LayoutMode.None ? "image" :
                m_taskData.layoutMode == RodinData.LayoutMode.BoundingBox ? "bbox" :
                m_taskData.layoutMode == RodinData.LayoutMode.Voxel ? "voxel" :
                m_taskData.layoutMode == RodinData.LayoutMode.PointCloud ? "pointCloud" : "unknown";
            return materialConfig;
        }
        private Dictionary<string, object> PrepareConfig() // dump_config()
        {
            Dictionary<string, object> config = new Dictionary<string, object>();
            config["type"] = m_taskData.genType == RodinData.GenType.OneClick ? "OneClick" : "Manual";
            if (m_taskData.resolution.GetType() == typeof(RodinData.ResolutionFast) && m_taskData.genMode == RodinData.GenMode.Fast)
            {
                config["material"] = new Dictionary<string, object> {
                    { "type", new List<string> { m_taskData.materialMode == RodinData.MaterialMode.Rodin_Shaded ? "Shaded" : "PBR" } },
                    { "resolution", "1K" }
                }; // get_material_config()
            }
            else
            {
                config["material"] = new Dictionary<string, object> {
                    { "type", new List<string> { m_taskData.materialMode == RodinData.MaterialMode.Rodin_Shaded ? "Shaded" : "PBR" } },
                    { "resolution", (RodinData.Resolution)m_taskData.resolution == RodinData.Resolution.Rodin_2K ? "2K" : "4K" }
                };
            }; // get_material_config()
            
            config["height"] = m_taskData.height;
            config["align"] = m_taskData.alignOption == RodinData.AlignOption.Center ? "Center" : "Bottom";
            config["voxel_condition_cfg"] = m_taskData.voxelMode == RodinData.VoxelMode.Strict ? "Strict" : "Rough";
            config["voxel_condition_weight"] = m_taskData.scale;
            config["pcd_condition_uncertainty"] = m_taskData.uncertainty;
            config["polygon"] = m_taskData.polygonOption == RodinData.PolygonOption.Quad ? "Quad" : "Raw";
            // Gen-2 与 Gen-2.5 均使用 Brilliant；Gen-1.5 保持用户所选 mode。
            config["mode"] = (m_taskData.genVersion == RodinData.GenVersion.Gen200 || m_taskData.genVersion == RodinData.GenVersion.Gen250) ? "Brilliant" :
                m_taskData.genMode == RodinData.GenMode.Default ? "Default" :
                m_taskData.genMode == RodinData.GenMode.Detail ? "Detail" :
                m_taskData.genMode == RodinData.GenMode.Smooth ? "Smooth" : "Fast";

            // 仅 Gen-2.5 额外携带所选 Thinking Effort 档位。
            if (m_taskData.genVersion == RodinData.GenVersion.Gen250)
            {
                config["brilliant_mode"] = RodinData.GetBrilliantModeValue(m_taskData.brilliantMode);
            }

            // Feature 1.1
            config["quality"] = m_taskData.quality;
            config["textTo"] = m_taskData.textTo == TextTo.Text;
            config["bypass"] = m_taskData.bypass;
            config["text"] = m_taskData.textInput;

            // Return FBX
            config["model"] = "fbx";

            return config;
        }

        private List<Dictionary<string, object>> PrepareImages()
        {
            Debug.Log("Prepare Images...");
            List<Dictionary<string, object>> images = new List<Dictionary<string, object>>();
            foreach (var rodinImage in importedTextures)
            {
                byte[] pngData = rodinImage.tex.EncodeToPNG();

                if (pngData == null)
                {
                    Debug.LogError("Failed when encoding the image.");
                    return null;
                }

                string tempFilePath = Path.Combine(Path.GetTempPath(), "rodin_tmp.png");
                File.WriteAllBytes(tempFilePath, pngData);
                Debug.Log($"\tTemp Image -> {tempFilePath}");

                string base64Str = Convert.ToBase64String(pngData);
                byte[] base64Bytes = Encoding.UTF8.GetBytes("data:image/png;base64," + base64Str);

                string md5Str;
                using (var md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(base64Bytes);
                    StringBuilder sb = new StringBuilder();
                    foreach (byte b in hash)
                        sb.Append(b.ToString("x2"));
                    md5Str = sb.ToString();
                }

                Dictionary<string, object> image = new Dictionary<string, object>
                {
                    { "format", "png" },
                    { "length", base64Bytes.Length },
                    { "md5", md5Str },
                    { "content", Encoding.UTF8.GetString(base64Bytes) }
                };

                File.Delete(tempFilePath);

                images.Add(image);
            }
            
            return images;
        }


        public Dictionary<string, object> PrepareMesh(GameObject go)
        {
            Debug.Log("Prepare Mesh...");

#if RODIN_HAS_UNITYGLTF
            var exporter = new GLTFSceneExporter(new Transform[] { go.transform }, new ExportContext());
            exporter.SaveGLB(RodinUtils.GetCachePath(), "rodin_tmp.glb");
            Debug.Log($"\tTemp Mesh -> {Path.Combine(RodinUtils.GetCachePath(), "rodin_tmp.glb")}");

            byte[] glbBytes = File.ReadAllBytes(Path.Combine(RodinUtils.GetCachePath(), "rodin_tmp.glb"));
            string base64Str = Convert.ToBase64String(glbBytes);
            byte[] base64Bytes = Encoding.UTF8.GetBytes("data:model/glb;base64," + base64Str);

            string md5Str;
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(base64Bytes);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                md5Str = sb.ToString();
            }

            File.Delete(Path.Combine(Path.GetTempPath(), "rodin_tmp.glb"));

            return new Dictionary<string, object>
            {
                { "format", "glb" },
                { "length", base64Bytes.Length },
                { "md5", md5Str },
                { "content", Encoding.UTF8.GetString(base64Bytes) }
            };
#else
            return new Dictionary<string, object>();
#endif
        }
        #endregion

        public void Connecting(RodinTask task)
        {
            //if (task.HasSubprocess()) { return; }

        }

        private void OpenURL(string url)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            Process.Start("open", url);
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            Process.Start("xdg-open", url);
#else
            Application.OpenURL(url);
#endif
        }

        public async void SubmitTask()
        {
            Debug.Log("Submit Prepare...");

            if (m_taskData.textTo == TextTo.Text && m_taskData.textInput.IsNullOrEmpty())
            {
                Debug.LogError("Text input is empty. Please provide a valid text input.");
                RodinLogger.Error("Submit aborted: text input is empty.");
                return;
            }

            RodinTask taskSubmit = new RodinTask(rodinClient, this);
            var submitData = new Dictionary<string, object>();
            submitData["type"] = m_taskData.layoutMode == RodinData.LayoutMode.None ? "image" :
                m_taskData.layoutMode == RodinData.LayoutMode.BoundingBox ? "bbox" :
                m_taskData.layoutMode == RodinData.LayoutMode.Voxel ? "voxel" :
                m_taskData.layoutMode == RodinData.LayoutMode.PointCloud ? "pointCloud" : "unknown";
            submitData["id"] = taskSubmit.Id;
            submitData["prompt"] = "";
            submitData["config"] = PrepareConfig();
            submitData["image"] = PrepareImages();

            #region Prepare Mesh
            m_taskData.go = Selection.activeGameObject?.transform.root.gameObject;
            if (Selection.activeGameObject != null && m_taskData.layoutMode != RodinData.LayoutMode.None)
            {
                submitData["condition"] = PrepareMesh(Selection.activeGameObject.transform.root.gameObject);
            }
            #endregion

            Debug.Log($"Create Task: {JsonConvert.SerializeObject(submitData)}");

            // 仅记录 config 摘要，避免把 image/condition 的 base64 写入日志文件。
            string versionName = m_taskData.genVersion == RodinData.GenVersion.Gen250 ? "Gen-2.5" :
                m_taskData.genVersion == RodinData.GenVersion.Gen200 ? "Gen-2" : "Gen-1.5";
            string brilliantInfo = m_taskData.genVersion == RodinData.GenVersion.Gen250
                ? RodinData.GetBrilliantModeValue(m_taskData.brilliantMode) : "-";
            string resolutionInfo = "-";
            if (submitData["config"] is Dictionary<string, object> cfg)
            {
                string modeInfo = cfg.TryGetValue("mode", out object modeVal) ? modeVal?.ToString() : "-";
                if (cfg.TryGetValue("material", out object matVal) && matVal is Dictionary<string, object> mat
                    && mat.TryGetValue("resolution", out object resVal))
                {
                    resolutionInfo = resVal?.ToString();
                }
                RodinLogger.Info(
                    $"Submit task: version={versionName}, mode={modeInfo}, brilliant_mode={brilliantInfo}, " +
                    $"resolution={resolutionInfo}, condition={submitData["type"]}, textTo={m_taskData.textTo}, " +
                    $"images={importedTextures?.Count ?? 0}", taskSubmit.Id);
            }

            taskSubmit.SetData(submitData);

            string url = $"{rodinServer.apiAddress}?show=plugin";
            if (!RodinServer.websiteConnected)
            {
                OpenURL(url);
            }
            else
            {
                Dictionary<string, object> sendData = new Dictionary<string, object>();
                sendData["type"] = "ping_client";
                rodinServer.BroadcastMessage(JsonConvert.SerializeObject(sendData));
            }
            
            taskManager.AddTask(taskSubmit);
        }
    }
}
