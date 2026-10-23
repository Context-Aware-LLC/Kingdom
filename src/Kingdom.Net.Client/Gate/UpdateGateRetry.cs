using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kingdom.Net.Client.Gate
{
    /// <summary>
    /// The launch gate's patience: up to <see cref="MaxAttempts"/> asks of <c>/update-gate</c>,
    /// with a growing pause between them, before the caller shows the player a failure.
    ///
    /// <para><b>Only <see cref="UpdateGateVerdict.Unreachable"/> is retried.</b> A pass is a pass,
    /// and <see cref="UpdateGateVerdict.UpdateRequired"/> is a real answer from a server that was
    /// reached: asking again cannot change it, and the player needs the store button now.</para>
    ///
    /// <para><b>The loop never uses <c>ConfigureAwait(false)</c>, on purpose.</b> Each attempt
    /// starts on the context that called <see cref="CheckAsync"/>; in Unity that is the main
    /// thread, and <c>UnityWebRequest</c> may only be created there. Resuming on a pool thread
    /// after the first backoff would make the second attempt throw.</para>
    /// </summary>
    public static class UpdateGateRetry
    {
        // Measured (Claude, 2026-10-23) against prod emberfall.fly.dev, GET /healthz:
        //   warm machine 0.72 s · wake from Fly suspend 1.43 s · cold start from stopped 6.68 s.
        // So the first attempt keeps the 15 s the gate always had: 2.2x a cold boot. That request
        // is what boots the machine, so by the second attempt it is warm or booting, and 10 s is
        // still 1.5x a full cold boot should the first never have reached Fly at all.
        //
        // The retries are for a flaky phone network, not a slow server: a phone with no route
        // fails in well under a second, so without the pauses three attempts would span nothing.
        // 3 s then 6 s lets a Wi-Fi-to-cellular handover finish between them.
        //
        // Worst case, every attempt timing out: 15 + 3 + 10 + 6 + 10 = 44 s (WorstCaseSeconds).
        private static readonly int[] TimeoutSeconds = { 15, 10, 10 };
        private static readonly int[] PauseMs = { 3000, 6000 };

        public static int MaxAttempts => TimeoutSeconds.Length;

        /// <summary>The per-request timeout for attempt <paramref name="attempt"/> (1-based).</summary>
        public static int TimeoutSecondsFor(int attempt)
            => TimeoutSeconds[Math.Max(0, Math.Min(attempt, MaxAttempts) - 1)];

        /// <summary>The pause after failed attempt <paramref name="attempt"/> (1-based), before
        /// the next one.</summary>
        public static int PauseMsAfter(int attempt)
            => PauseMs[Math.Max(0, Math.Min(attempt, PauseMs.Length) - 1)];

        /// <summary>Every attempt timing out and every pause served: the longest a player can wait
        /// before the failure state.</summary>
        public static int WorstCaseSeconds
        {
            get
            {
                int ms = 0;
                foreach (int s in TimeoutSeconds) ms += s * 1000;
                foreach (int p in PauseMs) ms += p;
                return (ms + 999) / 1000;
            }
        }

        /// <summary>Never throws, provided <paramref name="attempt"/> does not: every failure is a
        /// verdict. The result's <see cref="UpdateGateResult.Attempts"/> says how many asks it took.</summary>
        /// <param name="attempt">One ask of the gate: given the 1-based attempt number and that
        /// attempt's timeout in seconds. A caller showing progress reads the number here.</param>
        /// <param name="delay">The pause between attempts, in milliseconds, so a test can retry
        /// without sleeping. Defaults to <see cref="Task.Delay(int)"/>.</param>
        public static async Task<UpdateGateResult> CheckAsync(
            Func<int, int, Task<UpdateGateResult>> attempt,
            Func<int, Task>? delay = null)
        {
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            Func<int, Task> pause = delay ?? (ms => Task.Delay(ms));

            for (int n = 1; ; n++)
            {
                UpdateGateResult result = await attempt(n, TimeoutSecondsFor(n));
                if (result.Verdict != UpdateGateVerdict.Unreachable || n >= MaxAttempts)
                    return result.WithAttempts(n);
                await pause(PauseMsAfter(n));
            }
        }
    }
}
