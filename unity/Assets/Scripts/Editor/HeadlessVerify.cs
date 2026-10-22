using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Headless Unity verification harness (EF-001). Aggregates the local self-checks
    /// and exits non-zero on any compile error or check failure.
    ///
    /// <para>Run via: <c>Unity.exe -batchmode -nographics -quit -projectPath &lt;path&gt;
    /// -logFile &lt;file&gt; -executeMethod Kingdom.Editor.HeadlessVerify.RunAll</c></para>
    ///
    /// <para>B3TransportVerify is deliberately excluded — it opens live WebSocket connections and
    /// requires the droplet to be reachable.</para>
    /// </summary>
    public static class HeadlessVerify
    {
        [MenuItem("Kingdom/Verify/All Local Checks")]
        public static void RunAll()
        {
            // unity-verify.ps1 refuses a locked project and advances every project-owned compilation
            // input timestamp before starting a fresh batch Editor. Unity's startup refresh therefore
            // recompiles scripts before invoking -executeMethod; a compile error prevents this method
            // from running and is detected from the Editor log by the wrapper.
            // Do not call AssetDatabase.Refresh here: in -nographics mode it reserializes unrelated source
            // assets (including TMP font atlases). These checks are synchronous and do not use scaled time.
            var results = new[]
            {
                RunCheck("A5WireVerify",         A5WireVerify.Run),
                RunCheck("CardSetValidator",     CardSetValidator.RunAndLog),
                RunCheck("UxmlIntegrityCheck",   UxmlIntegrityCheck.Check),
                RunCheck("CharacterRosterVerify", CharacterRosterVerify.Check),
                RunCheck("PlayerAccountSelfCheck", PlayerAccountTools.SelfCheck),
                RunCheck("AddressablesGuard",    AddressablesGuard.Check),
                RunCheck("DeviceLayoutVerify",   DeviceLayoutVerify.Check),
                RunCheck("InputGestureVerify",   InputGestureVerify.Run),
                RunCheck("CollectionDeckDragVerify", CollectionDeckDragVerify.Run),
                RunCheck("CardRevealVerify",     CardRevealVerify.Run),
                RunCheck("CardMaterialBinderVerify", CardMaterialBinderVerify.Run),
                RunCheck("CardBackOwnershipVerify", CardBackOwnershipVerify.Run),
                RunCheck("LocalizationVerify",   LocalizationVerify.Run),
                RunCheck("HeraldVoiceVerify",     HeraldVoiceVerify.Run),
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
            var capturedErrors = new List<string>();

            void Handler(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    hadError = true;
                    capturedErrors.Add($"{type}: {message}");
                }
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

            foreach (string captured in capturedErrors)
                Debug.LogWarning($"[HeadlessVerify] {name} captured {captured}");

            return (name, !hadError);
        }
    }

}
