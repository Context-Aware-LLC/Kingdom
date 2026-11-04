using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace Kingdom.Policy
{
    /// <summary>
    /// A checkpoint scored in-process through ONNX Runtime: no Python, no subprocess. Reads a
    /// bot-model directory written (in the private kingdom-training repo) by <c>python -m tooling.scripts.training_trainer export-onnx</c>
    /// (<c>model.onnx</c> and <c>bot-model.json</c>) and builds the network's inputs with
    /// <see cref="PolicyDecisionEncoder"/>, the C# port of the Python encoder.
    ///
    /// <para>It answers the same <see cref="ICheckpointScorer"/> requests as
    /// <see cref="CheckpointScorerProcess"/>, from the same JSON, so a seat policy cannot tell the
    /// two apart. Its logits match the Python scorer's to float rounding (the export measures it),
    /// not bit for bit: two runtimes fuse operations differently.</para>
    ///
    /// <para>Thread-safe. ONNX Runtime allows concurrent <c>Run</c> calls on one session, so every
    /// bot seat on a server shares one instance and one copy of the weights.</para>
    /// </summary>
    public sealed class OnnxCheckpointScorer : ICheckpointScorer, IDisposable
    {
        public const string ManifestFile = "bot-model.json";
        private const string Format = "kingdom-policy-onnx";
        private const int SchemaVersion = 1;

        private static readonly JsonSerializerOptions MemoryJson = new JsonSerializerOptions
        {
            // What CheckpointScorerProcess sends the Python scorer, so both encoders read one shape.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>The graph's inputs, in the order <c>MODEL_INPUT_ARRAYS</c> lists them.</summary>
        internal static readonly string[] InputNames =
        {
            "observations",
            "visible_entity_mask",
            "visible_entity_kind",
            "visible_entity_definition",
            "visible_entity_features",
            "visible_entity_keywords",
            "card_memory_counts",
            "ongoing_effect_mask",
            "ongoing_effect_definition",
            "ongoing_effect_controller_is_self",
            "ongoing_effect_lifetime",
            "ongoing_effect_remaining_uses",
            "legal_action_mask",
            "legal_action_type",
            "legal_action_source_definition",
            "legal_action_source_entity",
            "legal_action_slot",
            "legal_action_target_mask",
            "legal_action_target_binding",
            "legal_action_target_entity",
            "legal_action_target_definition",
            "legal_action_target_zone",
            "legal_action_target_controller_is_self",
        };

        private static readonly string[] OutputNames = { "logits", "value" };

        private readonly InferenceSession _session;
        private readonly PolicyDecisionEncoder _encoder;
        private int _disposed;

        private OnnxCheckpointScorer(
            InferenceSession session, PolicyDecisionEncoder encoder, string checkpointSha256, string modelSha256)
        {
            _session = session;
            _encoder = encoder;
            CheckpointSha256 = checkpointSha256;
            ModelSha256 = modelSha256;
        }

        /// <summary>The checkpoint the model was exported from. Seat identities name this, so an
        /// ONNX seat and a Python seat on the same checkpoint report the same policy.</summary>
        public string CheckpointSha256 { get; }

        /// <summary>The <c>model.onnx</c> actually loaded.</summary>
        public string ModelSha256 { get; }

        internal PolicyDecisionEncoder Encoder => _encoder;

        /// <summary>The ids among <paramref name="cardIds"/> the model's vocabulary lacks, sorted.
        /// The encoder reads each as the unknown card.</summary>
        public IReadOnlyList<int> UnknownCards(IEnumerable<int> cardIds) => _encoder.UnknownCards(cardIds);

        /// <summary>
        /// Loads and checks a bot-model directory. Refuses one whose model file does not hash to
        /// its manifest, whose feature spec or vocabulary this encoder does not implement, or whose
        /// graph does not take exactly the inputs this scorer builds.
        /// </summary>
        public static OnnxCheckpointScorer Open(string directory, int intraOpThreads = 1)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Bot model directory must not be empty.", nameof(directory));
            if (intraOpThreads < 1)
                throw new ArgumentOutOfRangeException(nameof(intraOpThreads), "A scorer needs at least one thread.");
            string root = Path.GetFullPath(directory);
            string manifestPath = Path.Combine(root, ManifestFile);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Bot model manifest not found.", manifestPath);

            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            JsonElement m = manifest.RootElement;
            if (!m.TryGetProperty("format", out JsonElement format) || format.GetString() != Format
                || !m.TryGetProperty("schemaVersion", out JsonElement version) || version.GetInt32() != SchemaVersion)
                throw new InvalidDataException($"{manifestPath} is not a {Format} schema {SchemaVersion} manifest.");

            JsonElement model = m.GetProperty("model");
            string modelPath = Path.Combine(root, model.GetProperty("file").GetString()
                ?? throw new InvalidDataException("bot model manifest names no model file"));
            byte[] modelBytes = File.ReadAllBytes(modelPath);
            string modelSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
            string expectedSha = model.GetProperty("sha256").GetString() ?? "";
            if (!string.Equals(modelSha, expectedSha, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{modelPath} hashes to {modelSha}, but the manifest says {expectedSha}.");

            RequireInputs(m.GetProperty("inputs"));
            PolicyDecisionEncoder.RequireFeatureSpec(m.GetProperty("featureSpec"));
            JsonElement vocabulary = m.GetProperty("actionVocabulary");
            PolicyDecisionEncoder.RequireActionVocabulary(vocabulary);
            var encoder = new PolicyDecisionEncoder(
                Strings(vocabulary.GetProperty("cardDefinitions")),
                Strings(vocabulary.GetProperty("targetBindings")),
                m.GetProperty("maxTargets").GetInt32(),
                m.GetProperty("maxOngoingEffects").GetInt32());
            string checkpointSha = m.GetProperty("checkpointSha256").GetString()
                ?? throw new InvalidDataException("bot model manifest names no checkpoint");

            using var options = new SessionOptions
            {
                IntraOpNumThreads = intraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            var session = new InferenceSession(modelBytes, options);
            try
            {
                var declared = new HashSet<string>(session.InputMetadata.Keys, StringComparer.Ordinal);
                if (!declared.SetEquals(InputNames))
                    throw new InvalidDataException(
                        $"{modelPath} takes inputs [{string.Join(", ", declared)}], not the ones this scorer builds.");
                return new OnnxCheckpointScorer(session, encoder, checkpointSha, modelSha);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        Task<PolicyScore> ICheckpointScorer.ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement memory = playerMemory is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(playerMemory, playerMemory.GetType(), MemoryJson);
            return Task.FromResult(Score(observation, memory, legalActions));
        }

        internal PolicyScore Score(
            JsonElement observation, JsonElement playerMemory, IReadOnlyList<JsonElement> legalActions)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OnnxCheckpointScorer));
            PolicyInputs inputs = _encoder.Encode(observation, playerMemory, legalActions);
            return Run(inputs);
        }

        /// <summary>One forward pass over already-encoded inputs.</summary>
        internal PolicyScore Run(PolicyInputs inputs)
        {
            long a = inputs.Actions, t = inputs.Targets, o = inputs.Effects;
            long entities = PolicyDecisionEncoder.VisibleEntityLayout.Length;
            var values = new OrtValue[InputNames.Length];
            try
            {
                int i = 0;
                values[i++] = Tensor(inputs.Observations, 1, PolicyDecisionEncoder.ObservationFeatures.Length);
                values[i++] = Tensor(inputs.VisibleEntityMask, 1, entities);
                values[i++] = Tensor(inputs.VisibleEntityKind, 1, entities);
                values[i++] = Tensor(inputs.VisibleEntityDefinition, 1, entities);
                values[i++] = Tensor(inputs.VisibleEntityFeatures, 1, entities,
                    PolicyDecisionEncoder.VisibleEntityFeatures.Length);
                values[i++] = Tensor(inputs.VisibleEntityKeywords, 1, entities,
                    PolicyDecisionEncoder.VisibleEntityKeywords.Length);
                values[i++] = Tensor(inputs.CardMemoryCounts, 1, inputs.CardDefinitions,
                    PolicyDecisionEncoder.CardMemoryFeatures.Length);
                values[i++] = Tensor(inputs.OngoingEffectMask, 1, o);
                values[i++] = Tensor(inputs.OngoingEffectDefinition, 1, o);
                values[i++] = Tensor(inputs.OngoingEffectControllerIsSelf, 1, o);
                values[i++] = Tensor(inputs.OngoingEffectLifetime, 1, o);
                values[i++] = Tensor(inputs.OngoingEffectRemainingUses, 1, o);
                values[i++] = Tensor(inputs.LegalActionMask, 1, a);
                values[i++] = Tensor(inputs.LegalActionType, 1, a);
                values[i++] = Tensor(inputs.LegalActionSourceDefinition, 1, a);
                values[i++] = Tensor(inputs.LegalActionSourceEntity, 1, a);
                values[i++] = Tensor(inputs.LegalActionSlot, 1, a);
                values[i++] = Tensor(inputs.LegalActionTargetMask, 1, a, t);
                values[i++] = Tensor(inputs.LegalActionTargetBinding, 1, a, t);
                values[i++] = Tensor(inputs.LegalActionTargetEntity, 1, a, t);
                values[i++] = Tensor(inputs.LegalActionTargetDefinition, 1, a, t);
                values[i++] = Tensor(inputs.LegalActionTargetZone, 1, a, t);
                values[i++] = Tensor(inputs.LegalActionTargetControllerIsSelf, 1, a, t);

                using var run = new RunOptions();
                using IDisposableReadOnlyCollection<OrtValue> outputs =
                    _session.Run(run, InputNames, values, OutputNames);
                ReadOnlySpan<float> logits = outputs[0].GetTensorDataAsSpan<float>();
                ReadOnlySpan<float> value = outputs[1].GetTensorDataAsSpan<float>();
                if (logits.Length != inputs.Actions || value.Length != 1)
                    throw new InvalidDataException(
                        $"bot model returned {logits.Length} logits and {value.Length} values for "
                        + $"{inputs.Actions} legal actions.");
                var scores = new double[logits.Length];
                for (int k = 0; k < scores.Length; k++)
                {
                    if (!float.IsFinite(logits[k]))
                        throw new InvalidDataException("bot model produced a non-finite legal-action logit.");
                    scores[k] = logits[k];
                }
                if (!float.IsFinite(value[0]))
                    throw new InvalidDataException("bot model produced a non-finite value.");
                return new PolicyScore(scores, value[0]);
            }
            finally
            {
                foreach (OrtValue? value in values) value?.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _session.Dispose();
        }

        private static OrtValue Tensor<T>(T[] data, params long[] shape) where T : unmanaged
            => OrtValue.CreateTensorValueFromMemory(data, shape);

        private static void RequireInputs(JsonElement inputs)
        {
            if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() != InputNames.Length)
                throw new InvalidDataException("bot model inputs are not the ones this scorer builds");
            int index = 0;
            foreach (JsonElement name in inputs.EnumerateArray())
            {
                if (name.GetString() != InputNames[index])
                    throw new InvalidDataException(
                        $"bot model input {index} is '{name.GetString()}', expected '{InputNames[index]}'");
                index++;
            }
        }

        private static string[] Strings(JsonElement array)
        {
            var result = new string[array.GetArrayLength()];
            int index = 0;
            foreach (JsonElement entry in array.EnumerateArray())
                result[index++] = entry.GetString() ?? throw new InvalidDataException("vocabulary entry is null");
            return result;
        }
    }
}
