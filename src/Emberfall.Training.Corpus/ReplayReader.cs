using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Training.Corpus
{
    /// <summary>One replay file, parsed: its header, its ordered intents, and its outcome.</summary>
    public sealed record ReplayRecord(
        ulong Seed,
        string P0,
        string P1,
        IReadOnlyList<PlayerIntent> Intents,
        MatchOutcome Outcome);

    /// <summary>
    /// Reads a durable replay file into its intent stream.
    ///
    /// This lived inside <see cref="TrainingCorpusBuilder"/> as a private method until a second
    /// consumer needed it — the counterfactual evaluator, which replays a prefix to reconstruct a
    /// decision state before branching from it. Extracted rather than copied, deliberately: a
    /// replay parser that exists twice is a pair of parsers that will disagree, and the thing they
    /// would disagree about is what the authoritative engine was asked to do. The reviewer design
    /// note asks for exactly this seam for the same reason.
    ///
    /// It validates shape only. Whether a replay MATCHES a particular episode manifest is a
    /// separate question, and stays with the caller that has the manifest.
    /// </summary>
    public static class ReplayReader
    {
        public static ReplayRecord Read(string path, string expectedMatchId)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) throw new InvalidDataException($"Replay '{path}' is incomplete.");
            using JsonDocument header = JsonDocument.Parse(lines[0]);
            JsonElement root = header.RootElement;
            if (!root.TryGetProperty("emberfallReplay", out JsonElement version) || version.GetInt32() != 1)
                throw new InvalidDataException($"Replay '{path}' has an unsupported header.");
            ulong seed = root.GetProperty("seed").GetUInt64();
            string p0 = root.GetProperty("p0").GetString() ?? "";
            string p1 = root.GetProperty("p1").GetString() ?? "";

            var intents = new List<PlayerIntent>();
            MatchOutcome? outcome = null;
            for (int i = 1; i < lines.Length; i++)
            {
                using JsonDocument row = JsonDocument.Parse(lines[i]);
                if (row.RootElement.TryGetProperty("outcome", out JsonElement outcomeElement))
                {
                    if (!Enum.TryParse(outcomeElement.GetString(), false, out MatchOutcome parsed))
                        throw new InvalidDataException($"Replay '{path}' has an unknown outcome.");
                    outcome = parsed;
                    if (i != lines.Length - 1)
                        throw new InvalidDataException($"Replay '{path}' has rows after its outcome footer.");
                    continue;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(lines[i]);
                DecodeResult decoded = ProtocolCodec.Decode(bytes);
                if (!decoded.Ok || decoded.Message is not IntentMsg intent)
                    throw new InvalidDataException(
                        $"Replay '{path}' row {i + 1} is not an intent: {decoded.Error} {decoded.Detail}");
                if (!string.Equals(decoded.MatchId.Value, expectedMatchId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Replay '{path}' row {i + 1} belongs to match '{decoded.MatchId.Value}', " +
                        $"expected '{expectedMatchId}'.");
                intents.Add(intent.Intent);
            }
            if (!outcome.HasValue) throw new InvalidDataException($"Replay '{path}' has no outcome footer.");
            return new ReplayRecord(seed, p0, p1, intents, outcome.Value);
        }
    }
}
