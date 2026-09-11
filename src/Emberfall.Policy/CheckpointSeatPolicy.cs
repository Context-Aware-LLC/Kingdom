using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Training.Corpus;

namespace Emberfall.Policy
{
    public enum CheckpointSelectionMode
    {
        Argmax,
        Sample,
        Mixture,

        /// <summary>
        /// Choose by the CRITIC, not the policy head: apply each distinct play, score the
        /// resulting position, take the best. Deterministic, and it uses the same weights as
        /// <see cref="Argmax"/> — only the head that decides changes.
        /// </summary>
        ValueGreedy,
    }

    /// <summary>How a checkpoint policy converts legal-action logits into one action.</summary>
    public readonly record struct CheckpointSelection(
        CheckpointSelectionMode Mode,
        double Temperature,
        ulong Seed,
        double Epsilon = 0.0,
        int MaxPlays = 0,
        bool EnforceLethal = false)
    {
        /// <summary>
        /// Restrict selection to actions that win the game outright, whenever any exists. A
        /// property of the SELECTION, not of the sampler, so it composes with argmax, temperature
        /// and mixture alike.
        /// </summary>
        public CheckpointSelection WithLethalInvariant() => this with { EnforceLethal = true };

        public static CheckpointSelection Argmax =>
            new CheckpointSelection(CheckpointSelectionMode.Argmax, 1.0, 0UL);

        public static CheckpointSelection Sample(double temperature, ulong seed)
        {
            if (!double.IsFinite(temperature) || temperature <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(temperature), temperature, "Sampling temperature must be finite and positive.");
            return new CheckpointSelection(CheckpointSelectionMode.Sample, temperature, seed);
        }

        /// <summary>
        /// One-ply value-greedy. <paramref name="maxPlays"/> caps how many distinct plays get an
        /// afterstate evaluation, because each one costs a scorer call inside the decision budget;
        /// the plays are taken in policy order, so the cap trims the tail the policy already
        /// dislikes rather than an arbitrary slice.
        /// </summary>
        public static CheckpointSelection ValueGreedy(int maxPlays)
        {
            if (maxPlays < 2)
                throw new ArgumentOutOfRangeException(
                    nameof(maxPlays), maxPlays, "Value-greedy needs at least two plays to compare.");
            return new CheckpointSelection(CheckpointSelectionMode.ValueGreedy, 1.0, 0UL, 0.0, maxPlays);
        }

        /// <summary>
        /// Mix the tempered policy with uniform exploration over DISTINCT PLAYS. Temperature is
        /// multiplicative and cannot touch a logit gap near 27, which is what cross-entropy against
        /// a deterministic script produced here; an additive floor can.
        /// </summary>
        public static CheckpointSelection Mixture(double temperature, double epsilon, ulong seed)
        {
            if (!double.IsFinite(temperature) || temperature <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(temperature), temperature, "Sampling temperature must be finite and positive.");
            if (!double.IsFinite(epsilon) || epsilon <= 0.0 || epsilon >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(epsilon), epsilon, "Mixture epsilon must be finite and in (0, 1).");
            return new CheckpointSelection(CheckpointSelectionMode.Mixture, temperature, seed, epsilon);
        }
    }

    /// <summary>
    /// Checkpoint-backed seat policy. It enumerates the authoritative legal set, serializes the
    /// exact projected observation/action wire shapes used by corpus export, asks the warm Python
    /// scorer for logits, and performs reproducible selection in-process.
    /// </summary>
    public sealed class CheckpointSeatPolicy : IObservedSeatPolicy
    {
        private readonly object _memoryGate = new object();
        private readonly ICheckpointScorer _scorer;
        private readonly LegalActionEnumerator _legal;
        private readonly CheckpointSelection _selection;
        private readonly List<int> _remainingDeck;
        private readonly string _checkpointSha256;
        private readonly string _identity;
        private readonly ICardDatabase _cards;
        private int _lethalDecisions;
        private int _lookaheadMisses;
        private int _lookaheadFallbacks;
        private int _lookaheadEvaluated;
        private bool _memoryInitialized;
        private PlayerId? _player;
        private KnownOpponentHandTracker? _opponentHand;
        private ulong _randomState;

