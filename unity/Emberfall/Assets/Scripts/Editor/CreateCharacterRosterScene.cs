using System;
using Emberfall.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>Editor authoring entry point for the CharacterRoster scene.</summary>
    public static class CreateCharacterRosterScene
    {
        private const string ScenePath = "Assets/Scenes/CharacterRoster.unity";
        private const string TemplateScenePath = "Assets/Scenes/DeckbuilderSpike.unity";
        private const string UxmlPath = "Assets/UI/CharacterRoster/CharacterRosterScreen.uxml";
        private const string PanelPath = "Assets/UI/Deckbuilder/DeckbuilderPanelSettings.asset";

        [MenuItem("Emberfall/UI/Rebuild Character Roster Scene")]
        public static void Build()
        {
            AssetDatabase.ImportAsset(UxmlPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var source = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (source == null) throw new InvalidOperationException($"No VisualTreeAsset at {UxmlPath}.");
            if (panel == null) throw new InvalidOperationException($"No PanelSettings at {PanelPath}.");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.043f, 0.055f, 0.075f, 1f);
            cameraObject.AddComponent<AudioListener>();

            // Unity 6 can silently serialize a newly-added UIDocument's PanelSettings as null in
            // batch mode. Clone the project-proven Deckbuilder document, whose reference is already
            // persistent, then replace only the screen asset and controller.
            Scene templateScene = EditorSceneManager.OpenScene(TemplateScenePath, OpenSceneMode.Additive);
            UIDocument templateDocument = null;
            foreach (GameObject root in templateScene.GetRootGameObjects())
            {
                templateDocument = root.GetComponentInChildren<UIDocument>(includeInactive: true);
                if (templateDocument != null) break;
            }
            if (templateDocument == null)
                throw new InvalidOperationException($"No UIDocument found in {TemplateScenePath}.");

            templateDocument.gameObject.SetActive(false);
            var rosterObject = UnityEngine.Object.Instantiate(templateDocument.gameObject);
            rosterObject.name = "CharacterRoster";
            SceneManager.MoveGameObjectToScene(rosterObject, scene);
            EditorSceneManager.CloseScene(templateScene, removeScene: true);

            foreach (Component component in rosterObject.GetComponents<Component>())
            {
                if (component is Transform || component is UIDocument) continue;
                UnityEngine.Object.DestroyImmediate(component);
            }

            var document = rosterObject.GetComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = source;
            rosterObject.AddComponent<CharacterRosterController>();
            rosterObject.SetActive(true);
            EditorUtility.SetDirty(document);
            EditorSceneManager.MarkSceneDirty(scene);

            if (document.panelSettings != panel || document.visualTreeAsset != source)
                throw new InvalidOperationException("UIDocument rejected its PanelSettings or UXML assignment.");

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Unity could not save {ScenePath}.");

            EmberfallBuild.SyncSceneList();
            AssetDatabase.SaveAssets();
            Debug.Log($"[CreateCharacterRosterScene] Authored {ScenePath}; UIDocument has source + panel settings.");
        }
    }
}
