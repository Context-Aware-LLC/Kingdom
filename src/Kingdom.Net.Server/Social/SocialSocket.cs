using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Transport;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Social
{
    /// <summary>
    /// The <c>/social</c> route's socket driver (#409): hello, then frames into
    /// <see cref="SocialHub"/> until the socket dies.
    ///
    /// <para><b>There is no guest path, and no fallback to one.</b> The first frame must be a
    /// <see cref="SocialHelloMsg"/> whose token verifies; anything else closes the socket. The
    /// account is resolved exactly as every HTTP route resolves it — verifier, then
    /// <c>CredentialHash.Of(identity.Subject)</c>, then <c>ResolveOrCreateAsync</c> — so this
    /// socket and <c>GET /friends</c> can never disagree about who a token is.</para>
    ///
    /// <para><b>A socket that goes quiet is closed</b> after <see cref="IdleTimeoutMs"/>. Presence
    /// is derived from this socket being open, so a half-dead connection nobody reaps is a friend
    /// shown online for ever; the client pings to stay.</para>
    /// </summary>
    public sealed class SocialSocket
    {
        /// <summary>Three missed client pings at the intended 30 s cadence.</summary>
        public const long IdleTimeoutMs = 90_000;

        private readonly SocialHub _hub;
        private readonly IIdentityTokenVerifier? _verifier;
        private readonly IAccountStore? _accounts;
        private readonly ServerOptions _opts;
        private readonly long _idleTimeoutMs;

        public SocialSocket(SocialHub hub, IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            ServerOptions opts, long idleTimeoutMs = IdleTimeoutMs)
        {
            _hub = hub;
            _verifier = verifier;
            _accounts = accounts;
            _opts = opts;
            _idleTimeoutMs = idleTimeoutMs;
        }

        public async Task RunAsync(WebSocket socket, CancellationToken ct)
        {
            var conn = new WebSocketConnection(socket);

            byte[]? first = await ReceiveOneAsync(socket, _opts.HelloTimeoutMs, ct).ConfigureAwait(false);
            if (first is null || !SocialCodec.TryDecode(first, out SocialMsg? decoded) ||
                decoded is not SocialHelloMsg hello)
            {
                await RefuseAsync(conn, 1008, "expected hello").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(hello.ClientContractVersion, _opts.WireContractVersion, StringComparison.Ordinal))
            {
                await RefuseAsync(conn, 1002, "contract mismatch").ConfigureAwait(false);
                return;
            }

            if (_verifier is null || _accounts is null)
            {
                await RefuseAsync(conn, 1008, "authentication not configured").ConfigureAwait(false);
                return;
            }

            var verified = await _verifier.VerifyAsync(hello.AuthToken, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
            {
                await RefuseAsync(conn, 1008, "token rejected").ConfigureAwait(false);
                return;
            }

            var identity = verified.Identity!;
            Account account = await _accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);
            string playerId = account.PlayerId;

            try
            {
                await _hub.ConnectAsync(playerId, conn, ct).ConfigureAwait(false);

                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    byte[]? frame = await ReceiveOneAsync(socket, _idleTimeoutMs, ct).ConfigureAwait(false);
                    if (frame is null) break;

                    // An undecodable frame ends the connection rather than being skipped: this
                    // protocol has no strike budget to spend, and a client that sends garbage once
                    // is not one worth keeping a presence slot for.
                    if (!SocialCodec.TryDecode(frame, out SocialMsg? msg) || msg is null)
                    {
                        conn.Close(1008, "bad frame");
                        break;
                    }

                    await _hub.HandleAsync(playerId, msg, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                // CancellationToken.None: the request token is usually already cancelled by the
                // time this runs, and the friends still have to be told this player left.
                await _hub.DisconnectAsync(playerId, conn, CancellationToken.None).ConfigureAwait(false);
                conn.Close(1000, "bye");
                await Task.WhenAny(conn.PumpTask, Task.Delay(2_000, CancellationToken.None)).ConfigureAwait(false);
                conn.Abort();
            }
        }

        private static async Task RefuseAsync(WebSocketConnection conn, int code, string reason)
        {
            conn.Close(code, reason);
            await conn.PumpTask.ConfigureAwait(false);
        }

        /// <summary>One whole message, or null on close, fault, timeout, or a message over
        /// <see cref="SocialCodec.MaxMessageBytes"/> — past the cap nothing more is buffered.</summary>
        private static async Task<byte[]?> ReceiveOneAsync(WebSocket socket, long timeoutMs, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            var buffer = new byte[SocialCodec.MaxMessageBytes];
            int total = 0;
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer, total, buffer.Length - total), deadline.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;

                    total += result.Count;
                    if (result.EndOfMessage)
                    {
                        var frame = new byte[total];
                        Array.Copy(buffer, frame, total);
                        return frame;
                    }
                    if (total == buffer.Length) return null;
                }
            }
            catch (Exception)
            {
                return null;   // aborted, faulted or timed out: all of them are a disconnect
            }
        }
    }

    /// <summary>Runs <see cref="SocialHub.SweepChallenges"/> once a second for the life of the host.</summary>
    public sealed class ChallengeSweeper : BackgroundService
    {
        private readonly SocialHub _hub;
        private readonly TimeSpan _interval;

        public ChallengeSweeper(SocialHub hub, TimeSpan? interval = null)
        {
            _hub = hub;
            _interval = interval ?? TimeSpan.FromSeconds(1);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_interval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        _hub.SweepChallenges();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[social] challenge sweep faulted: {0}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Host shutdown.
            }
        }
    }
}