        public CheckpointSeatPolicy(
            CheckpointScorerProcess scorer,
            ICardDatabase db,
            ICommandValidator validator,
            IReadOnlyList<int> startingDeck,
            CheckpointSelection selection)
            : this(scorer, db, validator, startingDeck, selection, scorer.CheckpointSha256)
        {
        }

        internal CheckpointSeatPolicy(
            ICheckpointScorer scorer,
            ICardDatabase db,
            ICommandValidator validator,
            IReadOnlyList<int> startingDeck,
            CheckpointSelection selection,
            string checkpointSha256)
        {
            _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
            _cards = db ?? throw new ArgumentNullException(nameof(db));
            _legal = new LegalActionEnumerator(
                db ?? throw new ArgumentNullException(nameof(db)),
                validator ?? throw new ArgumentNullException(nameof(validator)));
            if (startingDeck is null) throw new ArgumentNullException(nameof(startingDeck));
            if (string.IsNullOrWhiteSpace(checkpointSha256))
                throw new ArgumentException("Checkpoint SHA must not be empty.", nameof(checkpointSha256));

            _selection = selection;
            if (selection.Mode != CheckpointSelectionMode.Argmax
                && (!double.IsFinite(selection.Temperature) || selection.Temperature <= 0.0))
                throw new ArgumentOutOfRangeException(nameof(selection), "Sampling temperature must be finite and positive.");
            if (selection.Mode == CheckpointSelectionMode.Mixture
                && (!double.IsFinite(selection.Epsilon) || selection.Epsilon <= 0.0 || selection.Epsilon >= 1.0))
                throw new ArgumentOutOfRangeException(nameof(selection), "Mixture epsilon must be finite and in (0, 1).");

            _remainingDeck = new List<int>(startingDeck);
            _remainingDeck.Sort();
            _randomState = selection.Seed;
            string sha = checkpointSha256.ToLowerInvariant();
            _checkpointSha256 = sha;
            string t = selection.Temperature.ToString("R", CultureInfo.InvariantCulture);
            string lethal = selection.EnforceLethal ? ":lethal" : "";
            _identity = selection.Mode switch
            {
                CheckpointSelectionMode.Argmax => $"checkpoint:{sha}:argmax",
                // The identity string lands in episodes.jsonl and is how a corpus says what
                // generated it. A mixture that recorded only a temperature would read as a plain
                // temperature-sampled corpus and be pooled with one.
                CheckpointSelectionMode.Mixture =>
                    $"checkpoint:{sha}:mixture:t={t}"
                    + $":eps={selection.Epsilon.ToString("R", CultureInfo.InvariantCulture)}"
                    + $":playkey=v{DistinctPlayKey.SchemaVersion}:seed={selection.Seed}",
                // Without its own arm this fell through to the sample string and a value-greedy
                // arena would have reported itself as temperature sampling at t=1, seed 0.
                CheckpointSelectionMode.ValueGreedy =>
                    $"checkpoint:{sha}:value-greedy:plays={selection.MaxPlays}"
                    + $":playkey=v{DistinctPlayKey.SchemaVersion}",
                _ => $"checkpoint:{sha}:sample:t={t}:seed={selection.Seed}",
            } + lethal;
        }

        public string Identity => _identity;

        /// <summary>
        /// Value-greedy accounting, so a run cannot quietly have been an argmax run. Misses are
        /// candidates whose lookahead could not resolve; fallbacks are whole decisions that had to
        /// fall back to the policy head.
        /// </summary>
        internal (int Evaluated, int Fallbacks, int Misses) LookaheadCounters
            => (_lookaheadEvaluated, _lookaheadFallbacks, _lookaheadMisses);

        /// <summary>How many decisions the lethal invariant actually constrained. A run that
        /// reports zero did not exercise it, whatever its identity string claims.</summary>
        internal int LethalDecisions => _lethalDecisions;
        internal CheckpointDecisionTrace? LastDecision { get; private set; }

