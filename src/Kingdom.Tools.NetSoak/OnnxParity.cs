using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Policy;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// Checks the ONNX bot against the Python scorer it replaces, two ways.
    ///
    /// <para><c>onnx-parity &lt;fixture.jsonl&gt; &lt;bot-model-dir&gt;</c> reads decisions the
    /// Python encoder already encoded and scored (<c>training_trainer onnx-parity-fixture</c>, in kingdom-training) and
    /// requires the C# encoder to produce every input array EXACTLY, and ONNX Runtime to reproduce
    /// the logits to <see cref="LogitTolerance"/> and the argmax always.</para>
    ///
    /// <para><see cref="DualScorer"/> does the same live, inside an arena (<c>--verify-onnx</c>):
    /// every decision the Python scorer answers is also scored by the ONNX model from the same
    /// request, so the comparison covers the exact JSON the server will send, not a corpus copy.</para>
    /// </summary>
    internal static class OnnxParity
    {
        internal const double LogitTolerance = 1e-3;

        internal static int RunFixture(string[] args)
        {
            string fixture = args[1];
            using OnnxCheckpointScorer scorer = OnnxCheckpointScorer.Open(args[2]);
            var encoder = scorer.Encoder;
            int decisions = 0, encodingMismatches = 0, argmaxDisagreements = 0, refused = 0;
            double worstLogit = 0, worstValue = 0;
            var notes = new List<string>();
            foreach (string line in File.ReadLines(fixture))
            {
                if (line.Length == 0) continue;
                decisions++;
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement row = document.RootElement;
                JsonElement[] actions = row.GetProperty("legalActions").EnumerateArray().ToArray();
                PolicyInputs inputs;
                try
                {
                    inputs = encoder.Encode(row.GetProperty("observation"), row.GetProperty("playerMemory"), actions);
                }
                catch (PolicyEncodingException ex)
                {
                    refused++;
                    if (notes.Count < 10) notes.Add($"decision {decisions}: refused: {ex.Message}");
                    continue;
                }
                string? mismatch = CompareArrays(inputs, row.GetProperty("arrays"));
                if (mismatch is not null)
                {
                    encodingMismatches++;
                    if (notes.Count < 10) notes.Add($"decision {decisions}: {mismatch}");
                    continue;
                }
                PolicyScore score = scorer.Run(inputs);
                double[] expected = row.GetProperty("logits").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                double gap = 0;
                for (int i = 0; i < expected.Length; i++) gap = Math.Max(gap, Math.Abs(expected[i] - score.Logits[i]));
                worstLogit = Math.Max(worstLogit, gap);
                worstValue = Math.Max(worstValue, Math.Abs(row.GetProperty("value").GetDouble() - score.Value));
                if (Argmax(expected) != Argmax(score.Logits))
                {
                    argmaxDisagreements++;
                    if (notes.Count < 10) notes.Add($"decision {decisions}: argmax differs (logit gap {gap:G4})");
                }
            }
            foreach (string note in notes) Console.WriteLine($"[onnx-parity] {note}");
            bool pass = decisions > 0 && refused == 0 && encodingMismatches == 0 && argmaxDisagreements == 0
                        && worstLogit <= LogitTolerance && worstValue <= LogitTolerance;
            Console.WriteLine(
                $"[onnx-parity] decisions={decisions} refused={refused} encodingMismatches={encodingMismatches} "
                + $"argmaxDisagreements={argmaxDisagreements} maxLogitDifference={worstLogit.ToString("G4", CultureInfo.InvariantCulture)} "
                + $"maxValueDifference={worstValue.ToString("G4", CultureInfo.InvariantCulture)} "
                + (pass ? "PASS" : "FAIL"));
            return pass ? 0 : 1;
        }

        /// <summary>Python's arrays are unpadded on the target and effect axes; the C# ones are
        /// padded to the export caps, and every padded cell must be zero.</summary>
        private static string? CompareArrays(PolicyInputs inputs, JsonElement arrays)
        {
            foreach (JsonProperty array in arrays.EnumerateObject())
            {
                int[] shape = array.Value.GetProperty("shape").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                double[] expected = array.Value.GetProperty("values").EnumerateArray()
                    .Select(v => v.ValueKind switch
                    {
                        JsonValueKind.True => 1.0,
                        JsonValueKind.False => 0.0,
                        _ => v.GetDouble(),
                    })
                    .ToArray();
                (double[] actual, int[] actualShape) = Flatten(inputs, array.Name);
                if (shape[0] != 1 || shape.Length != actualShape.Length + 1)
                    return $"{array.Name}: shape [{string.Join(",", shape)}] has no C# counterpart";
                // Every axis but the padded one must agree; the padded one may only be wider here.
                for (int axis = 1; axis < shape.Length; axis++)
                {
                    bool padded = (array.Name.StartsWith("legal_action_target_", StringComparison.Ordinal) && axis == 2)
                                  || (array.Name.StartsWith("ongoing_effect_", StringComparison.Ordinal) && axis == 1);
                    // A padded axis may differ either way; the cell loop below requires every cell
                    // outside the shared region to be zero on whichever side has it.
                    int want = shape[axis], have = actualShape[axis - 1];
                    if (!padded && have != want)
                        return $"{array.Name}: axis {axis} is {have} here and {want} in Python";
                }
                int[] strides = Strides(actualShape);
                var seen = new bool[actual.Length];
                int[] index = new int[shape.Length - 1];
                for (int flat = 0; flat < expected.Length; flat++)
                {
                    int rest = flat;
                    for (int axis = shape.Length - 1; axis >= 1; axis--)
                    {
                        index[axis - 1] = rest % shape[axis];
                        rest /= shape[axis];
                    }
                    bool inside = true;
                    int at = 0;
                    for (int axis = 0; axis < index.Length; axis++)
                    {
                        if (index[axis] >= actualShape[axis]) { inside = false; break; }
                        at += index[axis] * strides[axis];
                    }
                    if (!inside)
                    {
                        if (expected[flat] != 0) return $"{array.Name}: Python has a value past the C# cap";
                        continue;
                    }
                    seen[at] = true;
                    if ((float)expected[flat] != (float)actual[at])
                        return $"{array.Name}[{string.Join(",", index)}]: Python {expected[flat]}, C# {actual[at]}";
                }
                for (int at = 0; at < actual.Length; at++)
                    if (!seen[at] && actual[at] != 0)
                        return $"{array.Name}: C# padding cell {at} is {actual[at]}, not zero";
            }
            return null;
        }

        private static int[] Strides(int[] shape)
        {
            var strides = new int[shape.Length];
            int stride = 1;
            for (int axis = shape.Length - 1; axis >= 0; axis--)
            {
                strides[axis] = stride;
                stride *= shape[axis];
            }
            return strides;
        }

        private static (double[] Values, int[] Shape) Flatten(PolicyInputs inputs, string name)
        {
            int a = inputs.Actions, t = inputs.Targets, o = inputs.Effects;
            int e = PolicyDecisionEncoder.VisibleEntityLayout.Length;
            static double[] F(float[] v) => v.Select(x => (double)x).ToArray();
            static double[] L(long[] v) => v.Select(x => (double)x).ToArray();
            static double[] B(bool[] v) => v.Select(x => x ? 1.0 : 0.0).ToArray();
            return name switch
            {
                "observations" => (F(inputs.Observations), new[] { inputs.Observations.Length }),
                "visible_entity_mask" => (B(inputs.VisibleEntityMask), new[] { e }),
                "visible_entity_kind" => (L(inputs.VisibleEntityKind), new[] { e }),
                "visible_entity_definition" => (L(inputs.VisibleEntityDefinition), new[] { e }),
                "visible_entity_features" => (F(inputs.VisibleEntityFeatures), new[] { e, PolicyDecisionEncoder.VisibleEntityFeatures.Length }),
                "visible_entity_keywords" => (F(inputs.VisibleEntityKeywords), new[] { e, PolicyDecisionEncoder.VisibleEntityKeywords.Length }),
                "card_memory_counts" => (F(inputs.CardMemoryCounts), new[] { inputs.CardDefinitions, PolicyDecisionEncoder.CardMemoryFeatures.Length }),
                "ongoing_effect_mask" => (B(inputs.OngoingEffectMask), new[] { o }),
                "ongoing_effect_definition" => (L(inputs.OngoingEffectDefinition), new[] { o }),
                "ongoing_effect_controller_is_self" => (F(inputs.OngoingEffectControllerIsSelf), new[] { o }),
                "ongoing_effect_lifetime" => (L(inputs.OngoingEffectLifetime), new[] { o }),
                "ongoing_effect_remaining_uses" => (F(inputs.OngoingEffectRemainingUses), new[] { o }),
                "legal_action_mask" => (B(inputs.LegalActionMask), new[] { a }),
                "legal_action_type" => (L(inputs.LegalActionType), new[] { a }),
                "legal_action_source_definition" => (L(inputs.LegalActionSourceDefinition), new[] { a }),
                "legal_action_source_entity" => (L(inputs.LegalActionSourceEntity), new[] { a }),
                "legal_action_slot" => (F(inputs.LegalActionSlot), new[] { a }),
                "legal_action_target_mask" => (B(inputs.LegalActionTargetMask), new[] { a, t }),
                "legal_action_target_binding" => (L(inputs.LegalActionTargetBinding), new[] { a, t }),
                "legal_action_target_entity" => (L(inputs.LegalActionTargetEntity), new[] { a, t }),
                "legal_action_target_definition" => (L(inputs.LegalActionTargetDefinition), new[] { a, t }),
                "legal_action_target_zone" => (L(inputs.LegalActionTargetZone), new[] { a, t }),
                "legal_action_target_controller_is_self" => (F(inputs.LegalActionTargetControllerIsSelf), new[] { a, t }),
                _ => throw new InvalidDataException($"fixture array '{name}' is not a model input"),
            };
        }

        private static int Argmax(IReadOnlyList<double> values)
        {
            int best = 0;
            for (int i = 1; i < values.Count; i++) if (values[i] > values[best]) best = i;
            return best;
        }

        /// <summary>
        /// Plays the Python scorer's answer and scores every request again through ONNX, counting
        /// how often the two disagree. A decision the C# encoder refuses is counted, not thrown:
        /// the point is to measure, and the game goes on with the Python answer.
        /// </summary>
        internal sealed class DualScorer : ICheckpointScorer
        {
            private static readonly JsonSerializerOptions MemoryJson = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            private readonly ICheckpointScorer _python;
            private readonly OnnxCheckpointScorer _onnx;
            private readonly object _gate = new object();
            private long _decisions, _refused, _argmaxDisagreements;
            private double _worstLogit, _worstValue;
            private string? _firstRefusal;

            internal DualScorer(ICheckpointScorer python, OnnxCheckpointScorer onnx)
            {
                _python = python;
                _onnx = onnx;
            }

            public async Task<PolicyScore> ScoreAsync(
                JsonElement observation,
                object playerMemory,
                IReadOnlyList<JsonElement> legalActions,
                CancellationToken cancellationToken)
            {
                PolicyScore python = await _python.ScoreAsync(observation, playerMemory, legalActions, cancellationToken)
                    .ConfigureAwait(false);
                JsonElement memory = JsonSerializer.SerializeToElement(playerMemory, playerMemory.GetType(), MemoryJson);
                PolicyScore? onnx = null;
                string? refusal = null;
                try
                {
                    onnx = _onnx.Score(observation, memory, legalActions);
                }
                catch (PolicyEncodingException ex)
                {
                    refusal = ex.Message;
                }
                lock (_gate)
                {
                    _decisions++;
                    if (onnx is null)
                    {
                        _refused++;
                        _firstRefusal ??= refusal;
                    }
                    else
                    {
                        double gap = 0;
                        for (int i = 0; i < python.Logits.Count; i++)
                            gap = Math.Max(gap, Math.Abs(python.Logits[i] - onnx.Logits[i]));
                        _worstLogit = Math.Max(_worstLogit, gap);
                        _worstValue = Math.Max(_worstValue, Math.Abs(python.Value - onnx.Value));
                        if (Argmax(python.Logits) != Argmax(onnx.Logits)) _argmaxDisagreements++;
                    }
                }
                return python;
            }

            internal bool Report(string label)
            {
                lock (_gate)
                {
                    bool pass = _decisions > 0 && _refused == 0 && _argmaxDisagreements == 0
                                && _worstLogit <= LogitTolerance && _worstValue <= LogitTolerance;
                    Console.WriteLine(
                        $"[verify-onnx] {label} decisions={_decisions} refused={_refused} "
                        + $"argmaxDisagreements={_argmaxDisagreements} "
                        + $"maxLogitDifference={_worstLogit.ToString("G4", CultureInfo.InvariantCulture)} "
                        + $"maxValueDifference={_worstValue.ToString("G4", CultureInfo.InvariantCulture)} "
                        + (pass ? "PASS" : "FAIL")
                        + (_firstRefusal is null ? "" : $" firstRefusal=\"{_firstRefusal}\""));
                    return pass;
                }
            }
        }
    }
}
