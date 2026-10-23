#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using SentrySdk = Sentry.Unity.SentrySdk;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// DEV ONLY — compiled out of release builds. Fires a deliberate managed exception or a deliberate
    /// native crash on a development build, so pre-publication item 1's done-condition (both appear in
    /// Sentry, symbolicated, with the full tag set) can be exercised on a device.
    ///
    /// <para><b>Armed by a marker file</b>, the same pattern as <c>DevicePerfCapture</c>, so no UI and
    /// no rebuild:</para>
    /// <code>
    /// adb shell touch /sdcard/Android/data/com.contextaware.kobi/files/sentry-test-managed
    /// adb shell touch /sdcard/Android/data/com.contextaware.kobi/files/sentry-test-native
    /// </code>
    /// <para>then relaunch the app. The marker is deleted BEFORE firing, so a crash cannot loop. Each
    /// fires <see cref="DelaySeconds"/> after boot, once the scope's tags are set; sign in first (a
    /// previous launch is enough) to see <c>account_id</c>. A native crash is uploaded on the NEXT
    /// launch — open the app once more after it dies.</para>
    ///
    /// <para><b>Never in the Editor</b>: it compiles there so the compile gate checks it, but a native
    /// crash would take the Editor down, and the Editor does not report to Sentry anyway.</para>
    /// </summary>
    public sealed class CrashReportingDevTrigger : MonoBehaviour
    {
        public const string ManagedMarker = "sentry-test-managed";
        public const string NativeMarker = "sentry-test-native";
        private const float DelaySeconds = 5f;
        private const string LogTag = "[CrashReportingDevTrigger]";

        private bool _native;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.isEditor) return;

            bool managed = TakeMarker(ManagedMarker);
            bool native = TakeMarker(NativeMarker);
            if (!managed && !native) return;

            if (!SentrySdk.IsEnabled)
            {
                Debug.LogWarning($"{LogTag} a test marker was found but Sentry is disabled (empty DSN in " +
                                 "CrashReportingProject); nothing fired.");
                return;
            }

            var go = new GameObject("~CrashReportingDevTrigger");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            var trigger = go.AddComponent<CrashReportingDevTrigger>();
            trigger._native = native;
            // Managed first: it only logs, so a native marker in the same launch still fires after it.
            if (managed) trigger.Invoke(nameof(FireManaged), DelaySeconds);
            if (native) trigger.Invoke(nameof(FireNative), DelaySeconds + (managed ? 3f : 0f));
        }

        private void FireManaged()
        {
            Debug.Log($"{LogTag} throwing a deliberate managed exception.");
            throw new InvalidOperationException("Deliberate managed exception from CrashReportingDevTrigger " +
                                                "(pre-publication item 1 test).");
        }

        private void FireNative()
        {
            if (!_native) return;
            Debug.Log($"{LogTag} forcing a deliberate native crash (access violation). Relaunch to upload it.");
            SentrySdk.Flush(TimeSpan.FromSeconds(2));
            UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.AccessViolation);
        }

        private static bool TakeMarker(string name)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, name);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogTag} could not read or clear marker '{name}': {ex.Message}");
                return false;
            }
        }
    }
}
#endif
