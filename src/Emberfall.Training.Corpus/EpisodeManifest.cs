using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Emberfall.Core.Contracts;

namespace Emberfall.Training.Corpus
{
    /// <summary>
    /// The non-replayable provenance for one training episode. The replay remains the minimal
    /// <c>(seed, accepted intents, outcome)</c> truth; this sidecar pins everything needed to
    /// reproduce what those intents meant after cards, policies, and encoders move on.
    /// </summary>
    public sealed record EpisodeManifest(
        int SchemaVersion,
        string MatchId,
        ulong Seed,
        string P0,
        string P1,
        CardClass P0Class,
        CardClass P1Class,
        IReadOnlyList<int> P0Deck,
        IReadOnlyList<int> P1Deck,
        int HeroMaxHealth,
        int SparkTokenId,
        string EngineContractVersion,
        string WireContractVersion,
        string BuildRevision,
        string CardDataSha256,
        string P0Policy,
        string P1Policy,
        string ReplayFile,
        string ReplaySha256,
        string FinalP0ObservationSha256,
        string FinalP1ObservationSha256,
        IReadOnlyList<EpisodeDecisionHash> DecisionHashes,
        MatchOutcome Outcome)
    {
        public const int CurrentSchemaVersion = 1;
        public const string CurrentEngineContractVersion = "interfaces.v4";

        public void Validate()
        {
            if (SchemaVersion != CurrentSchemaVersion)
                throw new NotSupportedException(
                    $"Episode manifest schema {SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            Require(MatchId, nameof(MatchId));
            Require(P0, nameof(P0));
            Require(P1, nameof(P1));
            // A card id is a NUMBER now, so "required" is > 0 rather than non-empty — 0 is the
            // unset value that looks set.
            if (SparkTokenId <= 0)
                throw new InvalidDataException($"{nameof(SparkTokenId)} is required.");
            Require(EngineContractVersion, nameof(EngineContractVersion));
            Require(WireContractVersion, nameof(WireContractVersion));
            Require(BuildRevision, nameof(BuildRevision));
            RequireSha256(CardDataSha256, nameof(CardDataSha256));
            Require(P0Policy, nameof(P0Policy));
            Require(P1Policy, nameof(P1Policy));
            Require(ReplayFile, nameof(ReplayFile));
            RequireSha256(ReplaySha256, nameof(ReplaySha256));
            RequireSha256(FinalP0ObservationSha256, nameof(FinalP0ObservationSha256));
            RequireSha256(FinalP1ObservationSha256, nameof(FinalP1ObservationSha256));
            if (DecisionHashes is null || DecisionHashes.Count == 0)
                throw new ArgumentException("DecisionHashes must contain at least one accepted intent.");
            for (int i = 0; i < DecisionHashes.Count; i++)
            {
                EpisodeDecisionHash decision = DecisionHashes[i];
                if (decision.IntentIndex != i)
                    throw new ArgumentException("DecisionHashes must be contiguous and ordered by intent index.");
                Require(decision.Actor, $"{nameof(DecisionHashes)}[{i}].{nameof(decision.Actor)}");
                RequireSha256(decision.BeforeSha256,
                    $"{nameof(DecisionHashes)}[{i}].{nameof(decision.BeforeSha256)}");
                RequireSha256(decision.AfterSha256,
                    $"{nameof(DecisionHashes)}[{i}].{nameof(decision.AfterSha256)}");
                bool hasAnyBehavior = decision.BehaviorCheckpointSha256 is not null
                                      || decision.BehaviorLogProbability.HasValue
                                      || decision.BehaviorPredictedValue.HasValue
                                      || decision.BehaviorTemperature.HasValue
                                      || decision.BehaviorChosenActionIndex.HasValue
                                      || decision.BehaviorLegalActionCount.HasValue
                                      || decision.BehaviorAllowsConcede.HasValue;
                bool hasAllBehavior = decision.BehaviorCheckpointSha256 is not null
                                      && decision.BehaviorLogProbability.HasValue
                                      && decision.BehaviorPredictedValue.HasValue
                                      && decision.BehaviorTemperature.HasValue
                                      && decision.BehaviorChosenActionIndex.HasValue
                                      && decision.BehaviorLegalActionCount.HasValue;
                if (hasAnyBehavior != hasAllBehavior)
                    throw new ArgumentException(
                        $"{nameof(DecisionHashes)}[{i}] must provide all on-policy behavior fields or none.");
                if (hasAllBehavior)
                {
                    RequireSha256(decision.BehaviorCheckpointSha256!,
                        $"{nameof(DecisionHashes)}[{i}].{nameof(decision.BehaviorCheckpointSha256)}");
                    if (!double.IsFinite(decision.BehaviorLogProbability!.Value)
                        || decision.BehaviorLogProbability.Value > 1e-12)
                        throw new ArgumentException(
                            $"{nameof(DecisionHashes)}[{i}].{nameof(decision.BehaviorLogProbability)} "
                            + "must be a finite log probability no greater than zero.");
                    if (!double.IsFinite(decision.BehaviorPredictedValue!.Value))
                        throw new ArgumentException(
                            $"{nameof(DecisionHashes)}[{i}].{nameof(decision.BehaviorPredictedValue)} must be finite.");
                    if (!double.IsFinite(decision.BehaviorTemperature!.Value)
                        || decision.BehaviorTemperature.Value <= 0.0)
                        throw new ArgumentException(
                            $"{nameof(DecisionHashes)}[{i}].{nameof(decision.BehaviorTemperature)} "
                            + "must be finite and positive.");
                    if (decision.BehaviorLegalActionCount!.Value <= 0
                        || decision.BehaviorChosenActionIndex!.Value < 0
                        || decision.BehaviorChosenActionIndex.Value
                           >= decision.BehaviorLegalActionCount.Value)
                        throw new ArgumentException(
                            $"{nameof(DecisionHashes)}[{i}] has an invalid behavior action index.");
                    ValidateSamplerSpecification(decision, i);
                }
            }
            if (P0Deck is null || P0Deck.Count == 0) throw new ArgumentException("P0Deck must not be empty.");
            if (P1Deck is null || P1Deck.Count == 0) throw new ArgumentException("P1Deck must not be empty.");
            if (HeroMaxHealth <= 0) throw new ArgumentOutOfRangeException(nameof(HeroMaxHealth));
            if (Outcome == MatchOutcome.InProgress)
                throw new ArgumentException("A completed episode manifest requires a terminal outcome.", nameof(Outcome));
        }

        /// <summary>
        /// A mixture row must carry everything needed to recompute the probability its draw used.
        /// Half a sampler specification is worse than none: it looks reproducible and is not.
        /// </summary>
        private static void ValidateSamplerSpecification(EpisodeDecisionHash decision, int i)
        {
            string where = $"{nameof(DecisionHashes)}[{i}]";
            string mode = decision.BehaviorSamplerMode ?? "temperature";
            if (mode != "temperature" && mode != "mixture" && mode != "argmax")
                throw new ArgumentException(
                    $"{where}.{nameof(decision.BehaviorSamplerMode)} '{mode}' is not a known sampler.");
            if (mode != "mixture")
            {
                if (decision.BehaviorEpsilon.HasValue
                    || decision.BehaviorPlayKeySchemaVersion.HasValue)
                    throw new ArgumentException(
                        $"{where} carries mixture fields under sampler mode '{mode}'.");
                return;
            }
            if (!decision.BehaviorEpsilon.HasValue
                || !decision.BehaviorDistinctPlayCount.HasValue
                || !decision.BehaviorPlayKeySchemaVersion.HasValue)
                throw new ArgumentException(
                    $"{where} is a mixture row without a complete sampler specification.");
            if (!double.IsFinite(decision.BehaviorEpsilon.Value)
                || decision.BehaviorEpsilon.Value <= 0.0
                || decision.BehaviorEpsilon.Value >= 1.0)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.BehaviorEpsilon)} must be finite and in (0, 1).");
            if (decision.BehaviorPlayKeySchemaVersion.Value <= 0)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.BehaviorPlayKeySchemaVersion)} must be positive.");
            ValidatePlayGroups(decision, where);
            if (decision.BehaviorDistinctPlayCount!.Value <= 0
                || decision.BehaviorDistinctPlayCount.Value > decision.BehaviorLegalActionCount!.Value)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.BehaviorDistinctPlayCount)} must be between 1 and the "
                    + "legal action count; plays are a grouping OF the listed actions.");
        }

        /// <summary>
        /// The grouping must cover every listed action exactly once and use every play index it
        /// claims. A groups array that disagrees with the action count or the play count is worse
        /// than an absent one: it would let a verifier recompute a confident, wrong mu.
        /// </summary>
        private static void ValidatePlayGroups(EpisodeDecisionHash decision, string where)
        {
            IReadOnlyList<int>? groups = decision.BehaviorPlayGroups;
            if (groups is null || groups.Count == 0)
                throw new ArgumentException(
                    $"{where} is a mixture row without play-group membership; its behavior "
                    + "probability cannot be independently recomputed.");
            int actions = decision.BehaviorLegalActionCount!.Value;
            int plays = decision.BehaviorDistinctPlayCount!.Value;
            if (groups.Count != actions)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.BehaviorPlayGroups)} has {groups.Count} entries "
                    + $"for {actions} legal actions.");
            var seen = new bool[plays];
            foreach (int play in groups)
            {
                if (play < 0 || play >= plays)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.BehaviorPlayGroups)} names play {play}, outside "
                        + $"0..{plays - 1}.");
                seen[play] = true;
            }
            for (int i = 0; i < plays; i++)
                if (!seen[i])
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.BehaviorPlayGroups)} never uses play {i}, so the "
                        + "distinct-play count and the grouping disagree.");
        }

        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} must not be empty.", name);
        }

        private static void RequireSha256(string value, string name)
        {
            Require(value, name);
            if (value.Length != 64)
                throw new ArgumentException($"{name} must be a lowercase SHA-256 hex digest.", name);
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                    throw new ArgumentException($"{name} must be a lowercase SHA-256 hex digest.", name);
        }
    }

    /// <summary>Capture-time projected-state anchors around one accepted replay intent.</summary>
    public sealed record EpisodeDecisionHash(
        int IntentIndex,
        string Actor,
        string BeforeSha256,
        string AfterSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BehaviorCheckpointSha256 = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? BehaviorLogProbability = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? BehaviorPredictedValue = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? BehaviorTemperature = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? BehaviorChosenActionIndex = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? BehaviorLegalActionCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? BehaviorAllowsConcede = null,

        // A SCALAR TEMPERATURE NO LONGER DESCRIBES THE BEHAVIOR POLICY. Once capture can draw from
        // a mixture, a row carrying only a temperature reads as a plain tempered softmax and any
        // consumer that recomputes the behavior probability from it gets a different number than
        // the one the draw used — with nothing anywhere to contradict it. These fields are what
        // make the recorded probability reproducible; absent means the plain tempered sampler.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BehaviorSamplerMode = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? BehaviorEpsilon = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? BehaviorDistinctPlayCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? BehaviorPlayKeySchemaVersion = null,

        // Which distinct play each legal-action index belongs to. Without it a verifier cannot
        // recompute mu(i) = (1-eps)*pi(i) + (eps/K)*pi(i)/pi(P), because pi(P) needs the grouping
        // -- and re-deriving the grouping means a second implementation of the play-key rule that
        // will eventually disagree with the one that drew the action.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? BehaviorPlayGroups = null);
}
