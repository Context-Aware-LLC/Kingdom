using System;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Headless Unity verification harness (EF-001). Aggregates the four local self-checks
    /// and exits non-zero on any compile error or check failure.
    ///
    /// <para>Run via: <c>Unity.exe -batchmode -nographics -quit -projectPath &lt;path&gt;
    /// -logFile &lt;file&gt; -executeMethod Emberfall.Editor.HeadlessVerify.RunAll</c></para>
    ///
    /// <para>B3TransportVerify is deliberately excluded — it opens live WebSocket connections and
    /// requires the droplet to be reachable.</para>
    /// </summary>
    public static class HeadlessVerify
    {
        [MenuItem("Emberfall/Headless Verify (all local checks)")]
        public static void RunAll()
        {
            var results = new[]
            {
                RunCheck("A5WireVerify",         A5WireVerify.Run),
                RunCheck("CardSetValidator",     CardSetValidator.RunAndLog),
                RunCheck("UxmlIntegrityCheck",   UxmlIntegrityCheck.Check),
                RunCheck("PlayerAccountSelfCheck", PlayerAccountTools.SelfCheck),
            };

            Debug.Log("[HeadlessVerify] ==============================");
            int failed = 0;
            foreach (var (name, passed) in results)
            {
                Debug.Log($"[HeadlessVerify] {(passed ? "PASS" : "FAIL")} {name}");
                if (!passed) failed++;
            }
            Debug.Log($"[HeadlessVerify] ============================== {(failed == 0 ? "ALL PASS" : failed + " FAIL")}");

            EditorApplication.Exit(failed > 0 ? 1 : 0);
        }

        private static (string Name, bool Passed) RunCheck(string name, Action check)
        {
            bool hadError = false;

            void Handler(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    hadError = true;
            }

            Application.logMessageReceived += Handler;
            try
            {
                check();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HeadlessVerify] {name} threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Application.logMessageReceived -= Handler;
            }

            return (name, !hadError);
        }
    }
}
