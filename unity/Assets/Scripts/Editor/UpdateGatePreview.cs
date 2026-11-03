using Kingdom.Net.Client.Gate;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Review the forced-update gate's three states without a stale build or a dead
    /// server. Each item replaces the last; Close, or leaving Play mode, takes it down. The buttons
    /// are live: Update opens the store listing, Retry reloads the scene.
    ///
    /// <para>Picked outside Play mode, it enters Play and opens the state once the boot loading
    /// screen has had time to clear. The pick is held in <see cref="SessionState"/> because entering
    /// Play reloads the domain and wipes statics.</para></summary>
    [InitializeOnLoad]
    public static class UpdateGatePreview
    {
        private const string PendingKey = "Kingdom.UpdateGatePreview.Pending";
        private const int None = -1, WaitingState = -2;
        // Past the boot loading screen's two-second floor, so the preview lands on the menu.
        private const double BootSettleSeconds = 3.0;

        private static GameObject _host;
        private static double _openAt;

        static UpdateGatePreview()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) Close();
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetInt(PendingKey, None) != None)
                {
                    _openAt = EditorApplication.timeSinceStartup + BootSettleSeconds;
                    EditorApplication.update += OpenWhenSettled;
                }
            };
            AssemblyReloadEvents.beforeAssemblyReload += Close;
        }

        [MenuItem("Kingdom/Preview/Update Gate/Waiting")]
        private static void Waiting() => Open(WaitingState);

        [MenuItem("Kingdom/Preview/Update Gate/Update Required")]
        private static void Required() => Open((int)UpdateGateVerdict.UpdateRequired);

        [MenuItem("Kingdom/Preview/Update Gate/Unreachable")]
        private static void Unreachable() => Open((int)UpdateGateVerdict.Unreachable);

        [MenuItem("Kingdom/Preview/Update Gate/Close")]
        private static void Close()
        {
            GameObject host = _host;
            _host = null;
            if (host == null) return;
            if (Application.isPlaying) Object.Destroy(host);
            else Object.DestroyImmediate(host);
        }

        private static void Open(int state)
        {
            if (!Application.isPlaying)
            {
                SessionState.SetInt(PendingKey, state);
                EditorApplication.EnterPlaymode();
                return;
            }
            Close();
            _host = UpdateGateOverlay.Preview(state == WaitingState ? null : (UpdateGateVerdict?)state);
        }

        private static void OpenWhenSettled()
        {
            if (!Application.isPlaying) { EditorApplication.update -= OpenWhenSettled; return; }
            if (EditorApplication.timeSinceStartup < _openAt) return;
            EditorApplication.update -= OpenWhenSettled;
            int state = SessionState.GetInt(PendingKey, None);
            SessionState.EraseInt(PendingKey);
            if (state != None) Open(state);
        }
    }
}
