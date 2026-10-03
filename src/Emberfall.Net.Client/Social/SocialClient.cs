using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Client.Social
{
    /// <summary>Where the social connection is. Only <see cref="Live"/> means presence is known.</summary>
    public enum SocialState
    {
        /// <summary>Not started, or its run was cancelled.</summary>
        Idle,
        Connecting,
        /// <summary>Connected, and the server's first full presence list has arrived.</summary>
        Live,
        /// <summary>Lost, and waiting out a backoff before the next attempt.</summary>
        Retrying,
        /// <summary>Will not reconnect on its own — see <see cref="SocialClient.StopReason"/>.</summary>
        Stopped,
    }

    /// <summary>
    /// The client half of <c>/social</c> (#409): hello, ping, reconnect, and the friends' presence
    /// the server sends. Engine-free, so the same object runs in Unity and under test against the
    /// real server.
    ///
    /// <para><b>The socket IS the presence.</b> The server shows this player online exactly while
    /// this connection is open, so <see cref="RunAsync"/> is meant to run for the whole signed-in
    /// session — across scenes and through a match, where it is what makes a friend read
    /// "in a match" rather than "offline". It pings every <see cref="PingIntervalMs"/>, inside the
    /// server's 90 s idle reaper.</para>
    ///
    /// <para><b>Presence is only known while <see cref="SocialState.Live"/>.</b> On a drop it is
    /// cleared, not kept: a list from before the drop is a guess, and the game has no offline mode
    /// to show it in. The next connect brings a full list.</para>
    ///
    /// <para><b>Two closes end the loop instead of retrying it.</b> "replaced by a newer
    /// connection" is the same account signing in elsewhere — reconnecting would replace that one in
    /// turn, and the two would evict each other for ever. "contract mismatch" is this build being
    /// out of date, which no retry fixes. Everything else backs off and tries again.</para>
    ///
    /// <para><see cref="Received"/> and <see cref="StateChanged"/> are raised on the socket's own
    /// thread. A Unity caller marshals them to the main thread itself.</para>
    /// </summary>
    public sealed class SocialClient
    {
        /// <summary>A third of the server's idle timeout (<c>SocialSocket.IdleTimeoutMs</c>).</summary>
        public const int PingIntervalMs = 30_000;

        /// <summary>The server closes with these; neither is fixed by trying again.</summary>
        public const string ReplacedReason = "replaced by a newer connection";
        public const string ContractMismatchReason = "contract mismatch";

        /// <summary>The client's own reason: the token source answered empty.</summary>
        public const string NotSignedInReason = "not signed in";

        private static readonly int[] BackoffMs = { 1_000, 2_000, 5_000, 10_000, 30_000 };

        private readonly Uri _uri;
        private readonly Func<CancellationToken, Task<string>> _token;
        private readonly Func<IWireTransport> _transports;
        private readonly Func<int, CancellationToken, Task> _delay;
        private readonly string _contractVersion;
        private readonly int _pingIntervalMs;

        private readonly object _lock = new object();
        private readonly Dictionary<string, PresenceState> _presence =
            new Dictionary<string, PresenceState>(StringComparer.Ordinal);

        private volatile IWireTransport? _live;
        private volatile SocialState _state = SocialState.Idle;

        /// <summary>The server's message, or the client's own reason, for <see cref="SocialState.Stopped"/>.</summary>
        public string StopReason { get; private set; } = "";

        public SocialState State => _state;

        /// <summary>Every frame from the server except pongs, after presence has been applied.</summary>
        public event Action<SocialMsg>? Received;

        public event Action<SocialState>? StateChanged;

        /// <param name="uri">The <c>/social</c> route — see <see cref="UriFor"/>.</param>
        /// <param name="token">A fresh access token per attempt; empty means nobody is signed in,
        /// and stops the loop. Asked again on every reconnect because a Supabase token lives an
        /// hour and this connection outlives it.</param>
        /// <param name="transports">A new transport per attempt. Defaults to
        /// <see cref="WebSocketTransport"/> capped at the social frame size.</param>
        /// <param name="delay">The reconnect backoff, so a test can retry without sleeping.</param>
        public SocialClient(Uri uri, Func<CancellationToken, Task<string>> token,
            Func<IWireTransport>? transports = null, Func<int, CancellationToken, Task>? delay = null,
            string contractVersion = ProtocolContract.Version, int pingIntervalMs = PingIntervalMs)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _transports = transports ?? (() => new WebSocketTransport(SocialCodec.MaxMessageBytes));
            _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
            _contractVersion = contractVersion;
            _pingIntervalMs = pingIntervalMs;
        }

        /// <summary>The <c>/social</c> route on the same host as the HTTP routes:
        /// <c>https://host</c> → <c>wss://host/social</c>.</summary>
        public static Uri UriFor(string baseUrl)
        {
            var http = new Uri(baseUrl);
            var ws = new UriBuilder(http)
            {
                Scheme = http.Scheme == "https" ? "wss" : "ws",
                Path = http.AbsolutePath.TrimEnd('/') + "/social",
            };
            return ws.Uri;
        }

        /// <summary>Offline unless a live connection has said otherwise.</summary>
        public PresenceState PresenceOf(string code)
        {
            lock (_lock)
                return _presence.TryGetValue(code, out PresenceState state) ? state : PresenceState.Offline;
        }

        /// <summary>False when there is no live connection, or the send failed. Nothing is queued:
        /// a challenge sent into a dead socket should fail where the player can see it.</summary>
        public async Task<bool> SendAsync(SocialMsg msg, CancellationToken ct = default)
        {
            IWireTransport? live = _live;
            if (live is null || _state != SocialState.Live) return false;
            try
            {
                await live.SendAsync(SocialCodec.Encode(msg), ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Connect, and keep reconnecting, until <paramref name="ct"/> is cancelled or
        /// the loop is told to stop. Returns when it ends; never throws for a network reason.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                string token;
                try { token = await _token(ct).ConfigureAwait(false) ?? ""; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception) { token = ""; }

                if (token.Length == 0)
                {
                    Stop(NotSignedInReason);
                    return;
                }

                SetState(SocialState.Connecting);
                (bool wasLive, string? closeDetail) = await ConnectOnceAsync(token, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (closeDetail == ReplacedReason || closeDetail == ContractMismatchReason)
                {
                    Stop(closeDetail);
                    return;
                }

                if (wasLive) failures = 0;
                SetState(SocialState.Retrying);
                try
                {
                    await _delay(BackoffMs[Math.Min(failures, BackoffMs.Length - 1)], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                failures++;
            }
            SetState(SocialState.Idle);
        }

        /// <summary>One connection, from handshake to close. Whether it ever went live, and the
        /// text of the server's close frame if it sent one.</summary>
        private async Task<(bool WasLive, string? CloseDetail)> ConnectOnceAsync(string token, CancellationToken ct)
        {
            IWireTransport transport = _transports();
            string? closeDetail = null;
            transport.OnClosed += (_, detail) => { if (detail != null) closeDetail = detail; };

            bool wasLive = false;
            using var pings = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // A blocked read is not guaranteed to observe its token (an SslStream under Mono need
            // not), so a cancel also closes the socket underneath it.
            using CancellationTokenRegistration onCancel =
                ct.Register(() => _ = transport.CloseAsync(WireCloseReason.Local));
            try
            {
                await transport.ConnectAsync(_uri, ct).ConfigureAwait(false);
                await transport.SendAsync(SocialCodec.Encode(new SocialHelloMsg(token, _contractVersion)), ct)
                    .ConfigureAwait(false);
                _live = transport;
                _ = PingLoopAsync(transport, pings.Token);

                while (true)
                {
                    byte[]? frame = await transport.ReceiveAsync(ct).ConfigureAwait(false);
                    if (frame is null) break;

                    // A frame this build cannot read is skipped, not fatal: it is most likely a
                    // newer server's type, and the server already holds the contract version.
                    if (!SocialCodec.TryDecode(frame, out SocialMsg? msg) || msg is null) continue;
                    if (msg is SocialPongMsg) continue;

                    if (msg is PresenceMsg presence)
                    {
                        Apply(presence);
                        if (!wasLive)
                        {
                            wasLive = true;
                            SetState(SocialState.Live);
                        }
                    }
                    Raise(msg);
                }
            }
            catch (Exception)
            {
                // Refused, faulted or cancelled: all of them are this connection ending.
            }
            finally
            {
                pings.Cancel();
                _live = null;
                ClearPresence();
                try { await transport.CloseAsync(WireCloseReason.Local).ConfigureAwait(false); }
                catch (Exception) { /* already gone */ }
            }
            return (wasLive, closeDetail);
        }

        private async Task PingLoopAsync(IWireTransport transport, CancellationToken ct)
        {
            byte[] ping = SocialCodec.Encode(new SocialPingMsg());
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_pingIntervalMs, ct).ConfigureAwait(false);
                    await transport.SendAsync(ping, ct).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failed ping is the receive loop's to notice; it ends when the socket does.
            }
        }

        private void Apply(PresenceMsg msg)
        {
            lock (_lock)
            {
                if (msg.Full) _presence.Clear();
                foreach (FriendPresence friend in msg.Friends)
                {
                    if (friend.State == PresenceState.Offline) _presence.Remove(friend.Code);
                    else _presence[friend.Code] = friend.State;
                }
            }
        }

        private void ClearPresence()
        {
            lock (_lock) _presence.Clear();
        }

        private void Stop(string reason)
        {
            StopReason = reason;
            SetState(SocialState.Stopped);
        }

        private void SetState(SocialState state)
        {
            if (_state == state) return;
            _state = state;
            try { StateChanged?.Invoke(state); }
            catch (Exception) { /* a listener's fault is not the connection's */ }
        }

        private void Raise(SocialMsg msg)
        {
            try { Received?.Invoke(msg); }
            catch (Exception) { /* a listener's fault is not the connection's */ }
        }
    }
}
