using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Server.Replay
{
    /// <summary>
    /// Append-only `.jsonl` per match under a data dir (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §9). Each intent
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

        // Hand-rolled header: values are a u64 and two ids we mint ourselves — no escaping needed.
        private static string Header(ulong seed, PlayerId p0, PlayerId p1)
            => "{\"emberfallReplay\":1,\"seed\":" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"p0\":\"" + p0.Value + "\",\"p1\":\"" + p1.Value + "\"}\n";

        private static byte[] IntentRow(MatchId id, PlayerIntent intent)
            => ProtocolCodec.Encode(new IntentMsg(0UL, intent), id, 0UL);

        private static string Footer(MatchOutcome outcome) => "{\"outcome\":\"" + outcome + "\"}\n";

        public void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1)
            => File.WriteAllText(PathFor(id), Header(seed, p0, p1), new UTF8Encoding(false));

        public void Append(MatchId id, PlayerIntent intent)
        {
            byte[] row = IntentRow(id, intent);
            using var s = new FileStream(PathFor(id), FileMode.Append, FileAccess.Write, FileShare.Read);
            s.Write(row, 0, row.Length);
            s.WriteByte((byte)'\n');
        }

        public void Complete(MatchId id, MatchOutcome outcome)
            => File.AppendAllText(PathFor(id), Footer(outcome), new UTF8Encoding(false));

        /// <summary>
        /// A whole match in one open: the bytes <see cref="Begin"/>, one <see cref="Append"/> per
        /// intent and <see cref="Complete"/> would have left. A null <paramref name="outcome"/>
        /// writes no footer, which is what a match still in flight looks like.
        ///
        /// <para>For an offline capture, which holds a finished game in memory and has nothing to
        /// lose by writing it late. A live match must not use this: the journal's value there is
        /// that every accepted intent is on disk before the next one is read.</para>
        /// </summary>
        public void WriteAll(
            MatchId id, ulong seed, PlayerId p0, PlayerId p1,
            IReadOnlyList<PlayerIntent> intents, MatchOutcome? outcome)
        {
            var utf8 = new UTF8Encoding(false);
            using var s = new FileStream(PathFor(id), FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            byte[] header = utf8.GetBytes(Header(seed, p0, p1));
            s.Write(header, 0, header.Length);
            for (int i = 0; i < intents.Count; i++)
            {
                byte[] row = IntentRow(id, intents[i]);
                s.Write(row, 0, row.Length);
                s.WriteByte((byte)'\n');
            }
            if (outcome is MatchOutcome ended)
            {
                byte[] footer = utf8.GetBytes(Footer(ended));
                s.Write(footer, 0, footer.Length);
            }
        }

        /// <summary>Decision 24 void: a footer that carries no outcome, so a reader can tell
        /// "ended with no result" from "still in flight" (a file with no footer at all).</summary>
        public void Abandon(MatchId id)
            => File.AppendAllText(PathFor(id), "{\"void\":true}\n", new UTF8Encoding(false));
    }
}
