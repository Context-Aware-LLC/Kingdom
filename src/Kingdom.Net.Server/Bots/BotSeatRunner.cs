using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Policy;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Loopback;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Transport;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>
    /// Bridges one bot seat's decisions into a real match's <see cref="SessionPump"/>, over the
    /// production <see cref="LoopbackTransport"/> — no socket, no second wire codec (design doc §The
    /// bot seat). A real <see cref="NetworkedMatchSession"/>, the SAME class a human client uses,
    /// sits on the other end of the loopback and is driven by an <see cref="ISeatPolicy"/>.
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
        private readonly ISeatPolicy _policy;
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
        /// what the (unread) synthetic device token is keyed on. Never rendered.</param>
        /// <param name="botDisplayName">What a human opponent sees on the nameplate. Free to change
        /// without disturbing the token above, which is the whole reason these are two parameters
        /// and not one (see <see cref="BotRosterEntry"/>).</param>
        public BotSeatRunner(string botUsername, string botDisplayName, ICommandValidator validator, ICardDatabase db)
            : this(botUsername, botDisplayName, new GreedySeatPolicy(validator, db))
        {
        }

        /// <param name="policy">The match-local policy for this seat. A checkpoint policy carries
        /// player memory and seeded sampling state, so it must not be shared between seats.</param>
        public BotSeatRunner(string botUsername, string botDisplayName, ISeatPolicy policy)
        {
            // A bot never goes through MatchGate's hello: MatchGate's bot fill seats it straight into StartMatch
            // under its provisioned bot account (BotAccountProvisioner), and DrainOutboundLoopAsync
            // drops the HelloMsg the client opens with. The device token exists only because
            // NetworkedMatchSession.ConnectAsync refuses to start without one; nothing reads it.
            _clientOptions = new ClientOptions
            {
                DeviceToken = "bot-internal-" + botUsername,
                DisplayName = botDisplayName,
            };
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>Stable provenance written beside captured training episodes.</summary>
        public string PolicyIdentity => _policy.Identity;

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
            IObservedSeatPolicy? observedPolicy = _policy as IObservedSeatPolicy;
            GameState observedState = client.Current;
            GameState transitionBefore = observedState;
            // The newest state whose events the policy has seen. The server sends a move's
            // snapshot and THEN its events, as two messages, and the client installs the snapshot
            // as Current before the events arrive -- so the loop below can read a state whose draw
            // its memory has not counted yet. Deciding then fails the policy's deck check and the
            // seat plays that move with Greedy (seen 2026-10-18: 1 move in 26).
            ulong eventsSeen = observedState.StateVersion;
            void OnSnap(GameState current)
            {
                transitionBefore = observedState;
                observedState = current;
                if (observedPolicy is null) wake.Release();
            }
            void OnEvents(IReadOnlyList<GameEvent> events)
            {
                if (observedPolicy is null) return;
                observedPolicy.ObserveTransition(
                    transitionBefore, events, observedState, client.Player);
                Volatile.Write(ref eventsSeen, observedState.StateVersion);
                // For memory-bearing policies the event batch, not the preceding snapshot, makes
                // the new decision ready. This prevents scoring from racing stale player memory.
                wake.Release();
            }
            client.OnSnapshot += OnSnap;
            client.OnEvents += OnEvents;
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
                    // Give this state's events a moment to land. Bounded, because a move with no
                    // events sends no events message at all; the bot's own pacing delay below
                    // dwarfs the wait.
                    if (observedPolicy is not null && Volatile.Read(ref eventsSeen) < state.StateVersion)
                    {
                        long deadline = Environment.TickCount64 + 250;
                        while (Volatile.Read(ref eventsSeen) < state.StateVersion
                               && Environment.TickCount64 < deadline)
                            await wake.WaitAsync(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
                        // A newer state arrived meanwhile: decide on that one instead.
                        if (client.Current.StateVersion != state.StateVersion) continue;
                    }

                    PlayerIntent? intent = await _policy.ChooseAsync(state, client.Player, _cts.Token)
                        .ConfigureAwait(false);
                    lastActed = state.StateVersion;
                    if (intent is null) continue;

                    // Human-like pacing (design doc): the bot never acts with inhuman instancy.
                    // System.Random is banned by Kingdom.Tools.DeterminismGate check 7 (CI Stage 4;
                    // it was ServerFirewallTests until 2026-08-18) — determinism. The CSPRNG is
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
            catch (OperationCanceledException) when (_closed || _cts.IsCancellationRequested)
            {
                // Normal seat shutdown while a policy request or pacing delay is in flight.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[bot] policy {0} failed: {1}", _policy.Identity, ex.Message);
            }
            finally
            {
                client.OnSnapshot -= OnSnap;
                client.OnEvents -= OnEvents;
            }
        }
    }
}
