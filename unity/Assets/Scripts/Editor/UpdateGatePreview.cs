using Kingdom.Net.Client.Gate;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Review the forced-update gate's three states in Play mode without a stale build or a
    /// dead server. Each item replaces the last; Close, or leaving Play mode, takes it down. The
    /// buttons are live: Update opens the store listing, Retry reloads the scene.</summary>
    [InitializeOnLoad]
    public static class UpdateGatePreview
    {
        private static GameObject _host;

        static UpdateGatePreview()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) Close();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Close;
        }

        [MenuItem("Kingdom/Preview/Update Gate/Waiting")]
        private static void Waiting() => Open(null);

        [MenuItem("Kingdom/Preview/Update Gate/Update Required")]
        private static void Required() => Open(UpdateGateVerdict.UpdateRequired);

        [MenuItem("Kingdom/Preview/Update Gate/Unreachable")]
        private static void Unreachable() => Open(UpdateGateVerdict.Unreachable);

        [MenuItem("Kingdom/Preview/Update Gate/Close")]
        private static void Close()
        {
            GameObject host = _host;
            _host = null;
            if (host == null) return;
            if (Application.isPlaying) Object.Destroy(host);
            else Object.DestroyImmediate(host);
        }

        [MenuItem("Kingdom/Preview/Update Gate/Waiting", true)]
        [MenuItem("Kingdom/Preview/Update Gate/Update Required", true)]
        [MenuItem("Kingdom/Preview/Update Gate/Unreachable", true)]
        private static bool CanOpen() => Application.isPlaying;

        private static void Open(UpdateGateVerdict? verdict)
        {
            if (!Application.isPlaying) return;
            Close();
            _host = UpdateGateOverlay.Preview(verdict);
        }
    }
}
