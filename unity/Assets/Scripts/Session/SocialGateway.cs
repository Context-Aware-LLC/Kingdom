using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Social;
using Kingdom.Net.Protocol;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The game's one <see cref="SocialClient"/> (#409): the <c>/social</c> connection that makes
    /// this player online to their friends, and brings their friends' presence and challenges in.
    ///
    /// <para><b>It runs for the whole signed-in session, across every scene including the
    /// match</b> — the socket IS the presence, so closing it on a scene change would show the
    /// player offline to their friends mid-match. <see cref="EnsureStarted"/> is idempotent and
    /// cheap; call it from anything that only exists for a signed-in player.</para>
    ///
    /// <para><b>Everything it raises arrives on the main thread.</b> The client runs on the
    /// socket's own thread; a hidden <c>DontDestroyOnLoad</c> pump drains its frames in
    /// <c>Update</c>. The token is fetched on the main thread too, because a refresh goes through
    /// <c>UnityWebRequest</c>.</para>
    ///
    /// <para>Dropped on an account switch (<see cref="DeviceAccounts.Invalidate"/>): a connection
    /// that outlived one would keep the previous account online.</para>
    /// </summary>
    public static class SocialGateway
    {
        private static SocialClient _client;
        private static CancellationTokenSource _run;
        private static Pump _pump;

        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();

        /// <summary>A frame from the server, on the main thread. Presence is already applied.</summary>
        public static event Action<SocialMsg> Received;

        /// <summary>The connection's state changed, on the main thread.</summary>
        public static event Action<SocialState> StateChanged;

        public static SocialState State => _client?.State ?? SocialState.Idle;

        /// <summary>Offline unless a live connection has said otherwise.</summary>
        public static PresenceState PresenceOf(string code) => _client?.PresenceOf(code) ?? PresenceState.Offline;

        /// <summary>
        /// Start the connection if it is not already running. A client that stopped because nobody
        /// was signed in is started again; one stopped for any other reason is left stopped — above
        /// all one replaced by the same account on another device, which a restart here would
        /// evict in turn on every scene load.
        /// </summary>
        public static void EnsureStarted()
        {
            if (_client != null &&
                !(_client.State == SocialState.Stopped && _client.StopReason == SocialClient.NotSignedInReason))
                return;
            _run?.Cancel();

            if (_pump == null)
            {
                var go = new GameObject("SocialGateway") { hideFlags = HideFlags.HideInHierarchy };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _pump = go.AddComponent<Pump>();
            }

            var client = new SocialClient(SocialClient.UriFor(CollectionGateway.BaseUrl), TokenOnMainThread);
            // An event still in the queue when its client was replaced is dropped, not delivered as
            // if it came from the new one.
            client.Received += msg => MainThread.Enqueue(() =>
            {
                if (_client != client) return;
                ChatLog.OnSocial(msg);   // before listeners, so they read the settled log
                Received?.Invoke(msg);
            });
            client.StateChanged += state => MainThread.Enqueue(() =>
            {
                if (_client != client) return;
                if (state == SocialState.Stopped)
                    Debug.LogWarning($"[Social] connection stopped: {client.StopReason}.");
                if (state != SocialState.Live) ChatLog.FailAllPending();
                StateChanged?.Invoke(state);
            });

            var run = new CancellationTokenSource();
            _client = client;
            _run = run;
            Task.Run(() => client.RunAsync(run.Token));
        }

        /// <summary>A challenge, an accept, a close or a chat line. False when there is no live connection.</summary>
        public static Task<bool> SendAsync(SocialMsg msg) =>
            _client?.SendAsync(msg) ?? Task.FromResult(false);

        /// <summary>Close the connection and forget it, with this account's chat. Called from
        /// <see cref="DeviceAccounts.Invalidate"/>.</summary>
        public static void Invalidate()
        {
            _run?.Cancel();
            _run = null;
            _client = null;
            ChatLog.Clear();
        }

        private static Task<string> TokenOnMainThread(CancellationToken ct)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThread.Enqueue(async () =>
            {
                try { result.TrySetResult(await AuthGateway.TokenForConnectAsync()); }
                catch (Exception ex) { result.TrySetException(ex); }
            });
            ct.Register(() => result.TrySetCanceled());
            return result.Task;
        }

        /// <summary>Drains the queue on the main thread. Lives for the life of the app.</summary>
        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                while (MainThread.TryDequeue(out Action action))
                {
                    try { action(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }

            private void OnApplicationQuit() => Invalidate();
        }
    }
}
