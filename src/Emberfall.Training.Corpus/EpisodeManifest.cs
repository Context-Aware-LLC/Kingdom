using System;
using System.Collections.Generic;
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
        IReadOnlyList<string> P0Deck,
        IReadOnlyList<string> P1Deck,
        int HeroMaxHealth,
        string SparkTokenId,
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
            Require(SparkTokenId, nameof(SparkTokenId));
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
            }
            if (P0Deck is null || P0Deck.Count == 0) throw new ArgumentException("P0Deck must not be empty.");
            if (P1Deck is null || P1Deck.Count == 0) throw new ArgumentException("P1Deck must not be empty.");
            if (HeroMaxHealth <= 0) throw new ArgumentOutOfRangeException(nameof(HeroMaxHealth));
            if (Outcome == MatchOutcome.InProgress)
                throw new ArgumentException("A completed episode manifest requires a terminal outcome.", nameof(Outcome));
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
        string AfterSha256);
}
