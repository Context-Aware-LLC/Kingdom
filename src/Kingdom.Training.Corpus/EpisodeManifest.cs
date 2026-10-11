using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Kingdom.Core.Contracts;

namespace Kingdom.Training.Corpus
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
                ValidateSearchLabel(decision, i, hasAnyBehavior);
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

        /// <summary>
        /// A searched decision carries the whole search result or none of it. Every field here is
        /// a training target or the key a target is read through, so a row with a candidate list
        /// and no visits, or visits that outnumber their candidates, would train on a number
        /// nothing produced.
        /// </summary>
        private static void ValidateSearchLabel(EpisodeDecisionHash decision, int i, bool hasBehavior)
        {
            string where = $"{nameof(DecisionHashes)}[{i}]";
            bool any = decision.SearchVersion is not null
                       || decision.SearchCandidateActionIndices is not null
                       || decision.SearchVisits is not null
                       || decision.SearchMeanValues is not null
                       || decision.SearchPlayPriors is not null
                       || decision.SearchChosenActionIndex.HasValue
                       || decision.SearchPlayGroups is not null
                       || decision.SearchPlayKeySchemaVersion.HasValue;
            if (!any)
            {
                if (HasAnyRolloutField(decision))
                    throw new ArgumentException(
                        $"{where} carries search rollouts without the search label they belong to.");
                return;
            }
            if (string.IsNullOrWhiteSpace(decision.SearchVersion)
                || decision.SearchCandidateActionIndices is null
                || decision.SearchVisits is null
                || decision.SearchMeanValues is null
                || decision.SearchPlayPriors is null
                || !decision.SearchChosenActionIndex.HasValue
                || decision.SearchPlayGroups is null
                || !decision.SearchPlayKeySchemaVersion.HasValue)
                throw new ArgumentException($"{where} must provide all search-label fields or none.");
            // The behavior fields say how many actions the SAMPLER saw and whether concede was one
            // of them; a searching seat has its own answer to both (every action it grouped, never
            // concede). Two answers on one row is how a legal set gets reconstructed wrongly.
            if (hasBehavior)
                throw new ArgumentException(
                    $"{where} carries both on-policy behavior fields and a search label.");
            if (decision.SearchPlayKeySchemaVersion.Value <= 0)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.SearchPlayKeySchemaVersion)} must be positive.");

            IReadOnlyList<int> groups = decision.SearchPlayGroups;
            if (groups.Count < 2)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.SearchPlayGroups)} must name a play for every legal "
                    + "action, and a searched decision has at least two.");
            int plays = 0;
            foreach (int play in groups)
            {
                if (play < 0 || play >= groups.Count)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchPlayGroups)} names play {play}, outside "
                        + $"0..{groups.Count - 1}.");
                plays = Math.Max(plays, play + 1);
            }
            var used = new bool[plays];
            foreach (int play in groups) used[play] = true;
            for (int play = 0; play < plays; play++)
                if (!used[play])
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchPlayGroups)} never uses play {play}.");

            IReadOnlyList<int> candidates = decision.SearchCandidateActionIndices;
            if (candidates.Count < 2
                || decision.SearchVisits.Count != candidates.Count
                || decision.SearchMeanValues.Count != candidates.Count
                || decision.SearchPlayPriors.Count != candidates.Count)
                throw new ArgumentException(
                    $"{where} search label needs at least two candidates and one visit count, mean "
                    + "value and play prior for each.");
            var candidatePlays = new bool[plays];
            bool chosenIsCandidate = false;
            for (int c = 0; c < candidates.Count; c++)
            {
                int action = candidates[c];
                if (action < 0 || action >= groups.Count)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchCandidateActionIndices)} names action "
                        + $"{action}, outside 0..{groups.Count - 1}.");
                // Candidates are distinct PLAYS. Two spellings of one play would split its visits,
                // and the target over plays would no longer add up the way the search counted.
                if (candidatePlays[groups[action]])
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchCandidateActionIndices)} names play "
                        + $"{groups[action]} twice.");
                candidatePlays[groups[action]] = true;

                int visits = decision.SearchVisits[c];
                double? mean = decision.SearchMeanValues[c];
                if (visits < 0)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchVisits)}[{c}] must not be negative.");
                bool meanValid = visits == 0
                    ? !mean.HasValue
                    : mean.HasValue && double.IsFinite(mean.Value)
                      && mean.Value >= -1.0 && mean.Value <= 1.0;
                if (!meanValid)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchMeanValues)}[{c}] must be in [-1, 1] where "
                        + "the candidate was visited and absent where it was not.");
                double prior = decision.SearchPlayPriors[c];
                if (!double.IsFinite(prior) || prior < 0.0 || prior > 1.0 + 1e-9)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchPlayPriors)}[{c}] must be a probability.");
                if (action == decision.SearchChosenActionIndex.Value)
                {
                    chosenIsCandidate = true;
                    if (visits == 0)
                        throw new ArgumentException(
                            $"{where} search chose a candidate it never visited.");
                }
            }
            if (!chosenIsCandidate)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.SearchChosenActionIndex)} is not a search candidate.");
            ValidateSearchRollouts(decision, where);
        }

        private static bool HasAnyRolloutField(EpisodeDecisionHash decision)
            => decision.SearchDecisionSeed.HasValue
               || decision.SearchRootValue.HasValue
               || decision.SearchCandidateDroppedRound is not null
               || decision.SearchRoundWorlds is not null
               || decision.SearchNullRollouts.HasValue
               || decision.SearchRolloutValues is not null
               || decision.SearchRolloutOwnHealth is not null
               || decision.SearchRolloutOwnArmor is not null
               || decision.SearchRolloutOpponentHealth is not null
               || decision.SearchRolloutOpponentArmor is not null
               || decision.SearchRolloutPlies is not null
               || decision.SearchRolloutEndTurn is not null
               || decision.SearchRolloutCapped is not null;

        /// <summary>
        /// The rollouts are recorded in an order two short arrays imply, so the arrays have to
        /// agree with each other or every rollout is read as some other candidate's. The check
        /// that makes a mislaid layout impossible to miss is the last one: the visits and means of
        /// the label are recomputed from the rollouts and must come out the same.
        /// </summary>
        private static void ValidateSearchRollouts(EpisodeDecisionHash decision, string where)
        {
            if (!HasAnyRolloutField(decision)) return;
            if (!decision.SearchDecisionSeed.HasValue
                || !decision.SearchRootValue.HasValue
                || decision.SearchCandidateDroppedRound is null
                || decision.SearchRoundWorlds is null
                || !decision.SearchNullRollouts.HasValue
                || decision.SearchRolloutValues is null
                || decision.SearchRolloutOwnHealth is null
                || decision.SearchRolloutOwnArmor is null
                || decision.SearchRolloutOpponentHealth is null
                || decision.SearchRolloutOpponentArmor is null
                || decision.SearchRolloutPlies is null
                || decision.SearchRolloutEndTurn is null
                || decision.SearchRolloutCapped is null)
                throw new ArgumentException($"{where} must provide all search-rollout fields or none.");
            if (!double.IsFinite(decision.SearchRootValue.Value))
                throw new ArgumentException(
                    $"{where}.{nameof(decision.SearchRootValue)} must be finite.");

            IReadOnlyList<int> candidates = decision.SearchCandidateActionIndices!;
            IReadOnlyList<int> dropped = decision.SearchCandidateDroppedRound;
            IReadOnlyList<int> worlds = decision.SearchRoundWorlds;
            if (dropped.Count != candidates.Count || worlds.Count == 0)
                throw new ArgumentException(
                    $"{where} needs a dropped round for each search candidate and at least one round.");
            int survivor = -1;
            for (int c = 0; c < dropped.Count; c++)
            {
                if (dropped[c] < -1 || dropped[c] >= worlds.Count)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchCandidateDroppedRound)}[{c}] is not a round.");
                if (dropped[c] != -1) continue;
                if (survivor >= 0)
                    throw new ArgumentException($"{where} search has more than one survivor.");
                survivor = c;
            }
            if (survivor < 0 || candidates[survivor] != decision.SearchChosenActionIndex!.Value)
                throw new ArgumentException(
                    $"{where} search survivor is not the action the search chose.");

            IReadOnlyList<double?> values = decision.SearchRolloutValues;
            int expected = 0;
            for (int round = 0; round < worlds.Count; round++)
            {
                if (worlds[round] <= 0)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchRoundWorlds)}[{round}] must be positive.");
                for (int c = 0; c < dropped.Count; c++)
                    if (dropped[c] == -1 || dropped[c] >= round) expected += worlds[round];
            }
            if (values.Count != expected
                || decision.SearchRolloutOwnHealth.Count != expected
                || decision.SearchRolloutOwnArmor.Count != expected
                || decision.SearchRolloutOpponentHealth.Count != expected
                || decision.SearchRolloutOpponentArmor.Count != expected
                || decision.SearchRolloutPlies.Count != expected
                || decision.SearchRolloutEndTurn.Count != expected)
                throw new ArgumentException(
                    $"{where} search rounds imply {expected} rollouts; every rollout array must "
                    + "hold exactly that many.");

            int previous = -1;
            foreach (int position in decision.SearchRolloutCapped)
            {
                if (position <= previous || position >= expected)
                    throw new ArgumentException(
                        $"{where}.{nameof(decision.SearchRolloutCapped)} must be ascending rollout positions.");
                if (values[position] != 0.0)
                    throw new ArgumentException(
                        $"{where} rollout {position} stopped at the ply cap with a value that is not zero.");
                previous = position;
            }

            var totals = new double[dropped.Count];
            var visits = new int[dropped.Count];
            int missing = 0;
            int next = 0;
            for (int round = 0; round < worlds.Count; round++)
                for (int c = 0; c < dropped.Count; c++)
                {
                    if (dropped[c] != -1 && dropped[c] < round) continue;
                    for (int w = 0; w < worlds[round]; w++, next++)
                    {
                        if (decision.SearchRolloutPlies[next] < 0)
                            throw new ArgumentException(
                                $"{where}.{nameof(decision.SearchRolloutPlies)}[{next}] must not be negative.");
                        if (values[next] is not double value)
                        {
                            missing++;
                            continue;
                        }
                        if (!double.IsFinite(value) || value < -1.0 || value > 1.0)
                            throw new ArgumentException(
                                $"{where}.{nameof(decision.SearchRolloutValues)}[{next}] must be in [-1, 1].");
                        totals[c] += value;
                        visits[c]++;
                    }
                }
            if (missing != decision.SearchNullRollouts.Value)
                throw new ArgumentException(
                    $"{where}.{nameof(decision.SearchNullRollouts)} is {decision.SearchNullRollouts.Value}; "
                    + $"{missing} rollouts have no value.");
            for (int c = 0; c < dropped.Count; c++)
            {
                double? mean = decision.SearchMeanValues![c];
                if (visits[c] != decision.SearchVisits![c]
                    || (visits[c] > 0 && Math.Abs(totals[c] / visits[c] - mean!.Value) > 1e-9))
                    throw new ArgumentException(
                        $"{where} search candidate {c} has visits and a mean its rollouts do not add up to.");
            }
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
        IReadOnlyList<int>? BehaviorPlayGroups = null,

        // WHAT A SEARCHING SEAT FOUND, when it searched. Present together or not at all, and never
        // beside the Behavior* fields: a searched decision is a label, not an on-policy sample.
        // Candidates are legal-action indices into the list the seat enumerated WITHOUT concede,
        // one per distinct play; visits, mean values and play priors run parallel to them. A mean
        // is null where the candidate was never visited (JSON has no NaN, and zero is a real
        // value: a drawn rollout). The play groups cover every legal action, because the policy
        // target is over plays and regrouping downstream would be a second DistinctPlayKey.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SearchVersion = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchCandidateActionIndices = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchVisits = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<double?>? SearchMeanValues = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<double>? SearchPlayPriors = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? SearchChosenActionIndex = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchPlayGroups = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? SearchPlayKeySchemaVersion = null,

        // EVERY ROLLOUT BEHIND THE LABEL (fair-search.v3 on). Present together or not at all, and
        // only beside the label above; a v1 or v2 capture has none of them. Visits and means are
        // sums over these, and Validate holds the two to each other.
        //
        // The SearchRollout* arrays run parallel, one entry per rollout, in an order the entries
        // do not repeat because two short arrays imply it: round by round; inside a round, the
        // candidates that played it in ascending candidate position; inside a candidate, world by
        // world. Candidate c played round r when SearchCandidateDroppedRound[c] is -1 (the
        // survivor) or at least r. Round r ran SearchRoundWorlds[r] worlds, numbered on from the
        // rounds before it, and every candidate of a round played the same worlds. World w's
        // random stream is seeded SearchDecisionSeed + (w + 1) * 0x9E3779B97F4A7C15 mod 2^64.
        //
        // A value is null where the world could not be built or played, and the health, armor,
        // plies and turn beside it are then zero and mean nothing. Health and armor are the two
        // heroes' at the rollout's last position, Own being the searching seat. Plies counts
        // moves after the candidate. SearchRolloutCapped lists the positions that stopped at the
        // ply cap (value 0, game unfinished), ascending; it is almost always empty.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ulong? SearchDecisionSeed = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SearchRootValue = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchCandidateDroppedRound = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRoundWorlds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? SearchNullRollouts = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<double?>? SearchRolloutValues = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutOwnHealth = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutOwnArmor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutOpponentHealth = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutOpponentArmor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutPlies = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutEndTurn = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<int>? SearchRolloutCapped = null);
}