        public async ValueTask<PlayerIntent?> ChooseAsync(
            GameState state,
            PlayerId player,
            CancellationToken cancellationToken = default)
        {
            if (state is null) throw new ArgumentNullException(nameof(state));
            LastDecision = null;
            // Runners poll both seats. Concession is legal even while waiting, so enumerating
            // before checking decision ownership forces an idle seat to choose its only action:
            // concede. Pending choices may belong to the inactive player and take precedence.
            if (state.Outcome != MatchOutcome.InProgress) return null;
            if (state.PendingChoice is PendingChoice pending)
            {
                if (pending.Chooser != player) return null;
            }
            else if (state.Phase == TurnPhase.Mulligan)
            {
                bool done = state.SeatOf(player) == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                if (done) return null;
            }
            else if (state.Phase != TurnPhase.Main || state.ActivePlayer != player) return null;

            PlayerMemory memory;
            lock (_memoryGate)
            {
                EnsurePlayer(player);
                UpdatePlayerMemory(state, player);
                PlayerState self = state.PlayerOf(player);
                PlayerState opponent = state.Players[state.SeatOf(player) == 0 ? 1 : 0];
                memory = new PlayerMemory(
                    _remainingDeck.ToArray(),
                    SortedDefinitions(self.Graveyard),
                    SortedDefinitions(opponent.Graveyard),
                    _opponentHand!.Snapshot());
            }

            IReadOnlyList<PlayerIntent> actions = _legal.Enumerate(state, player, includeConcede: false);
            if (actions.Count == 0) return null;

            if (_selection.EnforceLethal)
            {
                IReadOnlyList<PlayerIntent> winning = ImmediatelyWinning(actions, state, player);
                if (winning.Count > 0)
                {
                    // Sam's ruling: ending the game is a deterministic invariant, not a
                    // preference the network has to rediscover from terminal returns. Every
                    // non-winning action here is dominated, so the policy chooses among the
                    // winners or not at all.
                    actions = winning;
                    _lethalDecisions++;
                }
            }

            JsonElement observation = EncodeObservation(state);
            IReadOnlyList<JsonElement> legalActions = EncodeActions(actions, state.Id);

            PolicyScore score = await _scorer.ScoreAsync(
                observation, memory, legalActions, cancellationToken).ConfigureAwait(false);
            if (score.Logits.Count != actions.Count)
                throw new InvalidOperationException(
                    $"Checkpoint scorer returned {score.Logits.Count} logits for {actions.Count} legal actions.");

            SelectionResult chosen = _selection.Mode switch
            {
                CheckpointSelectionMode.Argmax =>
                    new SelectionResult(SelectArgmax(score.Logits), 0.0, actions.Count),
                CheckpointSelectionMode.Mixture =>
                    SelectMixture(score.Logits, actions, state, _selection.Temperature, _selection.Epsilon),
                CheckpointSelectionMode.ValueGreedy =>
                    await SelectValueGreedyAsync(
                        score.Logits, actions, state, memory, _selection.MaxPlays, cancellationToken)
                        .ConfigureAwait(false),
                _ => SelectTemperature(score.Logits, _selection.Temperature),
            };
            LastDecision = new CheckpointDecisionTrace(
                _checkpointSha256,
                player.Value,
                state.StateVersion,
                actions.Count,
                chosen.Index,
                chosen.LogProbability,
                score.Value,
                _selection.Temperature,
                SamplerMode: SamplerModeName(_selection.Mode),
                Epsilon: _selection.Mode == CheckpointSelectionMode.Mixture ? _selection.Epsilon : null,
                DistinctPlayCount: chosen.DistinctPlayCount,
                PlayKeySchemaVersion: _selection.Mode == CheckpointSelectionMode.Mixture
                    ? DistinctPlayKey.SchemaVersion
                    : null,
                PlayGroups: chosen.PlayGroups);
            return actions[chosen.Index];
        }

