#if RODIN_HAS_UNITYGLTF
#else
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class RodinGLTFInstaller
{
    private static AddRequest _request;
    private static ListRequest _listRequest;
    private static string requiredVersion;

    public static int CompareVersion(string v1, string v2)
    {
        string[] parts1 = v1.Split('.');
        string[] parts2 = v2.Split('.');

        int length = Mathf.Max(parts1.Length, parts2.Length);

        for (int i = 0; i < length; i++)
        {
            int num1 = i < parts1.Length ? int.Parse(parts1[i]) : 0;
            int num2 = i < parts2.Length ? int.Parse(parts2[i]) : 0;

            if (num1 > num2) return 1;
            if (num1 < num2) return -1;
        }

        return 0;
    }

    [MenuItem("Tools/Rodin/Install UnityGLTF")]
    private static void InstallUnityGLTF()
    {
#if UNITY_6000_0_OR_NEWER
        requiredVersion = "2.17.0";
#else
        requiredVersion = "2.14.1";
#endif
        _listRequest = Client.List(true); // true = include dependencies
        EditorApplication.update += OnListProgress;

        Debug.Log("Installing UnityGLTF package from GitHub...");

//#if UNITY_6000_0_OR_NEWER
//        _request = Client.Add("https://github.com/KhronosGroup/UnityGLTF.git#release/2.17.0");
//#else
//        _request = Client.Add("https://github.com/KhronosGroup/UnityGLTF.git#release/2.14.1");
//#endif
        
//        EditorApplication.update += Progress;
    }

    private static void OnListProgress()
    {
        if (!_listRequest.IsCompleted)
            return;

        EditorApplication.update -= OnListProgress;

        if (_listRequest.Status == StatusCode.Failure)
        {
            Debug.LogError("[RodinBridge] Failed to list packages: " + _listRequest.Error?.message);
            return;
        }

        var pkg = _listRequest.Result.FirstOrDefault(p => p.name == "org.khronos.unitygltf");
        if (pkg != null)
        {
            Debug.Log($"[RodinBridge] UnityGLTF already installed: {pkg.version}");

            if (CompareVersion(pkg.version, requiredVersion) >= 0)
            {
                Debug.Log("[RodinBridge] Version matches. Adding macro only.");
                AddDefineSymbol("RODIN_HAS_UNITYGLTF");
                AssetDatabase.Refresh();
                return;
            }
            else
            {
                Debug.Log($"[RodinBridge] Version mismatch: required {requiredVersion}, found {pkg.version}. Reinstalling...");
            }
        }

        _request = Client.Add($"https://github.com/KhronosGroup/UnityGLTF.git#release/{requiredVersion}");
        EditorApplication.update += Progress;
    }

    private static void Progress()
    {
        if (!_request.IsCompleted)
            return;

        EditorApplication.update -= Progress;

        if (_request.Status == StatusCode.Success)
        {
            Debug.Log("✅ UnityGLTF installed successfully.");
            AddDefineSymbol("RODIN_HAS_UNITYGLTF");
        }
        else if (_request.Status >= StatusCode.Failure)
        {
            Debug.LogError($"❌ UnityGLTF install failed: {_request.Error?.message}");
        }
    }

    private static void AddDefineSymbol(string define)
    {
        //var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
        //var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
        var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);


        if (!defines.Contains(define))
        {
            defines = string.IsNullOrEmpty(defines) ? define : $"{defines};{define}";
            //PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, defines);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, defines);
            Debug.Log($"Added scripting define symbol: {define}");
        }
    }
}
#endif