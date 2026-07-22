using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Replay;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Support
{
    /// <summary>Deterministic clock for every timer/bucket path.</summary>
    public sealed class FakeTime : ITimeSource
    {
        public long NowMs { get; private set; } = 1_000_000;
        public void Advance(long ms) => NowMs += ms;
    }

    /// <summary>Captures outbound frames; never blocks (the IServerConnection contract).</summary>
    public sealed class FakeConnection : IServerConnection
    {
        public List<byte[]> Frames { get; } = new List<byte[]>();
        public int? CloseCode { get; private set; }
        public string? CloseReason { get; private set; }
        public bool RefuseEnqueue;

        public bool TryEnqueue(byte[] frame)
        {
            if (RefuseEnqueue) return false;
            Frames.Add(frame);
            return true;
        }

        public void Close(int closeCode, string reason)
        {
            CloseCode ??= closeCode;
            CloseReason ??= reason;
        }

        /// <summary>Decode every captured frame (they were encoded by the codec; decode must succeed).</summary>
        public List<NetMessage> Messages()
        {
            var list = new List<NetMessage>(Frames.Count);
            foreach (var f in Frames)
            {
                var res = ProtocolCodec.Decode(f);
                if (!res.Ok) throw new InvalidOperationException($"Server emitted an undecodable frame: {res.Error} {res.Detail}");
                list.Add(res.Message!);
            }
            return list;
        }

        public IEnumerable<T> MessagesOf<T>() where T : NetMessage
        {
            foreach (var m in Messages())
                if (m is T t) yield return t;
        }

        public T Last<T>() where T : NetMessage
        {
            T? found = null;
            foreach (var t in MessagesOf<T>()) found = t;
            return found ?? throw new InvalidOperationException($"No {typeof(T).Name} frame captured.");
        }
    }

    /// <summary>One scripted client seat: encodes frames with its own monotonic envelope seq.</summary>
    public sealed class ScriptedClient
    {
        private ulong _seq;
        public ScriptedClient(PlayerId player) => Player = player;
        public PlayerId Player { get; }

        public byte[] Frame(NetMessage msg, MatchId matchId) => ProtocolCodec.Encode(msg, matchId, ++_seq);
        public byte[] Intent(PlayerIntent intent, MatchId matchId, ulong lastVersion = 0)
            => Frame(new IntentMsg(lastVersion, intent), matchId);
        /// <summary>Reuse the CURRENT seq (a replayed frame) — for the monotonicity tests.</summary>
        public byte[] ReplayedIntent(PlayerIntent intent, MatchId matchId)
            => ProtocolCodec.Encode(new IntentMsg(0, intent), matchId, _seq);
    }

    /// <summary>Everything a session test needs, wired to the REAL card data + prebuilt decks.</summary>
    public sealed class SessionHarness
    {
        public const ulong Seed = 0xE17BEEF5EEDUL;

        public FakeTime Time { get; } = new FakeTime();
        public FakeConnection Conn0 { get; } = new FakeConnection();
        public FakeConnection Conn1 { get; } = new FakeConnection();
        public InMemoryReplayStore Replay { get; } = new InMemoryReplayStore();
        public MatchId Id { get; } = new MatchId("m-test");
        public PlayerId P0 { get; } = new PlayerId("p0");
        public PlayerId P1 { get; } = new PlayerId("p1");
        public ScriptedClient Client0 { get; }
        public ScriptedClient Client1 { get; }
        public MatchSession Session { get; }
        public ServerOptions Options { get; }

        private static FileCardDatabase? _db;
        public static FileCardDatabase Db => _db ??= FileCardDatabase.LoadDirectory(CardDataDir());

        public static string CardDataDir()
        {
            // tests/Emberfall.Net.Server.Tests -> repo root -> unity/Emberfall/Assets/CardData
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Emberfall", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate unity/Emberfall/Assets/CardData from test directory.");
        }

        public SessionHarness(ServerOptions? options = null, bool start = true)
        {
            // Clock tests advance far past the heartbeat timeout on purpose; liveness has its own test.
            // They also jump the clock a whole turn in ONE tick, which the decision-24 stall guard
            // is built to read as "the server was asleep" — correct in production, wrong for a test
            // that means the time to have really elapsed. Both guards are therefore disabled by
            // default and opted INTO by the tests that are about them (which pass real ServerOptions
            // and tick at a realistic ~1 Hz).
            Options = options ?? new ServerOptions
            {
                HeartbeatTimeoutMs = long.MaxValue / 4,
                StallGuardMs = long.MaxValue / 4,
            };
            Client0 = new ScriptedClient(P0);
            Client1 = new ScriptedClient(P1);
            Session = new MatchSession(
                Id, Seed, Options, Time, Db, PrebuiltDecks.EmberwardVsTidecaller(), Replay, P0, P1);
            if (start) Session.Start(Conn0, Conn1);
        }

        public FakeConnection Conn(int seat) => seat == 0 ? Conn0 : Conn1;
        public ScriptedClient Client(int seat) => seat == 0 ? Client0 : Client1;

        public void Post(int seat, byte[] frame) => Session.Dispatch(new SessionCommand.Frame(Conn(seat), frame));
        public void Post(IServerConnection conn, byte[] frame) => Session.Dispatch(new SessionCommand.Frame(conn, frame));
        public void Disconnect(int seat) => Session.Dispatch(new SessionCommand.Disconnected(Conn(seat)));
        public void Tick() => Session.Dispatch(new SessionCommand.ClockTick());
        public void AdvanceAndTick(long ms) { Time.Advance(ms); Tick(); }

        /// <summary>Both seats keep their opening hands — the fastest way out of mulligan.</summary>
        public void ConfirmMulligans()
        {
            Post(0, Client0.Intent(new MulliganIntent(P0, Array.Empty<InstanceId>()), Id));
            Post(1, Client1.Intent(new MulliganIntent(P1, Array.Empty<InstanceId>()), Id));
        }
    }
}