        public void ObserveTransition(
            GameState before,
            IReadOnlyList<GameEvent> events,
            GameState after,
            PlayerId player)
        {
            if (before is null) throw new ArgumentNullException(nameof(before));
            if (events is null) throw new ArgumentNullException(nameof(events));
            if (after is null) throw new ArgumentNullException(nameof(after));

            lock (_memoryGate)
            {
                EnsurePlayer(player);
                UpdatePlayerMemory(before, player);
                PlayerState ownBefore = before.PlayerOf(player);
                PlayerState ownAfter = after.PlayerOf(player);
                bool wasDone = before.SeatOf(player) == 0 ? before.Mulligan0Done : before.Mulligan1Done;
                bool isDone = after.SeatOf(player) == 0 ? after.Mulligan0Done : after.Mulligan1Done;
                if (before.Phase == TurnPhase.Mulligan && !wasDone && isDone)
                {
                    // Replacements are set aside and then shuffled back with new instance IDs.
                    foreach (CardInstance card in ownBefore.Hand)
                        if (FindCard(ownAfter.Hand, card.Id) == null)
                            _remainingDeck.Add(card.DefinitionId);
                }
                foreach (GameEvent e in events)
                {
                    if (e.Kind != GameEventKind.CardDrawn || !e.Source.HasValue
                        || e.Source.Value.Value == 0) continue;
                    // Draw events identify the actual card. A hand diff cannot distinguish it
                    // from a generated card, and misses overdraw cards sent to the graveyard.
                    CardInstance? drawn = FindCard(ownAfter.Hand, e.Source.Value)
                        ?? FindCard(ownAfter.Graveyard, e.Source.Value)
                        ?? FindCard(ownAfter.Board, e.Source.Value);
                    if (drawn == null) continue; // opponent draw, not our deck
                    if (!_remainingDeck.Remove(drawn.DefinitionId))
                        throw new InvalidOperationException("Drawn card is absent from checkpoint deck memory.");
                }
                _remainingDeck.Sort();
                RequireDeckCount(ownAfter);
                _opponentHand!.Observe(
                    before,
                    events,
                    after,
                    PubliclyPlayedDefinitions(after, events, player));
            }
        }

        private void EnsurePlayer(PlayerId player)
        {
            if (!_player.HasValue)
            {
                _player = player;
                _opponentHand = new KnownOpponentHandTracker(player);
                return;
            }
            if (_player.Value != player)
                throw new InvalidOperationException(
                    $"Checkpoint policy is already bound to player '{_player.Value.Value}', not '{player.Value}'.");
        }

        private void UpdatePlayerMemory(GameState state, PlayerId player)
        {
            PlayerState self = state.PlayerOf(player);
            if (!_memoryInitialized)
            {
                int drawnFromDeck = Math.Max(0, _remainingDeck.Count - self.Deck.Count);
                var definitions = new List<int>();
                foreach (CardInstance card in self.Hand)
                    if (card.Id.Value != 0) definitions.Add(card.DefinitionId);
                definitions.Sort();
                RemoveDefinitions(definitions, drawnFromDeck);
                _memoryInitialized = true;
            }
            RequireDeckCount(self);
        }

        private void RequireDeckCount(PlayerState self)
        {
            if (_remainingDeck.Count != self.Deck.Count)
                throw new InvalidOperationException(
                    $"Checkpoint deck memory count {_remainingDeck.Count} differs from public deck count {self.Deck.Count}; transition tracking is incomplete.");
        }

        private static CardInstance? FindCard(IReadOnlyList<CardInstance> cards, InstanceId id)
        {
            foreach (CardInstance card in cards)
                if (card.Id == id) return card;
            return null;
        }

        private void RemoveDefinitions(IReadOnlyList<int> definitions, int limit)
        {
            int removed = 0;
            foreach (int definition in definitions)
            {
                if (removed >= limit) break;
                if (_remainingDeck.Remove(definition)) removed++;
            }
        }

        /// <summary>
        /// The index that was drawn, the log probability of drawing it UNDER THE DISTRIBUTION THE
        /// DRAW ACTUALLY USED, and how many distinct plays that distribution spanned.
        ///
        /// One value, one code path. Sampling and the recorded probability used to be two
        /// independent functions that happened to agree because both were a plain softmax; a
        /// mixture makes them disagree, silently, and the trainer's importance ratio has no way to
        /// notice. Every ratio in the run would be wrong by a factor nothing logs.
        /// </summary>
        private readonly record struct SelectionResult(
            int Index,
            double LogProbability,
            int DistinctPlayCount,
            IReadOnlyList<int>? PlayGroups = null);

