using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Kingdom.Review
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
        private const string BundleKey = "Kingdom.Review.BundleDirectory";

        [MenuItem("Kingdom/RL/Open Decision Reviewer %#r", priority = 100)]
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

        [MenuItem("Kingdom/RL/Open Decision Reviewer (choose bundle)…", priority = 101)]
        public static void OpenWithPrompt()
        {
            string directory = Prompt(EditorPrefs.GetString(BundleKey, ""));
            if (!IsBundle(directory)) return;
            EditorPrefs.SetString(BundleKey, directory);
            Launch(directory);
        }

        [MenuItem("Kingdom/RL/Capture One Action (game view)", priority = 103)]
        public static void OpenNativeCapture()
        {
            string directory = EditorPrefs.GetString(BundleKey, "");
            if (!IsBundle(directory))
            {
                directory = Prompt(directory);
                if (!IsBundle(directory)) return;
                EditorPrefs.SetString(BundleKey, directory);
            }
            Launch(directory, captureOneAction: true);
        }

        [MenuItem("Kingdom/RL/Capture One Action (choose bundle)…", priority = 104)]
        public static void OpenNativeCaptureWithPrompt()
        {
            string directory = Prompt(EditorPrefs.GetString(BundleKey, ""));
            if (!IsBundle(directory)) return;
            EditorPrefs.SetString(BundleKey, directory);
            Launch(directory, captureOneAction: true);
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
                    + "dotnet run --project src/Kingdom.Tools.DecisionReview -- build "
                    + "--corpus <dir> --sample <file> --cards <dir> --out <dir>",
                    "OK");
                return "";
            }
            return chosen;
        }

        private static bool IsBundle(string directory) =>
            !string.IsNullOrEmpty(directory)
            && File.Exists(Path.Combine(directory, "bundle-manifest.json"));

        private static void Launch(string bundleDirectory, bool captureOneAction = false)
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                Debug.Log("[DecisionReview] Stopped play mode; run the menu item again.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Build(bundleDirectory, captureOneAction);
            EditorApplication.isPlaying = true;
        }

        /// <summary>The whole scene: a configured camera and one board object.</summary>
        private static void Build(string bundleDirectory, bool captureOneAction)
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
            reviewer.AddComponent<DecisionReviewBootstrap>().Configure(
                bundleDirectory, captureOneAction: captureOneAction);

            if (!captureOneAction) DecisionReviewWindow.Open();
            else EditorApplication.delayCall += () =>
            {
                System.Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType != null) EditorWindow.GetWindow(gameViewType).Focus();
            };
            Debug.Log($"[DecisionReview] Scene built for {bundleDirectory}");
        }
    }
}
