using System;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// The named B3 transport verify (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b3-networked-session-design.md §11.1 row 1 / §14 step 3;
    /// a5 §11 precedent, which caught a real gap). Opens REAL <see cref="WebSocketTransport"/>
    /// (ClientWebSocket) connections under Unity's Mono/IL2CPP runtime and plays a self-match to a
    /// decided outcome — the one thing pure-.NET `dotnet test` cannot prove about the client stack,
    /// because it runs the netstandard2.1 socket path inside the engine, not on the desktop CLR.
    ///
    /// Editor menu today (fast to run after every Unity upgrade). Promoting it into a player build —
    /// the doc's "from inside a player build" — is a follow-up; the socket path it exercises is the
    /// same one the player ships.
    /// </summary>
    public static class B3TransportVerify
    {
        private const string Live = "wss://emberfall.fly.dev/ws";

        [MenuItem("Emberfall/Verify B3 Transport (live)")]
        public static void VerifyAgainstLive() => _ = RunAsync(Live);

        private static async Task RunAsync(string url)
        {
            Debug.Log($"[B3Verify] connecting two ClientWebSockets to {url} …");
            // Two DISTINCT synthetic tokens (C-G1-8): the gate refuses a hello-less socket, and this
            // tool deliberately does not use the real device token — a verify run should not land in
            // the balance sample under this machine's identity.
            var c0 = new NetworkedMatchSession(new WebSocketTransport(),
                new ClientOptions { DeviceToken = "b3-verify-seat-0", Platform = "editor", BuildId = "b3-verify" });
            var c1 = new NetworkedMatchSession(new WebSocketTransport(),
                new ClientOptions { DeviceToken = "b3-verify-seat-1", Platform = "editor", BuildId = "b3-verify" });
            try
            {
                var uri = new Uri(url);
                await WithTimeout(Task.WhenAll(c0.ConnectAsync(uri), c1.ConnectAsync(uri)), 20, "connect");

                if (c0.State != ConnectionState.Live || c1.State != ConnectionState.Live)
                {
                    Debug.LogError($"[B3Verify] RED — did not reach Live (c0={c0.State}, c1={c1.State}).");
                    return;
                }

                Debug.Log($"[B3Verify] paired: seats ({c0.Seat},{c1.Seat}), v={c0.StateVersion}. Playing to a decided end …");

                var ended0 = WaitForEnded(c0);
                var ended1 = WaitForEnded(c1);
                await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
                await c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));
                _ = c1.SubmitAsync(new ConcedeIntent(c1.Player));
                await WithTimeout(Task.WhenAll(ended0, ended1), 30, "match end");

                bool clean = c0.ResyncCount == 0 && c1.ResyncCount == 0
                             && c0.Current.Outcome != MatchOutcome.InProgress;
                if (clean)
                    Debug.Log($"[B3Verify] GREEN — decided ({c0.Current.Outcome}) over real wss under Unity, " +
                              $"0 integrity failures. WebSocketTransport verified in-engine.");
                else
                    Debug.LogError($"[B3Verify] RED — outcome={c0.Current.Outcome}, " +
                                   $"resyncs=({c0.ResyncCount},{c1.ResyncCount}).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[B3Verify] RED — {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                c0.Dispose();
                c1.Dispose();
            }
        }

        /// <summary>Mono-safe timeout (Unity's runtime may lack Task.WaitAsync(TimeSpan), .NET 6+).</summary>
        private static async Task WithTimeout(Task work, int seconds, string what)
        {
            Task done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (done != work) throw new TimeoutException($"{what} timed out after {seconds}s");
            await work; // surface any fault
        }

        private static Task WaitForEnded(NetworkedMatchSession s)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.OnConnectionStateChanged += st => { if (st == ConnectionState.Ended) tcs.TrySetResult(true); };
            if (s.State == ConnectionState.Ended) tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