        private SelectionResult SelectTemperature(IReadOnlyList<double> logits, double temperature)
        {
            double[] probabilities = Softmax(logits, temperature);
            int index = Draw(probabilities);
            return new SelectionResult(index, Math.Log(probabilities[index]), logits.Count);
        }

        /// <summary>
        /// Draw from mu = (1-eps)*pi + eps*Uniform(DISTINCT PLAYS), and return the exact per-index
        /// probability mu(i) that the draw used.
        ///
        /// Exploration is uniform over plays rather than over raw indices, so a play does not
        /// collect more exploration for having more spellings — which is what mixing over indices
        /// would do, and it is not a property of the game. Within the chosen play the model keeps
        /// its own preference among aliases (model-conditional routing), so:
        ///
        ///     mu(i in P) = (1-eps)*pi(i) + (eps/K) * pi(i)/pi(P)
        ///
        /// Canonical-index routing — putting all of a play's eps/K on one member — is a DIFFERENT
        /// and equally valid policy. The two are indistinguishable downstream and bias every
        /// importance ratio differently, so the choice is made here explicitly rather than falling
        /// out of an implementation detail. See
        /// decisions/emberfall-mixture-behavior-probability-and-play-key-contract-20260909.
        /// </summary>
        private SelectionResult SelectMixture(
            IReadOnlyList<double> logits,
            IReadOnlyList<PlayerIntent> actions,
            GameState state,
            double temperature,
            double epsilon)
        {
            double[] pi = Softmax(logits, temperature);
            var playKeys = new string[actions.Count];
            for (int i = 0; i < actions.Count; i++) playKeys[i] = DistinctPlayKey.Of(actions[i], state);

            double[] mu = MixtureProbabilities(
                pi, playKeys, epsilon, out int distinctPlays, out int[] groups);
            int index = Draw(mu);
            return new SelectionResult(index, Math.Log(mu[index]), distinctPlays, groups);
        }

        /// <summary>
        /// The per-index mixture distribution. Split out so the algebra can be tested without a
        /// board: the properties that matter here — mass sums to one, a play's total is exactly
        /// (1-eps)*pi(P) + eps/K, and duplicating a spelling does not change a play's exploration
        /// share — are claims about this function, not about any particular game state.
        /// </summary>
        internal static double[] MixtureProbabilities(
            IReadOnlyList<double> pi,
            IReadOnlyList<string> playKeys,
            double epsilon,
            out int distinctPlays)
            => MixtureProbabilities(pi, playKeys, epsilon, out distinctPlays, out _);

        /// <summary>
        /// As above, and also hands back which play each action index belongs to.
        ///
        /// THE GROUPS HAVE TO BE PERSISTED OR THE RECEIPT IS UNVERIFIABLE. Recomputing
        /// `mu(i) = (1-eps)*pi(i) + (eps/K)*pi(i)/pi(P)` needs `pi(P)`, and `pi(P)` needs to know
        /// which indices share a play. Recording epsilon and K alone is not enough: an independent
        /// verifier would have to re-derive the grouping, which means reimplementing
        /// <see cref="DistinctPlayKey"/> in Python — a second implementation of the rule, which
        /// will eventually disagree with this one about what a play is, silently, in the
        /// denominator of every importance ratio.
        /// </summary>
        internal static double[] MixtureProbabilities(
            IReadOnlyList<double> pi,
            IReadOnlyList<string> playKeys,
            double epsilon,
            out int distinctPlays,
            out int[] groups)
        {
            if (pi.Count != playKeys.Count)
                throw new ArgumentException("Every action needs exactly one play key.", nameof(playKeys));

            var playOf = new int[pi.Count];
            var byKey = new Dictionary<string, int>(pi.Count, StringComparer.Ordinal);
            var playMass = new List<double>();
            var playSize = new List<int>();
            for (int i = 0; i < pi.Count; i++)
            {
                if (!byKey.TryGetValue(playKeys[i], out int play))
                {
                    play = playMass.Count;
                    byKey.Add(playKeys[i], play);
                    playMass.Add(0.0);
                    playSize.Add(0);
                }
                playOf[i] = play;
                playMass[play] += pi[i];
                playSize[play]++;
            }

            distinctPlays = playMass.Count;
            groups = playOf;
            double exploration = epsilon / distinctPlays;
            var mu = new double[pi.Count];
            for (int i = 0; i < pi.Count; i++)
            {
                int play = playOf[i];
                double mass = playMass[play];
                // pi(P) underflows to zero on a saturated policy — which is the whole reason this
                // sampler exists, so it is the common case here rather than an edge case. The
                // conditional pi(i)/pi(P) is undefined there, so spread the play's exploration mass
                // evenly across its spellings. Because the draw and the recorded probability read
                // this same array, the fallback cannot desynchronise them.
                double within = mass > 0.0 ? pi[i] / mass : 1.0 / playSize[play];
                mu[i] = (1.0 - epsilon) * pi[i] + exploration * within;
            }
            return mu;
        }

