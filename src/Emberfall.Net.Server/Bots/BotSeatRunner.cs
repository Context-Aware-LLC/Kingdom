using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Loopback;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Transport;

namespace Emberfall.Net.Server.Bots
{
    /// <summary>
    /// Bridges one bot seat's decisions into a real match's <see cref="SessionPump"/>, over the
    /// production <see cref="LoopbackTransport"/> — no socket, no second wire codec (design doc §The
    /// bot seat). A real <see cref="NetworkedMatchSession"/>, the SAME class a human client uses,
    /// sits on the other end of the loopback and is driven by <see cref="GreedyAi"/>.
    ///
    /// <para><b>Two-phase construction.</b> The <see cref="MatchSession"/>/<see cref="SessionPump"/>
    /// this seat will post into does not exist until AFTER <c>MatchGate.StartMatch</c> has already
    /// been handed this object as an <see cref="IServerConnection"/> — so the constructor only builds
    /// the transport (also required by the lock <c>BotPool.TryAcquire</c> constructs this inside —
    /// see that type's docs), and <see cref="Bind"/> (called once, right after <c>StartMatch</c>
    /// returns) starts the outbound drain loop and the AI. Nothing is lost by the gap: server→client
    /// frames enqueued before <see cref="Bind"/> (the match_start) simply sit in the transport's
    /// inbound queue until the client-side pump starts reading.</para>
    /// </summary>
    public sealed class BotSeatRunner : IServerConnection
    {
        private readonly LoopbackTransport _transport = new LoopbackTransport();
        private readonly ClientOptions _clientOptions;
        private readonly ICommandValidator _validator;
        private readonly ICardDatabase _db;
        private SessionPump? _pump;
        private volatile bool _closed;
        // Never disposed: Cancel() alone is documented thread-safe/idempotent (safe even if Close()
        // is invoked more than once — MatchSession has several call sites, e.g. reconnect supersede
        // + match-over), but Dispose() racing a concurrent read of .Token (RunAiAsync, between Bind()
        // starting it and it reaching the ConnectAsync line) is not. A cancelled-but-undisposed CTS
        // is a normal, bounded leak for an object with the seat's own lifetime — BotSeatRunner itself
        // isn't IDisposable either.
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <param name="botUsername">The roster's account username — the bot's stable identity, and
        /// what the synthetic device token is keyed on. Never rendered.</param>
        /// <param name="botDisplayName">What a human opponent sees on the nameplate. Free to change
        /// without disturbing the token above, which is the whole reason these are two parameters
        /// and not one (see <see cref="BotRosterEntry"/>).</param>
        public BotSeatRunner(string botUsername, string botDisplayName, ICommandValidator validator, ICardDatabase db)
        {
            _clientOptions = new ClientOptions
            {
                DeviceToken = "bot-internal-" + botUsername,
                DisplayName = botDisplayName,
            };
            _validator = validator;
            _db = db;
        }

        // ── IServerConnection: server → bot ──────────────────────────────────────

        public bool TryEnqueue(byte[] frame)
        {
            if (_closed) return false;
            _transport.EnqueueInbound(frame);
            return true;
        }

        public void Close(int closeCode, string reason)
        {
            _closed = true;
            // ServerClose() wakes an active receive immediately when there are no buffered frames,
            // which NetworkedMatchSession routes to OnPeerClosed. That resolves a still-pending
            // ConnectAsync's internal match-start wait with a fault, so a Close() that lands before
            // Bind()/ConnectAsync's match_start ever arrives no longer hangs RunAiAsync forever.
            // _cts.Cancel() is the secondary/defensive path — harmless and idempotent even if
            // ConnectAsync (or a future transport whose ConnectAsync/SendAsync actually observes
            // the token, e.g. a real socket) is still mid-flight.
            _transport.ServerClose();
            _cts.Cancel();
        }

        // ── Bind: bot → server, and the AI itself ────────────────────────────────

        /// <summary>Wires this seat's outbound direction into the real match and starts the AI.
        /// Call exactly once, after the match has actually started for this seat.</summary>
        public void Bind(SessionPump pump)
        {
            _pump = pump;
            _ = Task.Run(DrainOutboundLoopAsync);
            _ = Task.Run(RunAiAsync);
        }

        private async Task DrainOutboundLoopAsync()
        {
            while (!_closed)
            {
                if (_transport.TryDequeueOutbound(out byte[] frame))
                {
                    var decoded = ProtocolCodec.Decode(frame);
                    // NetworkedMatchSession.ConnectAsync always opens with a HelloMsg — this bot's
                    // identity and deck are already known from the pool, so there is no handshake to
                    // complete. MatchSession has no Hello handler and would strike an unrecognized
                    // frame, so it is discarded here rather than routed (mirrors
                    // LoopbackServerPeer.DrainOutbound in the test tree, RC-F9's own precedent).
                    if (decoded.Ok && decoded.Message is HelloMsg) continue;
                    _pump!.Post(new SessionCommand.Frame(this, frame));
                }
                else
                {
                    await Task.Delay(5).ConfigureAwait(false);
                }
            }
        }

        private async Task RunAiAsync()
        {
            using var client = new NetworkedMatchSession(_transport, _clientOptions);
            try
            {
                // The Close()/Bind() race (Close lands before the match_start frame ever reaches the
                // transport) is unblocked by NetworkedMatchSession.OnPeerClosed itself faulting the
                // pending connect — see that method. _cts.Token is still passed through so a transport
                // whose ConnectAsync/SendAsync actually observes cancellation (a real socket, unlike
                // LoopbackTransport) gets it too; either path lands here as a caught exception.
                await client.ConnectAsync(new Uri("loopback://bot"), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[bot] failed to connect: {0}", ex.Message);
                return;
            }

            ulong lastActed = ulong.MaxValue;
            using var wake = new SemaphoreSlim(0);
            void OnSnap(GameState _) => wake.Release();
            client.OnSnapshot += OnSnap;
            try
            {
                while (!_closed)
                {
                    GameState state = client.Current;
                    if (state.Outcome != MatchOutcome.InProgress || client.State != ConnectionState.Live)
                        break;
                    if (lastActed != ulong.MaxValue && state.StateVersion <= lastActed)
                    {
                        await wake.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        continue;
                    }

                    PlayerIntent? intent = GreedyAi.Choose(state, client.Player, _validator, _db);
                    lastActed = state.StateVersion;
                    if (intent is null) continue;

                    // Human-like pacing (design doc): the bot never acts with inhuman instancy.
                    // System.Random is banned by ServerFirewallTests (determinism); the CSPRNG is
                    // pacing jitter only, matching MatchGate.MintSeed's own convention.
                    await Task.Delay(RandomNumberGenerator.GetInt32(500, 2000)).ConfigureAwait(false);
                    CommandResult result = await client.SubmitAsync(intent).ConfigureAwait(false);
                    // lastActed is already bumped to this StateVersion above, so a rejected intent
                    // would otherwise idle silently for the rest of the turn (no retry, no log) —
                    // this at least makes the reject observable.
                    if (!result.Accepted)
                    {
                        Console.Error.WriteLine("[bot] intent rejected: {0}", result.Reason);
                    }
                }
            }
            finally
            {
                client.OnSnapshot -= OnSnap;
            }
        }
    }
}
