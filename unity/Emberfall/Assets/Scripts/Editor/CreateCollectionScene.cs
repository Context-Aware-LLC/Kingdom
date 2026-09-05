using System;
using Emberfall.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>Editor authoring entry point for the Collection scene.</summary>
    public static class CreateCollectionScene
    {
        private const string ScenePath = "Assets/Scenes/Collection.unity";

        /// <summary>
        /// The UIDocument donor for the workaround below — the OTHER authored scene, never this
        /// tool's own output.
        ///
        /// <para><b>Why not <c>ScenePath</c>.</b> Both authoring tools used to clone
        /// <c>DeckbuilderSpike.unity</c>, which #154 retired. Pointing this at its own output would
        /// still run — the donor is read off disk before the save overwrites it — but the tool could
        /// then only <i>recycle</i> Collection.unity, never rebuild it from a broken one, which is
        /// the single thing it exists for. <see cref="CreateCharacterRosterScene"/> donates in the
        /// opposite direction, so neither tool depends on itself.</para>
        /// </summary>
        private const string TemplateScenePath = "Assets/Scenes/CharacterRoster.unity";
        private const string UxmlPath = "Assets/UI/Collection/CollectionScreen.uxml";
        private const string PanelPath = "Assets/UI/Common/EmberfallPanelSettings.asset";

        // MOVED OUT OF "Emberfall/UI/" AND GATED 2026-09-04 (Sam: "so they aren't liable to be
        // accidentally triggered"). This sits one item away from Check UXML Integrity, which is a
        // read-only check — and it is not: NewScene(EmptyScene, Single) followed by SaveScene over
        // ScenePath discards whatever is in that scene, with no undo. The submenu keeps it off the
        // path of a stray click; the dialog below is what actually stops it.
        [MenuItem("Emberfall/Rebuild Scene — OVERWRITES/Collection")]
        public static void Build()
        {
            // Interactive only. A -batchmode caller asked for this by name and has no one to
            // answer a dialog; leaving it unguarded there would trade a silent overwrite for a
            // ten-minute CI hang, which is the worse of the two. Nothing calls Build() headlessly
            // today — this is so that adding such a caller does not deadlock.
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog(
                    "Rebuild the Collection scene?",
                    $"This REPLACES {ScenePath} with a freshly generated scene.\n\n"
                    + "Anything hand-authored in it — objects, components, inspector values — is "
                    + "discarded and cannot be undone. The UXML and PanelSettings it builds from "
                    + "are not touched.\n\nRebuild it?",
                    "Rebuild — discard the current scene", "Cancel"))
                return;

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
            // batch mode. Clone a document whose reference is already persistent on disk — see
            // TemplateScenePath for which one and why — then replace only the screen asset and
            // controller.
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
            var collectionObject = UnityEngine.Object.Instantiate(templateDocument.gameObject);
            collectionObject.name = "Collection";
            SceneManager.MoveGameObjectToScene(collectionObject, scene);
            EditorSceneManager.CloseScene(templateScene, removeScene: true);

            foreach (Component component in collectionObject.GetComponents<Component>())
            {
                if (component is Transform || component is UIDocument) continue;
                UnityEngine.Object.DestroyImmediate(component);
            }

            var document = collectionObject.GetComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = source;
            collectionObject.AddComponent<CollectionController>();
            collectionObject.SetActive(true);
            EditorUtility.SetDirty(document);
            EditorSceneManager.MarkSceneDirty(scene);

            if (document.panelSettings != panel || document.visualTreeAsset != source)
                throw new InvalidOperationException("UIDocument rejected its PanelSettings or UXML assignment.");

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Unity could not save {ScenePath}.");

            EmberfallBuild.SyncSceneList();
            AssetDatabase.SaveAssets();
            Debug.Log($"[CreateCollectionScene] Authored {ScenePath}; UIDocument has source + panel settings.");
        }
    }
}
