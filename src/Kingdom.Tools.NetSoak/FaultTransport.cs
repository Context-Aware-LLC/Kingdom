using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// The three impairments a real network has and the in-memory <see cref="Kingdom.Net.Client.Loopback.LoopbackTransport"/>
    /// structurally cannot produce: added <b>latency</b> (with jitter), frame <b>loss</b>, and a
    /// mid-stream <b>disconnect</b>. EF-076 — the fault surface a real desync gate (EF-075 real AI
    /// over real sockets, EF-077 the CI soak stage) drives its snapshots through.
    /// </summary>
    /// <remarks>
    /// <para>Every knob is an integer and every decision is drawn from the sanctioned splitmix64
    /// <see cref="DeterministicRandom"/> (never <c>System.Random</c>), so a fault run is reproducible
    /// from its <c>(seed, profile)</c> pair alone — the drop set, jitter schedule and disconnect point
    /// are byte-identical on every machine. Only the wall-clock <i>duration</i> of a latency hold is
    /// non-deterministic; the counters this decorator exposes are not.</para>
    /// </remarks>
    public readonly struct FaultProfile
    {
        /// <summary>Fixed delay added to every DELIVERED inbound frame, milliseconds.</summary>
        public int LatencyMs { get; init; }

        /// <summary>Extra uniform delay in [0, JitterMs] added per delivered frame, milliseconds.</summary>
        public int JitterMs { get; init; }

        /// <summary>Inbound frames dropped, per thousand (‰). Integer to stay float-free.</summary>
        public int LossPermille { get; init; }

        /// <summary>&gt;0: force-close the wire once this many inbound frames have been seen. 0 = never.</summary>
        public int DisconnectAfterFrames { get; init; }

        public bool IsActive =>
            LatencyMs > 0 || JitterMs > 0 || LossPermille > 0 || DisconnectAfterFrames > 0;

        public override string ToString() =>
            $"latency={LatencyMs}ms jitter={JitterMs}ms loss={LossPermille}‰ " +
            $"disconnect@{(DisconnectAfterFrames > 0 ? DisconnectAfterFrames.ToString() : "never")}";

        /// <summary>
        /// Parse trailing <c>--latency N --jitter N --loss N --disconnect N</c> flags out of an argv
        /// tail. Unknown/absent flags leave their axis at zero, so the returned profile is inert
        /// (<see cref="IsActive"/> false) unless a caller opted into a fault — the default soak path
        /// is unchanged.
        /// </summary>
        public static FaultProfile Parse(string[] args)
        {
            int latency = 0, jitter = 0, loss = 0, disconnect = 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--latency": int.TryParse(args[i + 1], out latency); break;
                    case "--jitter": int.TryParse(args[i + 1], out jitter); break;
                    case "--loss": int.TryParse(args[i + 1], out loss); break;
                    case "--disconnect": int.TryParse(args[i + 1], out disconnect); break;
                }
            }
            return new FaultProfile
            {
                LatencyMs = Math.Max(0, latency),
                JitterMs = Math.Max(0, jitter),
                LossPermille = Math.Clamp(loss, 0, 1000),
                DisconnectAfterFrames = Math.Max(0, disconnect),
            };
        }
    }

    /// <summary>
    /// An <see cref="IWireTransport"/> decorator that impairs the server&#8594;client (inbound) path per a
    /// <see cref="FaultProfile"/>. The send path is deliberately left clean: EF-076's gate is snapshot
    /// loss (the desync surface), and outbound intent/resync loss is a distinct fault already modelled
    /// by the test tree's <c>FaultInjectingTransport.DropNextResync</c> — keeping send unimpaired here
    /// isolates the two so a soak can attribute a desync to exactly one of them.
    /// </summary>
    public sealed class FaultTransport : IWireTransport
    {
        private readonly IWireTransport _inner;
        private readonly FaultProfile _profile;
        private readonly DeterministicRandom _rng;
        private int _innerFramesSeen;
        private bool _disconnected;

        public FaultTransport(IWireTransport inner, FaultProfile profile, ulong seed)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _profile = profile;
            _rng = new DeterministicRandom(seed);
            _inner.OnClosed += (r, detail) => OnClosed?.Invoke(r, detail);
        }

        // ── deterministic counters (a pure function of seed + profile + inner frame order) ──

        /// <summary>Inbound frames handed on to the client.</summary>
        public int FramesDelivered { get; private set; }

        /// <summary>Inbound frames swallowed by the loss injector.</summary>
        public int FramesDropped { get; private set; }

        /// <summary>Total latency the decorator scheduled across all delivered frames, milliseconds.</summary>
        public long LatencyScheduledMs { get; private set; }

        /// <summary>Whether the disconnect injector force-closed the wire.</summary>
        public bool DisconnectFired { get; private set; }

        public event Action<WireCloseReason, string?>? OnClosed;

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _inner.ConnectAsync(uri, ct);

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct) => _inner.SendAsync(utf8, ct);

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_disconnected) return null;

                byte[]? frame = await _inner.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null) return null; // real peer close — nothing to impair.

                _innerFramesSeen++;

                // disconnect: force-close once the scheduled inbound-frame count is reached. Modelled
                // as a peer-side fault so the session takes its Fault -> reconnect path, not a clean exit.
                if (_profile.DisconnectAfterFrames > 0 && _innerFramesSeen >= _profile.DisconnectAfterFrames)
                {
                    _disconnected = true;
                    DisconnectFired = true;
                    OnClosed?.Invoke(WireCloseReason.Fault, null);
                    return null;
                }

                // loss: drop this frame and receive the next instead — the client sees a gap, exactly
                // as a lost packet on the wire. Draw is taken only for frames that survive to here, so
                // the RNG sequence is a stable function of the frames actually seen.
                if (_profile.LossPermille > 0 && _rng.NextInt(1000) < _profile.LossPermille)
                {
                    FramesDropped++;
                    continue;
                }

                // latency + jitter: hold the frame before delivery.
                int hold = _profile.LatencyMs + (_profile.JitterMs > 0 ? _rng.NextInt(_profile.JitterMs + 1) : 0);
                if (hold > 0)
                {
                    LatencyScheduledMs += hold;
                    await Task.Delay(hold, ct).ConfigureAwait(false);
                }

                FramesDelivered++;
                return frame;
            }
        }

        public Task CloseAsync(WireCloseReason reason) => _inner.CloseAsync(reason);
    }
}
