using System.IO;
using System.Text;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server.Replay
{
    /// <summary>
    /// Append-only `.jsonl` per match under a data dir (b1-match-server-design.md §9). Each intent
    /// row is a full `ProtocolCodec.Encode(IntentMsg)` envelope — one codec both directions, zero
    /// parallel parser surface (the reason IntentJsonCodec stays internal to the protocol assembly).
    /// Row 0 is a header line carrying the seed; the seed lives ONLY here, server-side (ADR-0004).
    /// </summary>
    public sealed class JsonlReplayStore : IReplayStore
    {
        private readonly string _dir;

        public JsonlReplayStore(string dataDir)
        {
            _dir = dataDir;
            Directory.CreateDirectory(_dir);
        }

        public string PathFor(MatchId id) => Path.Combine(_dir, id.Value + ".jsonl");

        public void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1)
        {
            // Hand-rolled header: values are a u64 and two ids we mint ourselves — no escaping needed.
            string header = "{\"emberfallReplay\":1,\"seed\":" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"p0\":\"" + p0.Value + "\",\"p1\":\"" + p1.Value + "\"}\n";
            File.WriteAllText(PathFor(id), header, new UTF8Encoding(false));
        }

        public void Append(MatchId id, PlayerIntent intent)
        {
            byte[] row = ProtocolCodec.Encode(new IntentMsg(0UL, intent), id, 0UL);
            using var s = new FileStream(PathFor(id), FileMode.Append, FileAccess.Write, FileShare.Read);
            s.Write(row, 0, row.Length);
            s.WriteByte((byte)'\n');
        }

        public void Complete(MatchId id, MatchOutcome outcome)
        {
            string footer = "{\"outcome\":\"" + outcome + "\"}\n";
            File.AppendAllText(PathFor(id), footer, new UTF8Encoding(false));
        }

        /// <summary>Decision 24 void: a footer that carries no outcome, so a reader can tell
        /// "ended with no result" from "still in flight" (a file with no footer at all).</summary>
        public void Abandon(MatchId id)
            => File.AppendAllText(PathFor(id), "{\"void\":true}\n", new UTF8Encoding(false));
    }
}
