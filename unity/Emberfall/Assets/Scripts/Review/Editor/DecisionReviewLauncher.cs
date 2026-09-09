using System.IO;
using Emberfall.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Emberfall.Review
{
    /// <summary>
    /// Opens the decision reviewer.
    ///
    /// THE SCENE IS BUILT IN CODE, NOT AUTHORED. <see cref="ProceduralBoardSetup"/> already
    /// constructs the whole anchor hierarchy at runtime — the shipping board works without a
    /// hand-crafted scene — so there is nothing left for a .unity asset to carry but a camera and
    /// three components. Generating it means the reviewer has no scene asset to drift, no GUIDs to
    /// orphan, and no risk of a Unity run rewriting a tracked file that is not yours.
    /// </summary>
    public static class DecisionReviewLauncher
    {
        private const string BundleKey = "Emberfall.Review.BundleDirectory";

        [MenuItem("Emberfall/Review/Open Decision Reviewer %#r", priority = 100)]
        public static void Open()
        {
            string directory = EditorPrefs.GetString(BundleKey, "");
            if (!IsBundle(directory))
            {
                directory = Prompt(directory);
                if (!IsBundle(directory)) return;
                EditorPrefs.SetString(BundleKey, directory);
            }
            Launch(directory);
        }

        [MenuItem("Emberfall/Review/Open Decision Reviewer (choose bundle)…", priority = 101)]
        public static void OpenWithPrompt()
        {
            string directory = Prompt(EditorPrefs.GetString(BundleKey, ""));
            if (!IsBundle(directory)) return;
            EditorPrefs.SetString(BundleKey, directory);
            Launch(directory);
        }

        private static string Prompt(string current)
        {
            string chosen = EditorUtility.OpenFolderPanel(
                "Select a decision-review bundle", Directory.Exists(current) ? current : "", "");
            if (string.IsNullOrEmpty(chosen)) return "";
            if (!IsBundle(chosen))
            {
                EditorUtility.DisplayDialog(
                    "Not a review bundle",
                    $"'{chosen}' has no bundle-manifest.json.\n\nBuild one with:\n"
                    + "dotnet run --project src/Emberfall.Tools.DecisionReview -- build "
                    + "--corpus <dir> --sample <file> --cards <dir> --out <dir>",
                    "OK");
                return "";
            }
            return chosen;
        }

        private static bool IsBundle(string directory) =>
            !string.IsNullOrEmpty(directory)
            && File.Exists(Path.Combine(directory, "bundle-manifest.json"));

        private static void Launch(string bundleDirectory)
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                Debug.Log("[DecisionReview] Stopped play mode; run the menu item again.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Build(bundleDirectory);
            EditorApplication.isPlaying = true;
        }

        /// <summary>The whole scene: a configured camera and one board object.</summary>
        private static void Build(string bundleDirectory)
        {
            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            cameraGo.transform.SetPositionAndRotation(
                BoardCameraRig.AuthoredPosition, BoardCameraRig.AuthoredRotation);
            BoardCameraRig.Configure(camera);

            // Only the bootstrap. It builds and rebuilds the board itself under a child object,
            // because every frame is a different match and the acting player has to render at the
            // bottom in all of them — see DecisionReviewBootstrap.Show.
            var reviewer = new GameObject("DecisionReviewer");
            reviewer.AddComponent<DecisionReviewBootstrap>().Configure(bundleDirectory);

            DecisionReviewWindow.Open();
            Debug.Log($"[DecisionReview] Scene built for {bundleDirectory}");
        }
    }
}
