using System;
using System.IO;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Telemetry;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The client half of the first-session funnel (pre-publication item 2; the row contract is
    /// <c>docs/dev-docs/specs/first-session-funnel.md</c>). Each static is one milestone; the call
    /// sites decide when it happened, <see cref="FunnelRecorder"/> decides what the row says, and
    /// <see cref="FunnelQueue"/> keeps it on disk until <c>POST /telemetry/client</c> takes it.
    ///
    /// <para><b>Self-bootstrapping</b>, like <see cref="CrashReportingScope"/>: no scene or prefab
    /// references it. A hidden object drives the uploader from its frame loop and hears
    /// <c>OnApplicationPause</c> and <c>OnApplicationQuit</c>.</para>
    ///
    /// <para><b>Every entry point swallows everything.</b> A milestone is a claim for counting, and
    /// no caller — the boot, the sign-in, the board — may fail because telemetry did.</para>
    ///
    /// <para><b>Main thread only.</b> The uploader creates <c>UnityWebRequest</c>s, which only the
    /// main thread may do; every call site here is a Unity callback or a continuation already on it.</para>
    /// </summary>
    public static class FirstSessionFunnel
    {
        /// <summary>Under <c>Application.persistentDataPath</c>: device-level, not inside any local
        /// account's scope, because a row belongs to the process that wrote it, not to a player.</summary>
        public const string QueueRelativePath = "telemetry/funnel-queue.jsonl";

        private static FunnelRecorder _recorder;
        private static FunnelUploader _uploader;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_recorder != null) return;
            try
            {
                var clock = new SystemClientClock();
                var stamp = new FunnelStamp(FunnelSession.Id, BuildStamp.ReleaseId, Platform);
                Action<string> log = message => Debug.Log(message);

                var queue = new FunnelQueue(
                    new FileFunnelQueueStore(Path.Combine(Application.persistentDataPath, QueueRelativePath)),
                    FunnelQueue.DefaultCapacity, log);
                // The same token accessor every other request uses: it refreshes a token near expiry
                // and answers empty when there is no session, which sends the device header alone.
                _uploader = new FunnelUploader(
                    queue, CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(),
                    DeviceIdentity.Token, AuthGateway.TokenForConnectAsync, clock, log);
                _recorder = new FunnelRecorder(queue, _uploader, clock, stamp);

                _recorder.Launch();
                // Whatever an earlier process left (its exit row, an unreachable gate) goes now.
                _uploader.RequestFlushNow();

                // Any other request that succeeds proves the network is up: a cheap moment to flush.
                UnityWebRequestAuthHttp.Answered += () => _uploader?.NotifyServerAnswered();

                var go = new GameObject("~FirstSessionFunnel");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Pump>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Funnel] could not start ({ex.GetType().Name}: {ex.Message}); " +
                                 "this process records no milestones.");
                _recorder = null;
                _uploader = null;
            }
        }

        /// <summary>The boot's update gate resolved. Skips a pass remembered from earlier in this
        /// process (<see cref="UpdateGateResult.Attempts"/> 0), which asked nobody.</summary>
        public static void Gate(UpdateGateResult result)
        {
            if (result != null && result.Attempts == 0) return;
            Safely(r => r.Gate(result));
        }

        /// <summary>The boot's update gate threw instead of answering.</summary>
        public static void GateError() => Safely(r => r.GateError());

        /// <summary>A sign-in completed, by <paramref name="cause"/> (the path that produced it).</summary>
        public static void SignedIn(string cause) => Safely(r => r.SignedIn(cause));

        /// <summary>A session restored from disk at boot stands in for a sign-in, once per process
        /// and only if nothing else recorded one — without it, a returning player's session reads
        /// as a <c>sign_in</c> drop.</summary>
        public static void SignedInFromStoredSession()
            => Safely(r => { if (!r.HasSignedIn) r.SignedIn("stored_session"); });

        public static void SignInFailed(string cause) => Safely(r => r.SignInFailed(cause));

        public static void SignInSkipped(string cause) => Safely(r => r.SignInSkipped(cause));

        /// <summary>The first snapshot of a match has been rendered.</summary>
        public static void BoardPlayable(string matchId) => Safely(r => r.BoardPlayable(matchId));

        /// <summary>The player asked to queue. <paramref name="source"/> is a
        /// <see cref="FunnelQueueSource"/> value.</summary>
        public static void Queue(string source) => Safely(r => r.Queue(source));

        private static string Platform =>
            Application.isEditor ? FunnelPlatform.Editor
            : Application.platform == RuntimePlatform.Android ? FunnelPlatform.Android
            : Application.platform == RuntimePlatform.IPhonePlayer ? FunnelPlatform.Ios
            : FunnelPlatform.Desktop;

        private static void Safely(Action<FunnelRecorder> record)
        {
            FunnelRecorder recorder = _recorder;
            if (recorder == null) return;
            try { record(recorder); }
            catch (Exception ex) { Debug.LogWarning($"[Funnel] {ex.GetType().Name}: {ex.Message}"); }
        }

        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                try { _uploader?.Tick(); }
                catch (Exception ex) { Debug.LogWarning($"[Funnel] {ex.GetType().Name}: {ex.Message}"); }
            }

            private void OnApplicationPause(bool paused)
            {
                if (paused)
                {
                    Safely(r => r.Pause());
                    // Best effort before the OS freezes the process; the row is on disk either way.
                    try { if (_uploader != null && !_uploader.IsBackingOff) _ = _uploader.FlushAsync(); }
                    catch (Exception) { }
                }
                else
                {
                    Safely(r => r.Resume());
                }
            }

            /// <summary>Written to disk synchronously and never sent from here: a request started
            /// now would be abandoned. The next launch flushes it under this session's id.</summary>
            private void OnApplicationQuit() => Safely(r => r.Exit());
        }
    }
}
