using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>Read-only structural gate for the roster scene's silent-failure edges.</summary>
    public static class CharacterRosterVerify
    {
        private const string ScenePath = "Assets/Scenes/CharacterRoster.unity";

        [MenuItem("Emberfall/UI/Verify Character Roster Scene")]
        public static void Check()
        {
            bool sceneExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            bool enabledInBuild = Array.Exists(EditorBuildSettings.scenes,
                entry => entry.enabled && entry.path == ScenePath);

            string absolute = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ScenePath);
            string yaml = File.Exists(absolute) ? File.ReadAllText(absolute) : "";
            bool hasController = yaml.Contains(
                "Emberfall.Presentation::Emberfall.Presentation.CharacterRosterController");
            bool hasPanel = Regex.IsMatch(yaml,
                @"m_PanelSettings: \{fileID: (?!0\})\d+, guid: [0-9a-f]{32}, type: 2\}");
            bool hasSource = Regex.IsMatch(yaml,
                @"sourceAsset: \{fileID: (?!0\})\d+, guid: [0-9a-f]{32}, type: 3\}");

            string report = $"[CharacterRosterVerify] scene={sceneExists}; build={enabledInBuild}; " +
                            $"controller={hasController}; panel={hasPanel}; source={hasSource}";
            if (sceneExists && enabledInBuild && hasController && hasPanel && hasSource)
                Debug.Log(report);
            else
                Debug.LogError(report);
        }
    }
}