        /// <summary>
        /// Pick the play whose resulting position the CRITIC likes best, with the policy head
        /// demoted to deciding which plays are worth the lookahead and breaking ties.
        ///
        /// Falls back to policy argmax when nothing could be evaluated — a seat that cannot look
        /// ahead must still play, and playing the policy's choice is the honest degradation. The
        /// fallback is counted so a run cannot silently be an argmax run wearing this name.
        /// </summary>
        private async Task<SelectionResult> SelectValueGreedyAsync(
            IReadOnlyList<double> logits,
            IReadOnlyList<PlayerIntent> actions,
            GameState state,
            object memory,
            int maxPlays,
            CancellationToken cancellationToken)
        {
            double[] pi = Softmax(logits, 1.0);
            var byKey = new Dictionary<string, int>(actions.Count, StringComparer.Ordinal);
            var representatives = new List<int>();
            for (int i = 0; i < actions.Count; i++)
            {
                string key = DistinctPlayKey.Of(actions[i], state);
                if (!byKey.TryGetValue(key, out int at)) { byKey[key] = representatives.Count; representatives.Add(i); }
                else if (pi[i] > pi[representatives[at]]) representatives[at] = i;
            }
            int distinctPlays = representatives.Count;
            representatives.Sort((a, b) => pi[b].CompareTo(pi[a]));
            int budget = Math.Min(maxPlays <= 0 ? distinctPlays : maxPlays, distinctPlays);

            int best = -1;
            double bestValue = double.NegativeInfinity;
            for (int slot = 0; slot < budget; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = representatives[slot];
                AfterstateLookahead.Afterstate? after =
                    AfterstateLookahead.Apply(state, actions[index], state.Id, _cards);
                if (after is null) { _lookaheadMisses++; continue; }

                double value;
                if (after.Value.Terminal)
                {
                    // Ground truth beats an estimate. A play that ends the game in our favour is
                    // worth exactly +1 and no critic opinion improves on that.
                    value = AfterstateLookahead.TerminalValue(after.Value.State, _player!.Value);
                }
                else
                {
                    // A drawn card from this seat's own redacted deck lands in its own hand as a
                    // blank, which the encoder refuses and which never occurs in training. Skip
                    // rather than score an out-of-distribution position.
                    if (AfterstateLookahead.HasRedactedVisibleEntity(after.Value.State, _player!.Value))
                    {
                        _lookaheadMisses++;
                        continue;
                    }
                    IReadOnlyList<PlayerIntent> next =
                        _legal.Enumerate(after.Value.State, _player!.Value, includeConcede: false);
                    if (next.Count == 0) next = new PlayerIntent[] { new EndTurnIntent(_player!.Value) };
                    PolicyScore reply;
                    try
                    {
                        reply = await _scorer.ScoreAsync(
                            EncodeObservation(after.Value.State),
                            memory,
                            EncodeActions(next, after.Value.State.Id),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // A scorer-reported rejection does NOT fault the process — only transport
                        // failures do — so one unencodable candidate must not take down the match.
                        // Anything the guard above did not anticipate lands here and is counted.
                        _lookaheadMisses++;
                        continue;
                    }
                    value = reply.Value;
                }
                // Strict improvement only, so ties fall to the earlier slot — which is the
                // higher-policy-probability play. Deterministic, and it keeps the policy as the
                // tie-break rather than letting float noise choose.
                if (value > bestValue) { bestValue = value; best = index; }
            }

            if (best < 0)
            {
                _lookaheadFallbacks++;
                return new SelectionResult(SelectArgmax(logits), 0.0, distinctPlays);
            }
            _lookaheadEvaluated++;
            return new SelectionResult(best, 0.0, distinctPlays);
        }

        /// <summary>
        /// The legal actions that end the game in this seat's favour right now.
        ///
        /// Cheap in the way that matters: it applies each candidate through the engine but never
        /// calls the scorer, so it costs nothing in inference and is not comparable to the
        /// value-greedy lookahead that runs a network per candidate.
        ///
        /// It also sidesteps the redacted-draw problem entirely. That limit is the ENCODER
        /// refusing a blank card in the actor's own hand; asking whether `Outcome` moved off
        /// `InProgress` needs no encoding at all, so a lethal that involves drawing is still
        /// detected.
        ///
        /// A candidate whose application fails is skipped rather than treated as non-winning,
        /// which is the safe direction: the worst case is missing a lethal and playing normally.
        /// </summary>
        private IReadOnlyList<PlayerIntent> ImmediatelyWinning(
            IReadOnlyList<PlayerIntent> actions, GameState state, PlayerId player)
        {
            var winning = new List<PlayerIntent>();
            foreach (PlayerIntent action in actions)
            {
                AfterstateLookahead.Afterstate? after =
                    AfterstateLookahead.Apply(state, action, state.Id, _cards);
                if (after is null || !after.Value.Terminal) continue;
                if (AfterstateLookahead.TerminalValue(after.Value.State, player) > 0.0)
                    winning.Add(action);
            }
            return winning;
        }

        private static double[] Softmax(IReadOnlyList<double> logits, double temperature)
        {
            if (logits.Count == 0) throw new InvalidOperationException("Checkpoint scorer returned no logits.");
            double max = double.NegativeInfinity;
            foreach (double logit in logits)
            {
                if (!double.IsFinite(logit))
                    throw new InvalidOperationException("Checkpoint scorer returned a non-finite logit.");
                if (logit > max) max = logit;
            }

            var weights = new double[logits.Count];
            double total = 0.0;
            for (int i = 0; i < logits.Count; i++)
            {
                weights[i] = Math.Exp((logits[i] - max) / temperature);
                total += weights[i];
            }
            if (!double.IsFinite(total) || total <= 0.0)
                throw new InvalidOperationException("Checkpoint logits did not produce a valid sampling distribution.");
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;
            return weights;
        }

        private int Draw(IReadOnlyList<double> probabilities)
        {
            double draw = NextUnitDouble();
            double cumulative = 0.0;
            for (int i = 0; i < probabilities.Count; i++)
            {
                cumulative += probabilities[i];
                if (draw < cumulative) return i;
            }
            // Floating-point tail: the cumulative sum can land a hair under 1.0.
            for (int i = probabilities.Count - 1; i >= 0; i--)
                if (probabilities[i] > 0.0) return i;
            throw new InvalidOperationException("Checkpoint logits did not produce a valid sampling distribution.");
        }

        private static string SamplerModeName(CheckpointSelectionMode mode) => mode switch
        {
            CheckpointSelectionMode.Argmax => "argmax",
            CheckpointSelectionMode.Mixture => "mixture",
            _ => "temperature",
        };

        private double NextUnitDouble()
        {
            // SplitMix64: tiny, deterministic, and independent of System.Random (which is banned
            // server-side). The top 53 bits map exactly onto a double mantissa.
            _randomState += 0x9E3779B97F4A7C15UL;
            ulong z = _randomState;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / 9007199254740992.0);
        }

