using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine.Rendering;

namespace Rodin
{
    public static class RodinUtils
    {
        public static string GetJsonSummary(JToken token, int maxStringLength = 50)
        {
            if (token == null)
                return "null";

            if (token.Type == JTokenType.Object)
            {
                JObject obj = (JObject)token;
                JObject summary = new JObject();

                foreach (var pair in obj)
                {
                    if (pair.Value.Type == JTokenType.String)
                    {
                        string val = pair.Value.ToString();
                        summary[pair.Key] = val.Length > maxStringLength ? val.Substring(0, maxStringLength) + "..." : val;
                    }
                    else if (pair.Value.Type == JTokenType.Object || pair.Value.Type == JTokenType.Array)
                    {
                        summary[pair.Key] = GetJsonSummary(pair.Value, maxStringLength);
                    }
                    else
                    {
                        summary[pair.Key] = pair.Value;
                    }
                }
                return summary.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            else if (token.Type == JTokenType.Array)
            {
                JArray array = (JArray)token;
                JArray summaryArray = new JArray();
                foreach (var item in array)
                {
                    summaryArray.Add(GetJsonSummary(item, maxStringLength));
                }
                return summaryArray.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            else if (token.Type == JTokenType.String)
            {
                string val = token.ToString();
                return val.Length > maxStringLength ? val.Substring(0, maxStringLength) + "..." : val;
            }
            else
            {
                return token.ToString();
            }
        }

        public static string GetCachePath()
        {
            // Project_root/Temp
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string tempPath = System.IO.Path.Combine(projectRoot, "Temp");

            if (!System.IO.Directory.Exists(tempPath))
            {
                System.IO.Directory.CreateDirectory(tempPath);
            }
            return tempPath;
        }

        public static bool isDataValid(JToken token)
        {
            return !(token == null || token.Type == JTokenType.Null ||
           (token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString())) ||
           (token.Type == JTokenType.Array && !token.HasValues) ||
           (token.Type == JTokenType.Object && !token.HasValues));
        }
    }

    public static class TexturePreprocessor
    {
        public static Shader standard = null;

        public static void EnsureShaderIncluded()
        {
            RenderPipelineAsset rpAsset = GraphicsSettings.currentRenderPipeline;
            string shaderName;

            if (rpAsset == null)
            {
                // Built-in RP
                shaderName = "Standard";
            }
#if UNITY_RENDER_PIPELINE_UNIVERSAL
        else if (rpAsset is UniversalRenderPipelineAsset)
        {
            shaderName = "Universal Render Pipeline/Lit";
        }
#endif
#if UNITY_RENDER_PIPELINE_HDRP
        else if (rpAsset is HDRenderPipelineAsset)
        {
            shaderName = "HDRP/Lit";
        }
#endif
            else
            {
                Debug.LogWarning($"⚠️ 检测到自定义或未知的 RenderPipeline: {rpAsset.GetType().FullName}，请手动指定基础 Shader，并重命名为Standard后，重新打开插件窗口。");
                return;
            }

            standard = Shader.Find(shaderName);
            if (standard == null)
            {
                Debug.LogError($"找不到名为{shaderName}的 Shader，请确认项目设置。");
                return;
            }

            var graphicsSettingsObj = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
            SerializedObject so = new SerializedObject(graphicsSettingsObj);
            SerializedProperty alwaysIncludedShaders = so.FindProperty("m_AlwaysIncludedShaders");

            bool alreadyIncluded = false;
            for (int i = 0; i < alwaysIncludedShaders.arraySize; i++)
            {
                SerializedProperty element = alwaysIncludedShaders.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == standard)
                {
                    alreadyIncluded = true;
                    break;
                }
            }

            if (!alreadyIncluded)
            {
                int index = alwaysIncludedShaders.arraySize;
                alwaysIncludedShaders.InsertArrayElementAtIndex(index);
                alwaysIncludedShaders.GetArrayElementAtIndex(index).objectReferenceValue = standard;

                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                Debug.Log("✅ 已将 Standard Shader 添加到 GraphicsSettings → Always Included Shaders。");
            }
            else
            {
                Debug.Log("ℹ️ Standard Shader 已存在于 Always Included Shaders。");
            }
        }

        public static void MakeTextureReadableAndSetType(Texture2D tex, bool isNormalMap = false)
        {
            if (tex == null) return;

            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool needReimport = false;

                if (!importer.isReadable)
                {
                    importer.isReadable = true;
                    needReimport = true;
                }

                if (isNormalMap && importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    needReimport = true;
                }

                if (!isNormalMap && importer.textureType != TextureImporterType.Default)
                {
                    importer.textureType = TextureImporterType.Default;
                    needReimport = true;
                }

                if (needReimport)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            }
        }
    }
}

