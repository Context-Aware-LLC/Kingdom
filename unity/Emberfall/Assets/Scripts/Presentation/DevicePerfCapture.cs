using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-064 - on-device frame capture. Samples every frame into memory, then writes one CSV
    /// plus a percentile summary to <c>Application.persistentDataPath</c> for <c>adb pull</c>.
    ///
    /// SELF-BOOTSTRAPPING BY DESIGN. This attaches itself via
    /// <see cref="RuntimeInitializeOnLoadMethod"/> and is referenced by no scene, no prefab and
    /// no other script. That is deliberate: a capture harness that requires a scene edit cannot
    /// be landed by anyone who does not hold the Unity Editor seat, and the Editor is a
    /// single-writer resource here. Nothing needs to be wired for this to run.
    ///
    /// WHAT IT CANNOT MEASURE, AND WHY THAT MATTERS
    /// --------------------------------------------
    /// <b>Draw calls, batches and SetPass calls are not readable from script in a player
    /// build.</b> <c>UnityStats</c> is Editor-only, so no on-device script can report them --
    /// including this one. Do not add a field here that pretends otherwise. The two real routes
    /// to those numbers are the Unity Profiler attached over adb, and Android GPU Inspector;
    /// the capture script documents both. The static proxy for draw-call pressure is
    /// <c>tooling/scripts/card_renderer_census.py</c>, which counts the renderer components a
    /// card builds (18 at time of writing) without needing a device at all.
    ///
    /// GPU time requires <b>Frame Timing Stats</b> enabled in Player Settings. When it is off,
    /// <see cref="FrameTimingManager"/> yields no timings and the GPU columns read -1 rather
    /// than 0 -- an absent measurement and a measurement of zero are not the same thing, and a
    /// zero here would silently read as "the GPU is free".
    ///
    /// PERCENTILES, NOT MEANS. The summary reports p50/p95/p99. A mean frame time hides exactly
    /// the stutter a player notices; on a 60 Hz phone the p99 is the number that decides whether
    /// the board feels smooth.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DevicePerfCapture : MonoBehaviour
    {
        // Marker file lets a release APK be switched into capture mode without a rebuild:
        //   adb shell "run-as <pkg> touch files/perfcapture.on"
        private const string MarkerFileName = "perfcapture.on";
        private const string LogTag = "[EF-064-PERF]";

        // Ring capacity. 60 fps * 300 s = 18000; a five-minute capture is far longer than any
        // hand-driven session, and the buffer is pre-allocated so sampling never allocates.
        private const int MaxSamples = 18000;

        private double[] _frameMs;
        private double[] _cpuMs;
        private double[] _gpuMs;
        private long[] _monoBytes;
        private int _count;
        private int _startGc0;
        private float _startTime;
        private bool _written;

        // Hoisted out of Update. A profiler that allocates once per frame perturbs the GC
        // figure it exists to report -- this array is the measurement instrument, so it must
        // not appear in the measurement.
        private readonly FrameTiming[] _timingScratch = new FrameTiming[1];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ShouldCapture()) return;

            var go = new GameObject("~EF064DevicePerfCapture");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<DevicePerfCapture>();
        }

        private static bool ShouldCapture()
        {
            // A development build is always capturing; a release build opts in via the marker.
            if (Debug.isDebugBuild) return true;
            try
            {
                return File.Exists(Path.Combine(Application.persistentDataPath, MarkerFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Awake()
        {
            _frameMs = new double[MaxSamples];
            _cpuMs = new double[MaxSamples];
            _gpuMs = new double[MaxSamples];
            _monoBytes = new long[MaxSamples];
            _startGc0 = GC.CollectionCount(0);
            _startTime = Time.realtimeSinceStartup;

            // Logged so the capture script can recover the output path off logcat without
            // having to guess whether persistentDataPath resolved to internal or external
            // storage -- that depends on a Player Setting and is not knowable from the host.
            Debug.Log($"{LogTag} capture armed. output dir = {Application.persistentDataPath}");
            Debug.Log($"{LogTag} device = {SystemInfo.deviceModel} / {SystemInfo.operatingSystem} / " +
                      $"gpu = {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
        }

        private void Update()
        {
            if (_count >= MaxSamples) return;

            _frameMs[_count] = Time.unscaledDeltaTime * 1000.0;

            double cpu = -1.0, gpu = -1.0;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timingScratch) > 0)
            {
                cpu = _timingScratch[0].cpuFrameTime;
                gpu = _timingScratch[0].gpuFrameTime;
            }
            _cpuMs[_count] = cpu;
            _gpuMs[_count] = gpu;
            _monoBytes[_count] = Profiler.GetMonoUsedSizeLong();
            _count++;
        }

        private void OnApplicationPause(bool paused)
        {
            // Android delivers pause on backgrounding; that is the reliable moment to flush,
            // because OnApplicationQuit is not guaranteed to run when the OS kills the process.
            if (paused) Flush();
        }

        private void OnApplicationQuit() => Flush();

        /// <summary>Writes the capture. Safe to call twice; the second call is a no-op.</summary>
        public void Flush()
        {
            if (_written || _count == 0) return;
            _written = true;

            string dir = Application.persistentDataPath;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string csvPath = Path.Combine(dir, $"perf-{stamp}.csv");
            string sumPath = Path.Combine(dir, $"perf-{stamp}-summary.txt");

            try
            {
                var csv = new StringBuilder(_count * 48);
                csv.AppendLine("frame,frame_ms,cpu_ms,gpu_ms,mono_bytes");
                for (int i = 0; i < _count; i++)
                {
                    csv.Append(i).Append(',')
                       .Append(_frameMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(_cpuMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(_gpuMs[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(_monoBytes[i])
                       .Append('\n');
                }
                // Built once. Calling BuildSummary twice would re-read realtimeSinceStartup and
                // print an elapsed/fps pair that disagrees with the file it claims to summarise.
                string summary = BuildSummary();
                File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
                File.WriteAllText(sumPath, summary, Encoding.UTF8);

                Debug.Log($"{LogTag} wrote {_count} samples -> {csvPath}");
                Debug.Log($"{LogTag} summary -> {sumPath}");
                foreach (string line in summary.Split('\n'))
                    if (line.Length > 0) Debug.Log($"{LogTag} {line}");
                Debug.Log($"{LogTag} CAPTURE-COMPLETE");
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogTag} write failed: {e}");
            }
        }

        private string BuildSummary()
        {
            var frames = new double[_count];
            Array.Copy(_frameMs, frames, _count);
            Array.Sort(frames);

            double elapsed = Time.realtimeSinceStartup - _startTime;
            int gc0 = GC.CollectionCount(0) - _startGc0;

            var sb = new StringBuilder();
            sb.Append("EF-064 device perf capture\n");
            sb.Append($"device      : {SystemInfo.deviceModel}\n");
            sb.Append($"os          : {SystemInfo.operatingSystem}\n");
            sb.Append($"gpu         : {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})\n");
            sb.Append($"memory      : {SystemInfo.systemMemorySize} MB system / {SystemInfo.graphicsMemorySize} MB gfx\n");
            sb.Append($"screen      : {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F1} Hz\n");
            sb.Append($"targetFps   : {Application.targetFrameRate} (vSync {QualitySettings.vSyncCount})\n");
            sb.Append($"samples     : {_count} over {elapsed:F1} s\n");
            sb.Append($"mean fps    : {(_count / Math.Max(elapsed, 0.001)):F1}\n");
            sb.Append($"frame p50   : {Pct(frames, 50):F2} ms\n");
            sb.Append($"frame p95   : {Pct(frames, 95):F2} ms\n");
            sb.Append($"frame p99   : {Pct(frames, 99):F2} ms\n");
            sb.Append($"frame max   : {frames[_count - 1]:F2} ms\n");
            sb.Append($"gc gen0     : {gc0} collections\n");

            // Only frames that actually carried a GPU timing may enter the percentile. The
            // absent ones are stored as -1, and leaving them in would sort to the bottom of
            // the array and drag p95 DOWN -- reporting the GPU as faster than it is, which is
            // the one direction of error a perf capture must never make.
            double[] gpuValid = ValidOnly(_gpuMs, _count);
            sb.Append(gpuValid.Length > 0
                ? $"gpu p95     : {Pct(gpuValid, 95):F2} ms  (from {gpuValid.Length}/{_count} frames)\n"
                : "gpu         : UNAVAILABLE - enable Frame Timing Stats in Player Settings\n");

            sb.Append("draw calls  : NOT MEASURABLE from script in a player build; ");
            sb.Append("use Unity Profiler over adb or Android GPU Inspector\n");
            return sb.ToString();
        }

        /// <summary>Sorted ascending copy of the non-sentinel (>= 0) entries in the first
        /// <paramref name="count"/> elements.</summary>
        private static double[] ValidOnly(double[] src, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if (src[i] >= 0.0) n++;
            var dst = new double[n];
            int j = 0;
            for (int i = 0; i < count; i++) if (src[i] >= 0.0) dst[j++] = src[i];
            Array.Sort(dst);
            return dst;
        }

        // Nearest-rank percentile on an already-sorted ascending array.
        private static double Pct(double[] sorted, int p)
        {
            if (sorted.Length == 0) return -1.0;
            int rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
            if (rank < 0) rank = 0;
            if (rank >= sorted.Length) rank = sorted.Length - 1;
            return sorted[rank];
        }
    }
}