        private static int SelectArgmax(IReadOnlyList<double> logits)
        {
            if (logits.Count == 0) throw new InvalidOperationException("Checkpoint scorer returned no logits.");
            int best = 0;
            double bestValue = logits[0];
            if (!double.IsFinite(bestValue))
                throw new InvalidOperationException("Checkpoint scorer returned a non-finite logit.");
            for (int i = 1; i < logits.Count; i++)
            {
                double value = logits[i];
                if (!double.IsFinite(value))
                    throw new InvalidOperationException("Checkpoint scorer returned a non-finite logit.");
                if (value > bestValue)
                {
                    best = i;
                    bestValue = value;
                }
            }
            return best;
        }

        // The separate LogProbability() reconstruction was DELETED here, not left unused. It
        // recomputed the chosen action's probability from the logits on a path independent of the
        // draw, which is only correct while the sampler is a plain softmax. Keeping it would leave
        // a second, wrong answer sitting next to the right one for the next person to reach for.

        private static JsonElement EncodeObservation(GameState state)
        {
            byte[] envelope = ProtocolCodec.Encode(SnapshotMsg.Of(state), state.Id, 0UL);
            using JsonDocument document = JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("body").GetProperty("state").Clone();
        }

        private static IReadOnlyList<JsonElement> EncodeActions(
            IReadOnlyList<PlayerIntent> actions,
            MatchId match)
        {
            var result = new List<JsonElement>(actions.Count);
            foreach (PlayerIntent action in actions)
            {
                byte[] envelope = ProtocolCodec.Encode(new IntentMsg(0UL, action), match, 0UL);
                using JsonDocument document = JsonDocument.Parse(envelope);
                result.Add(document.RootElement.GetProperty("body").GetProperty("intent").Clone());
            }
            return result;
        }

