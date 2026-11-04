using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Serialization;
using Kingdom.Net.Client;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// One match as the soak driver sees it: each seat's projected view, and a door to submit
    /// through. Two tables sit behind it. <see cref="LoopbackSoakTable"/> is the real client and
    /// the real server session over an in-memory transport, and is what proves the wire.
    /// <see cref="DirectSoakTable"/> is the rules core alone, and writes the same replay, manifest
    /// and telemetry for a small fraction of the work.
    /// </summary>
    internal interface ISoakTable : IDisposable
    {
        MatchId Id { get; }
        PlayerId Player(int seat);

        /// <summary>What <paramref name="seat"/> can see now. Never the authoritative state.</summary>
        GameState View(int seat);

        /// <summary><c>GameStateHasher.Hash</c> of <see cref="View"/>.</summary>
        string ViewHash(int seat);

        Task StartAsync();
        Task<CommandResult> SubmitAsync(int seat, PlayerIntent intent);

        int Resyncs { get; }
        int Misattributions { get; }

        /// <summary>The outcome the replay log closed with, or null while it is still open.</summary>
        MatchOutcome? LoggedOutcome { get; }
    }

    internal sealed class LoopbackSoakTable : ISoakTable
    {
        private readonly SoakLoopback _peer;
        private readonly NetworkedMatchSession[] _clients;
        private int _misattributions;

        public LoopbackSoakTable(
            ICardDatabase db, ulong seed, string matchId, MatchSetup setup,
            ITelemetrySink? telemetry, MatchAttribution? attribution, IReplayStore? durableReplay,
            string device0, string device1)
        {
            _peer = new SoakLoopback(db, seed, matchId, setup, telemetry, attribution, durableReplay);
            var opts = new ClientOptions { OnMisattribution = _ => Interlocked.Increment(ref _misattributions) };
            _clients = new[]
            {
                _peer.Client(0, opts with { DeviceToken = device0 }),
                _peer.Client(1, opts with { DeviceToken = device1 }),
            };
            _peer.Bind(_clients[0], _clients[1]);
        }

        public MatchId Id => _peer.Id;
        public PlayerId Player(int seat) => _clients[seat].Player;
        public GameState View(int seat) => _clients[seat].Current;
        public string ViewHash(int seat) => GameStateHasher.Hash(_clients[seat].Current);
        public int Resyncs => _clients[0].ResyncCount + _clients[1].ResyncCount;
        public int Misattributions => _misattributions;
        public MatchOutcome? LoggedOutcome => _peer.Replay.Matches[_peer.Id.Value].Outcome;

        public Task StartAsync()
        {
            Task s0 = _clients[0].ConnectAsync(new Uri("loopback://soak"));
            Task s1 = _clients[1].ConnectAsync(new Uri("loopback://soak"));
            _peer.Start();
            return Task.WhenAll(s0, s1);
        }

        public async Task<CommandResult> SubmitAsync(int seat, PlayerIntent intent)
        {
            Task<CommandResult> submitted = _clients[seat].SubmitAsync(intent);
            await _peer.RelayAsync();
            return await submitted;
        }

        public void Dispose() => _peer.Dispose();
    }

    /// <summary>
    /// The rules core with nothing around it: <see cref="LocalMatchHost"/>, the replay log, and the
    /// same <see cref="MatchTelemetryRecorder"/> the server session feeds. No snapshot is encoded,
    /// decoded or re-hashed, which is where a loopback match spends nearly all of its time. The
    /// replay is held in memory and written in one open when the match is over.
    ///
    /// <para><b>This table cannot find a desync.</b> There is one state and nobody to disagree
    /// with it, so <see cref="Resyncs"/> is zero by construction and a clean run here says nothing
    /// about the wire. The loopback soak stays the check on that; this is for making games.</para>
    ///
    /// <para>Its output is the loopback's, byte for byte, and that is checked rather than argued:
    /// <c>work/parallel-rollouts/compare_captures.py</c> over the same seeds.</para>
    /// </summary>
    internal sealed class DirectSoakTable : ISoakTable
    {
        private readonly ulong _seed;
        private readonly LocalMatchHost _host;
        private readonly JsonlReplayStore? _replay;
        private readonly List<PlayerIntent> _accepted = new List<PlayerIntent>();
        private readonly MatchTelemetryRecorder _telemetry;
        private readonly PlayerId[] _players = { new PlayerId("p0"), new PlayerId("p1") };
        private readonly GameState?[] _views = new GameState?[2];
        private readonly string?[] _viewHashes = new string?[2];
        private bool _started, _ended;

        public DirectSoakTable(
            ICardDatabase db, ulong seed, string matchId, MatchSetup setup,
            ITelemetrySink? telemetry, MatchAttribution? attribution, JsonlReplayStore? durableReplay)
        {
            Id = new MatchId(matchId);
            _seed = seed;
            _host = new LocalMatchHost(new CommandProcessor(db), db, setup);
            _replay = durableReplay;
            _telemetry = new MatchTelemetryRecorder(Id, setup, telemetry, attribution, db);
        }

        public MatchId Id { get; }
        public PlayerId Player(int seat) => _players[seat];
        public int Resyncs => 0;
        public int Misattributions => 0;
        public MatchOutcome? LoggedOutcome { get; private set; }

        // A view lives until the next accepted intent; nothing else can change the state. The
        // seat that is not to move is asked for one every pass, and most passes change nothing.
        public GameState View(int seat) => _views[seat] ??= _host.Snapshot(_players[seat]);
        public string ViewHash(int seat) => _viewHashes[seat] ??= GameStateHasher.Hash(View(seat));

        public Task StartAsync()
        {
            _host.BeginMatch(Id, _players[0], _players[1], _seed);
            _telemetry.AfterDeal(_host.State);
            _started = true;
            return Task.CompletedTask;
        }

        /// <summary>The accepted path of <c>MatchSession.SubmitToCore</c>, in its order, and the
        /// rejects a client can actually be handed. Rate limits and strikes have no meaning here.</summary>
        public Task<CommandResult> SubmitAsync(int seat, PlayerIntent intent)
        {
            GameState state = _host.State;
            if (!Equals(intent.Player, _players[seat]))
                throw new InvalidOperationException(
                    $"Seat {seat} submitted an intent for '{intent.Player.Value}'.");
            if (_ended) return Rejected(RejectReason.GameOver);
            if (!IntentAllowlist.IsClientLegal(intent)) return Rejected(RejectReason.UnknownIntent);
            if (IntentAllowlist.IsTurnScoped(intent)
                && state.Phase != TurnPhase.Mulligan
                && !Equals(state.ActivePlayer, _players[seat]))
                return Rejected(RejectReason.NotYourTurn);

            MatchTelemetryRecorder.Pending observed = _telemetry.BeforeSubmit(state, intent);
            CommandResult raw = _host.Submit(intent);
            if (!raw.Accepted) return Task.FromResult(raw);

            _telemetry.AfterAccepted(state, observed, raw.Events);
            _accepted.Add(intent);
            _views[0] = _views[1] = null;
            _viewHashes[0] = _viewHashes[1] = null;

            if (state.Outcome != MatchOutcome.InProgress)
            {
                _replay?.WriteAll(Id, _seed, _players[0], _players[1], _accepted, state.Outcome);
                LoggedOutcome = state.Outcome;
                _telemetry.Complete(
                    state,
                    DateTimeOffset.FromUnixTimeMilliseconds(SoakLoopback.HarnessEpochMs),
                    intent is ConcedeIntent ? MatchEndReason.Concede : MatchEndReason.Win);
                _ended = true;
            }
            return Task.FromResult(raw);
        }

        private Task<CommandResult> Rejected(RejectReason reason)
            => Task.FromResult(CommandResult.Rejected(reason, _host.StateVersion));

        /// <summary>A match that stopped short leaves what the loopback would have: the intents
        /// that were accepted, and no footer.</summary>
        public void Dispose()
        {
            if (_started && !_ended)
                _replay?.WriteAll(Id, _seed, _players[0], _players[1], _accepted, outcome: null);
            _ended = true;
        }
    }
}
