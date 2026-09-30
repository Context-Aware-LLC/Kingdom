using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberfall.Core.Contracts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Review
{
    /// <summary>
    /// Optional real-bundle smoke check for the append/resume/legal-match lane. Runs against a
    /// private temporary copy, never Sam's original bundle or annotation sidecars.
    /// Set EMBERFALL_REVIEW_BUNDLE and invoke -executeMethod Emberfall.Review.NativeActionCaptureVerify.Run.
    /// </summary>
    public static class NativeActionCaptureVerify
    {
        public static void Run()
        {
            string source = Environment.GetEnvironmentVariable("EMBERFALL_REVIEW_BUNDLE");
            string scratch = Path.Combine(Path.GetTempPath(),
                "emberfall-native-review-" + Guid.NewGuid().ToString("N"));
            int exitCode = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                    throw new InvalidOperationException("Set EMBERFALL_REVIEW_BUNDLE to a review bundle.");

                string copied = Path.Combine(scratch, "bundle");
                Directory.CreateDirectory(copied);
                Directory.CreateDirectory(Path.Combine(copied, "cards"));
                File.Copy(Path.Combine(source, "bundle-manifest.json"),
                    Path.Combine(copied, "bundle-manifest.json"));
                File.Copy(Path.Combine(source, "frames.jsonl"), Path.Combine(copied, "frames.jsonl"));
                foreach (string file in Directory.GetFiles(Path.Combine(source, "cards")))
                    File.Copy(file, Path.Combine(copied, "cards", Path.GetFileName(file)));

                ReviewBundle bundle = ReviewBundle.Load(copied);
                ReviewFrame frame = null;
                foreach (ReviewFrame candidate in bundle.Frames)
                    foreach (ReviewAction action in candidate.Actions)
                        if ((string)action.Intent["$type"] == "end_turn") { frame = candidate; break; }
                if (frame == null) throw new InvalidOperationException("Bundle has no end-turn action.");

                var capture = NativeActionCapture.Open(bundle, "smoke");
                if (capture.Count != 0) throw new Exception("Temporary action sidecar is not empty.");
                var legal = new EndTurnIntent(new PlayerId(frame.Actor));
                if (!capture.TryRecord(frame, legal, out string error))
                    throw new Exception("Legal action rejected: " + error);
                if (capture.Count != 1 || !capture.IsCaptured(frame))
                    throw new Exception("Legal capture did not persist in memory.");
                if (capture.TryRecord(frame, new ConcedeIntent(new PlayerId(frame.Actor)), out _))
                    throw new Exception("Concede was accepted as a demonstration.");

                ReviewFrame mulliganFrame = null;
                JArray legalToss = null;
                foreach (ReviewFrame candidate in bundle.Frames)
                {
                    foreach (ReviewAction action in candidate.Actions)
                    {
                        if ((string)action.Intent["$type"] != "mulligan"
                            || !(action.Intent["toReplace"] is JArray cards) || cards.Count < 2) continue;
                        mulliganFrame = candidate;
                        legalToss = cards;
                        break;
                    }
                    if (mulliganFrame != null) break;
                }
                if (mulliganFrame == null) throw new Exception("Bundle has no multi-card mulligan action.");
                var reversedToss = new List<InstanceId>();
                for (int i = legalToss.Count - 1; i >= 0; i--)
                {
                    string wireId = (string)legalToss[i];
                    reversedToss.Add(new InstanceId(int.Parse(wireId.Substring(1),
                        CultureInfo.InvariantCulture)));
                }
                if (!capture.TryRecord(mulliganFrame,
                        new MulliganIntent(new PlayerId(mulliganFrame.Actor), reversedToss), out error))
                    throw new Exception("Mulligan card order incorrectly changed legality: " + error);
                if (capture.TryRecord(mulliganFrame,
                        new MulliganIntent(new PlayerId(mulliganFrame.Actor),
                            new[] { reversedToss[0], reversedToss[0] }), out _))
                    throw new Exception("Duplicate mulligan card ids were accepted.");

                capture = NativeActionCapture.Open(bundle, "smoke");
                if (capture.Count != 2 || !capture.IsCaptured(frame) || capture.WasSkipped(frame)
                    || !capture.IsCaptured(mulliganFrame))
                    throw new Exception("Legal capture did not resume from disk.");
                capture.Skip(frame);
                capture = NativeActionCapture.Open(bundle, "smoke");
                if (capture.Count != 2 || !capture.WasSkipped(frame))
                    throw new Exception("Latest-row skip did not resume from disk.");
                File.AppendAllText(capture.Path, "{\"torn\"");
                capture = NativeActionCapture.Open(bundle, "smoke");
                if (!capture.TryRecord(frame, legal, out error))
                    throw new Exception("Capture after a torn tail failed: " + error);
                capture = NativeActionCapture.Open(bundle, "smoke");
                if (capture.Count != 2 || capture.WasSkipped(frame))
                    throw new Exception("Latest row after a torn tail did not resume.");
                Debug.Log("[NativeActionCaptureVerify] PASS: legal match, mulligan set matching, concede refusal, append, torn-tail recovery and resume");
            }
            catch (Exception ex)
            {
                Debug.LogError("[NativeActionCaptureVerify] FAIL: " + ex);
                exitCode = 1;
            }
            finally
            {
                // This uniquely named directory was made by this invocation and contains only
                // copied fixture data and its own sidecar. Never clean the source bundle.
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(scratch);
                if (target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(target).StartsWith("emberfall-native-review-", StringComparison.Ordinal)
                    && Directory.Exists(target))
                {
                    try { Directory.Delete(target, true); }
                    catch (Exception ex)
                    {
                        Debug.LogError("[NativeActionCaptureVerify] Scratch cleanup failed: " + ex);
                        exitCode = 1;
                    }
                }
            }
            EditorApplication.Exit(exitCode);
        }
    }
}