        private static IReadOnlyList<int> SortedDefinitions(IReadOnlyList<CardInstance> cards)
        {
            var result = new List<int>(cards.Count);
            foreach (CardInstance card in cards) result.Add(card.DefinitionId);
            result.Sort();
            return result;
        }

        private static IReadOnlyList<int> PubliclyPlayedDefinitions(
            GameState projectedAfter,
            IReadOnlyList<GameEvent> events,
            PlayerId viewer)
        {
            PlayerState opponent = projectedAfter.Players[1 - projectedAfter.SeatOf(viewer)];
            var result = new List<int>();
            foreach (GameEvent gameEvent in events)
            {
                if (gameEvent.Kind != GameEventKind.CardPlayed
                    || !gameEvent.Source.HasValue
                    || gameEvent.Source.Value.Value == 0)
                    continue;
                CardInstance? card = FindPublicCard(opponent, gameEvent.Source.Value);
                if (card is not null) result.Add(card.DefinitionId);
            }
            return result;
        }

        private static CardInstance? FindPublicCard(PlayerState player, InstanceId id)
        {
            if (player.Weapon is not null && player.Weapon.Id == id) return player.Weapon;
            foreach (CardInstance card in player.Board)
                if (card.Id == id) return card;
            foreach (CardInstance card in player.Graveyard)
                if (card.Id == id) return card;
            return null;
        }

        internal sealed record PlayerMemory(
            IReadOnlyList<int> SelfRemainingDeck,
            IReadOnlyList<int> SelfGraveyard,
            IReadOnlyList<int> OpponentGraveyard,
            IReadOnlyList<int> KnownOpponentHand);
    }

    internal interface ICheckpointScorer
    {
        Task<PolicyScore> ScoreAsync(
            JsonElement observation,
            object playerMemory,
            IReadOnlyList<JsonElement> legalActions,
            CancellationToken cancellationToken);
    }

    internal sealed record PolicyScore(IReadOnlyList<double> Logits, double Value);

    /// <summary>
    /// Capture-time provenance for one decision.
    ///
    /// <paramref name="Temperature"/> ALONE NO LONGER DESCRIBES THE BEHAVIOR POLICY. A mixture is
    /// not a temperature, so a reader that sees only a scalar temperature would reconstruct the
    /// wrong distribution and be certain about it. <paramref name="SamplerMode"/>,
    /// <paramref name="Epsilon"/> and <paramref name="PlayKeySchemaVersion"/> are what make the
    /// recorded behavior probability reproducible.
    /// </summary>
    internal sealed record CheckpointDecisionTrace(
        string CheckpointSha256,
        string Actor,
        ulong StateVersion,
        int LegalActionCount,
        int SelectedActionIndex,
        double BehaviorLogProbability,
        double PredictedValue,
        double Temperature,
        bool AllowsConcede = false,
        string SamplerMode = "temperature",
        double? Epsilon = null,
        int? DistinctPlayCount = null,
        int? PlayKeySchemaVersion = null,
        IReadOnlyList<int>? PlayGroups = null);
}
