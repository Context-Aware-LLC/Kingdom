# Kingdom RL training — the whole arc to 2026-09-09

Written 2026-09-09 from the complete set of training pages in the brain (both the `default` and
`codex` shelves, readable together only since 2026-09-09 — see the partition note at the end).

The `work/` and `evidence/training-*` paths cited below live in the private training repository,
which left this one on 2026-11-04.

This is the **narrative and the conclusions**. Every number here is quoted from a brain page or an
evidence file; per-run identities, SHAs and artifact paths stay in those pages and are not copied
here. It replaces the old `docs/sam/rl-project.md`, which described generation 1 only and now sits
beside it as [`rl-training-generation-1.md`](rl-training-generation-1.md).

> **2026-10-05 — the Greedy in this document no longer exists. Read §14 first.** Every score,
> label and agreement figure below was measured against `greedy-ai.v2`. On 2026-10-05 the bot was
> changed to play better and relabelled `greedy-ai.v3`; v2 was removed from the code. Nothing below
> compares to a number measured after that date.

> **2026-09-10 — read §13 before you believe any arena score below.** Every "refusal" in this
> document is a 56-game score against Greedy, and that arena ran at `gamesPerSeat 1`: one seed per
> matchup. Measured on 2026-09-10, **24 of 28 matchups score exactly 50% regardless of the policy**,
> so a 26-30 headline is `24-24` plus four independent observations, and its interval spans 50%.
> The mechanical findings in §6 are measured off the corpus and are unaffected. The *strength*
> claims in §4, §5, §7 and §12 are not: the deficit they set out to explain was never established.

---

## 1. What we are actually building

A policy/value network that plays Kingdom well enough to be the game's bot. Not an explainable
tactician — Sam set that standard explicitly on 2026-09-09
(`decisions/emberfall-optimize-training-for-game-strength-not-perfect-tactics-20260909`):

> a game-playing bot, not a brain-surgeon bot

Promotion is decided **primarily by argmax game strength** against the parent and against Greedy AI.
Critic metrics, confidence calibration and human review are supporting diagnostics, never standalone
vetoes — and never standalone *approvals* either.

## 2. The machine that runs experiments

Built through August, proven repeatedly since. Every generation follows the same loop, and the loop
itself has not been the problem:

```
freeze cards → capture N training games → materialize corpus → export shards
   → ONE actor-critic update (fresh or continued optimizer)
   → fixed training gate
   → open the independently seeded reserved holdout EXACTLY ONCE
   → if holdout passes: fixed 112-game argmax arena vs parent + vs Greedy
   → promote or reject
```

Non-negotiables that have held all the way through:

- **Preregistration.** Seeds, offsets, hyperparameters, gates and stop rules are written to the brain
  *before* capture. No seed search, no retry, no tuning against a holdout result.
- **One-shot holdout.** Independently seeded, zero seed overlap, zero replay-hash overlap, opened
  once, atomically claimed.
- **A negative result is a completed result.** Four separate children have been correctly refused
  promotion. None of those runs is a failure.
- **Everything is hashed** — card digest, deck-pool digest, export manifest, checkpoint, arena
  report, annotation file.

## 3. Generation 1 (through 2026-09-05) — the loop works

Champion `af027b49…` promoted 2026-09-05 against card digest `3e168f24…`: beat its training parent
85-83 and the previous champion 86-82, all 2,688 measured games clean. Greedy was already the
weakness (78-90) and the margin was narrow.

The value of generation 1 was never the champion. It was proving live inference, a durable league,
mixed-opponent rollouts, optimizer continuation, atomic holdout claims and the promotion gate all
work end to end. Full record: [`rl-training-generation-1.md`](rl-training-generation-1.md) and
`evidence/training-benchmark/`.

## 4. Generation 2 (2026-09-07 → 09) — five experiments, five refusals

Sam froze the **current** card set rather than keep optimizing against a stale one
(`decisions/emberfall-current-card-set-is-the-next-training-freeze`): the balance matrix was good
enough, and remaining outliers were mostly Greedy's own bad play.

| # | Experiment | Critic | Arena vs Greedy | Verdict |
|---|---|---|---|---|
| 1 | current-cards pilot, 56 games, T=1 | train gate passed | — | holdout failed on MAE |
| 2 | current-cards step-560, T=1 | train gate passed | — | holdout failed on MAE |
| 3 | cohesive pilot, 56 games, T=1 | both gates passed | 26-30 | strength-neutral, not promoted |
| 4 | cohesive step-560, T=1 | both gates passed | 26-30 | strength-neutral, not promoted |
| 5 | cohesive pilot then **step-560, T=4** | both gates passed, big EV gains | 26-30 | not promoted |

**26-30 against Greedy, five times, on the same fixed arena seeds.** The supervised parent scored it
first. Every child since has reproduced it exactly. In experiment 5, 51 of 56 complete action traces
were byte-identical to the parent's, and not one of the five differences changed a game outcome.

Two supporting builds landed in the middle of this:

- **CUDA.** Pinned env `emberfall-training-py314-cu130`, RTX 5070 Ti. First qualification narrowly
  failed on `probabilityRatio.maximum` (1.30e-4 vs a 1e-4 ceiling), so CPU stayed trusted. A second
  qualification against the T4 pilot passed, and the step-560 optimization ran on GPU in 53m42s.
  Operational rule: `--behavior-verification-device cpu --device cuda`.
- **Cohesive decks.** 56 hand-built strategically coherent decks (both hero-power orientations of all
  28 class pairings) replaced generated pools, so self-play stopped being played with incoherent
  30-card piles.

### The diagnosis that explained all of it

`notes/emberfall-generation-2-step560-policy-saturation-diagnosis-20260908`, on the 58,016-decision
training population:

- the temperature-1 behavior sample matched the parent's argmax on **99.50%** of rows
- median top-action probability at genuine choice points: **0.9999995**
- median top-two logit gap: **14.68**; mean **50.73**
- of 41,015 rows with a gap ≥ 5, **zero** flipped after the update

The critic had abundant signal and improved. The actor had almost no gradient, because it almost
never sampled anything but its own argmax. **We were re-labelling the same decisions.**

## 5. The exploration attempt, and why it did not work either

Temperature 4 was the conservative first intervention. It worked *as an acquisition method* and
failed to move strength:

- raw non-argmax rate rose from 0.66% to **9.77%** — the preregistered 5% gate passed
- critic improved materially on training **and** the sealed holdout (holdout EV 0.217 → 0.400,
  calibration error 0.267 → 0.045)
- arena: 27-29 vs parent, **26-30 vs Greedy**

Then the alias collapse landed and took most of that acquisition away
(`notes/emberfall-t4-normalized-entropy-vs-distinct-novelty-result-20260909`):

> 2,195 of the 4,202 alternate-index samples — **52.24%** — collapse to the *same play*.
> Genuine novelty is **4.67%**, not 9.77%.

Because the action list spells one play several ways: the same card in board slot 0 vs slot 1, two
copies of a card, interchangeable tokens, different orderings of the same complete turn. Sampling a
"different action" often meant sampling the identical decision.

## 6. The two findings that survive everything above

### 6a. Confidence was never the defect

The visual decision-review tool (Kingdom-scoped by decision, built 2026-09-08/09) let Sam grade
real board states instead of text. 30 near-certain decisions, every one at p ≥ 0.99:

```
  forced (1 play)        :   0    0.0%      <- not one
  binary (2 plays)       :   8   26.7%
  real choice (3+ plays) :  22   73.3%

                           n  good play  bad play   conf unwarranted
  2 plays                  8          8         0                  0
  3+ plays                22         17         5                  8
```

**25 of 30 good plays. 22 of 30 warranted near-certainty.** Sam:

> even though it does some inexplicable bonehead plays sometimes, overwhelmingly when there is any
> level of ambiguity about what decision to make it seems to confidently select the optimal

This **retired "high confidence" as a promotion blocker**. The bonehead plays are real — 5 of 30 —
but confidence does not predict them. A display bug had been merging Unearth choices into one row
(fixed in `b432be31`); it biased the tool toward "it had no choice", which is the exact conclusion
the tool exists to test. Two of the three re-graded states turned out to be bad plays.

The **naming constraint** from `decisions/emberfall-decision-aware-confidence-in-standard-results`
governs how any of this may be written up: structural filtering alone measures confidence
*concentration*, not correctness. Say "overconfidence" only where a reasonableness label exists.

### 6b. Terminal return cannot tell a good move from a bad one in a won game

The T4 child review (30 mixed-confidence frames, 2026-09-09) found the mirror problem —
**under**confidence: five score-5 plays sat at 50-58% confidence. And then the mechanism, in
`notes/emberfall-terminal-return-credit-assignment-obstacle-20260909`:

Advantage is `episode_return − behavior_predicted_value`, γ=1. So:

- Sample 6, `Play The Spark`, graded **1 of 5** by Sam. Actor won. Behavior value +0.92.
  **Advantage +0.08 — reinforced.**
- Sample 13, `Marked for Death` on Void Shriek, graded **1 of 5**. Actor won. Behavior value +0.97.
  **Advantage +0.03 — reinforced.**
- Sample 1: opponent at 5 health, ready 6-attack and 7-attack minions on board. The child gave both
  face attacks ~zero probability and cast Lightning Bolt on a minion at 62% instead. It won anyway.
  Behavior value +0.4046, so that action collected **+0.5954 advantage.**

Every action by the winner gets +1. Every action by the loser gets −1. A lethal move gets no special
label. This is not Greedy imitation leaking through — the objective has no Greedy target in it any
more. It is that terminal outcome is too diffuse to assign blame at this data volume.

Sam's ruling (`decisions/emberfall-policy-must-take-immediate-guaranteed-lethal-20260909`):
**immediate guaranteed lethal is a deterministic game invariant, not something the network should
rediscover.** Simulate each already-enumerated legal action; if any wins immediately, restrict
selection to those. The follow-on curriculum — afterstate features, distance-to-lethal labels,
bounded search distilled into the policy — is sketched in
`notes/emberfall-lethal-horizon-curriculum-for-learned-planning-20260909`, with the warning not to
assume lethal-in-1 extrapolates to lethal-in-4.

## 7. Where it stands, 2026-09-09 evening

**Active parent: the supervised generation-2 baseline `765fdcc4…`.** Nothing has beaten it. Four
children evaluated, four refused, all correctly.

Measured on the T4 step-560 corpus, 56,805 decisions, `behaviorTemperature = 4` on every row:

| metric | value |
|---|---|
| effective actions `1/Σp²`, all | 1.13 |
| effective actions, excluding forced | **1.18** |
| p(chosen) > 0.999 | 53% |
| forced decisions (N=1) | 24.8% |
| critic explained variance | +0.226 |
| critic sign agreement | 73.9% |
| \|V\|>0.8 rows (56.8% of all), wrong sign | 15.2% |

**At temperature 4 the policy behaves as though it has 1.18 options.** A p(chosen) of 0.999 that
survived division by 4 implies a raw logit gap near 27 — p ≈ 1−10⁻¹² at T=1. Temperature is
multiplicative and **cannot touch a gap that large.**

Root cause: cross-entropy against a deterministic greedy script has **no fixed point** — it rewards a
wider logit gap without bound. Policy gradients then cannot undo it, because an action's gradient
scales with how often it is sampled. Self-sealing.

The critic is not uniformly weak, it is weak *early*: EV 0.087 on turns 0-5, rising to 0.34 at turn
40+. Bootstrapping (GAE) into that early critic would inject bias, which is why it is a separate
later test and not a free win.

### A false lead, recorded so nobody repeats it

An earlier pass reported the critic at **negative** explained variance. That read
`review-bundles/step560-larger-30-20260908` — 30 stratified frames, a taxonomy-discovery sample —
as if it were the corpus. Real value is **+0.226**.

## 8. The decided next move

`decisions/emberfall-mixture-sampling-pilot-eps-015` and
`decisions/emberfall-mixture-behavior-probability-and-play-key-contract-20260909`:

A **56-game, training-only mixture-sampling pilot at ε = 0.15**, mixing over **distinct plays, not
raw action indices**. No optimizer change. No holdout. Acquisition only.

Sam settled the two silent-correctness hazards himself:

**Routing.** Model-conditional within the selected play. Sample a play from
`μ(P) = (1−ε)·π(P) + ε/K`, then an index within it from `π(i)/π(P)`, giving

```
μ(i) = (1−ε)·π(i) + (ε/K)·π(i)/π(P)
```

Exploration is uniform over *plays* — so a play does not get more exploration for having more
spellings — while the model keeps its preference among aliases.

**One source of truth for μ.** The exact per-index probability must be computed and persisted **by
the same code path that draws the index**. Never reconstructed later. `behaviorLogProbability` gets
`log(μ(i))` straight from the sampling result. A scalar `behaviorTemperature` no longer describes
the behavior policy; the receipt must carry sampler mode, ε, routing rule, distinct-play key schema
version and the executed probability.

**Play keys normalize the wire target.** `"i2"` and `{"instance":"i97","binding":"target"}` are one
target in two spellings. Not theoretical: the current corpus has 113,490 bare-string and 2,653 object
target occurrences, and **381 decisions carry the same instance in both shapes**.

### The measurement trap, flagged at decision time

Both stated success criteria are **partly mechanical**. For a point-mass π, ε=0.15 forces effective
branching to ~1.16 (K=2) … 1.36 (K=20) *before the policy learns anything*, against a current 1.18.
So "branching rose to 1.3" would prove nothing.

**The non-mechanical metric is novel state coverage** — `observationHash` values in the pilot corpus
absent from the t4 corpus. Compare against a **56-game subsample** of t4, not all 560.

### Ordering, and what stays fixed

- Adaptive entropy targeting, soft cloning targets and GAE are tested **separately, afterward**.
  Simultaneous changes destroy attribution. (Adaptive entropy also needs care: `1/Σp²` is a Rényi-2
  quantity while SAC-style tuning targets Shannon entropy — target one and report the other.)
- **The argmax-strength promotion gate does not move.** Exploration performance and checkpoint
  promotion are different gates. A mixed sampler generates data from the frozen parent; a candidate
  still promotes only on deterministic argmax strength.
- PPO ratios: if the trainer optimizes raw π from mixed behavior data, the denominator must be the
  recorded mixed μ, and the numerator choice needs an explicit off-policy objective — not a blind
  substitution.

## 9. Known hazards carried forward

- **`LegalActionEnumerator` materializes target combinations before validating mana.** Mass Rebirth
  with 19 eligible graveyard minions produced `19P7 = 253,955,520` target arrays for a card that
  could not be cast (4 mana, costs 7), blocking synchronously past any wall-clock cancellation.
  Corrective seam: a cheap validator preflight before target-plan expansion. Separately, ordered
  multi-target selection needs an explicit contract decision — even unordered is `C(19,7) = 50,388`.
  One episode is already quarantined from bootstrap corpora for this
  (`decisions/emberfall-quarantine-pathological-flat-action-episodes`).
- **`INVALID-` prefixed artifacts must never be used** — one corpus/export/checkpoint set from an
  experiment that collapsed ordered target permutations, one from a rollout missing
  `behaviorAllowsConcede` provenance.
- **The existing actor-critic verifier recomputes behavior probability as temperature-softmax only.**
  A correct mixture receipt **will fail it** until it is extended to recompute μ from logits, group
  membership and sampler spec, treating the live-captured value as authoritative.
- **The brain partition was one-way until 2026-09-09.** Codex read both shelves; Claude read only
  `default`. Sixty pages — including Sam's eight-class decision taxonomy and the overconfidence
  naming constraint — were invisible, and got independently re-derived. Fixed at
  `access_tokens.permissions.source_id = ['default','codex']`. Duplicated work was the visible cost;
  two agents holding different records of the same decision, each confident, was the real one.

## 10. Deferred, on purpose

- Live rendered spectator self-play — "monitored" means rollout health, not a viewer.
- The generic JSON card-game review gym. The Kingdom review tool stays Kingdom-shaped until it
  is understood.
- The full decision-class taxonomy as a *blocker*. It remains a diagnostic; training does not wait
  for it.
- The reusable counterfactual policy evaluator (8-12 h estimate) — designed, not built.

## 11. The sampler, as built (2026-09-09)

The capture half of §8 is implemented and tested. **No trainer, optimizer or holdout was touched.**

- **`src/Kingdom.Tools.NetSoak/DistinctPlayKey.cs`** (new) — the play identity, versioned
  (`SchemaVersion = 1`). Keyed from the **typed** `PlayerIntent`, not from JSON, which is what makes
  the two wire spellings of a target a non-issue: `IntentJsonCodec.WriteTargets` picks bare-vs-object
  purely from `Binding.Length == 0`, so the shape carries nothing the typed value does not.
- **`CheckpointSeatPolicy`** — `CheckpointSelectionMode.Mixture`, `CheckpointSelection.Mixture(t, ε,
  seed)`, and `MixtureProbabilities(π, playKeys, ε)` returning the μ vector that the draw *and* the
  recorded probability both read. **`LogProbability()` was deleted, not left unused** — a second,
  wrong answer sitting next to the right one is the whole failure mode.
- **Provenance** — `SamplerMode`, `Epsilon`, `DistinctPlayCount`, `PlayKeySchemaVersion` on the
  decision trace, on `EpisodeDecisionHash` and through `TrainingCorpusBuilder`. A mixture row is
  rejected unless its sampler specification is complete; a non-mixture row is rejected if it carries
  mixture fields. The policy identity string is
  `checkpoint:{sha}:mixture:t={T}:eps={ε}:playkey=v1:seed={seed}`, so a mixture corpus cannot be
  pooled with a tempered one by accident.
- **CLI** — `netsoak rollout … --epsilon 0.15`. Absent keeps the historical tempered sampler
  *including its seed consumption*, so a rollout without the flag still reproduces bit for bit.
- **Tests** — `tests/Kingdom.Net.Server.Tests/Bots/MixtureSamplingTests.cs`, 22 assertions
  covering the algebra, alias invariance, the saturated and underflowed cases, the wire round-trip,
  and the property that matters: `exp(behaviorLogProbability) == μ[selected]`.

Not built, deliberately: the trainer's behavior verifier still reproduces a tempered softmax only,
so a mixture corpus **fails it loudly** — the correct failure. A `RETIREMENT TRIGGER` sits on
`_capture_temperature_log_probabilities` in `tooling/scripts/training_trainer/actor_critic.py`
naming what to change and the off-policy question to settle first.

**One divergence, flagged rather than reconciled.** The Unity reviewer's `ActionGrouping` normalises
a target to its instance id and *drops the binding*, so it merges strictly more than this key does.
The production contract forbids collapsing semantically distinct non-default bindings, so the two
now disagree on any decision offering the same instance under different bindings — 381 decisions in
the current corpus. Deciding which is right is Sam's call, not a thing to quietly settle.

Still untouched and in scope for later: `PolicyArenaRunner.cs`, `PolicyLeagueCommands.cs` (carries
the fields through but its own rollouts still sample by temperature),
`tooling/scripts/training_trainer/data.py`, `work/decision-entropy/compute_decision_entropy.py`.

## 12. The pilot ran (2026-09-09) — clean, and it killed its own success criterion

56 games, `--epsilon 0.15`, T=4, CPU. `clean=56/56 rejects=0 stalls=0 resyncs=0
replayDisagreements=0`. 6,017 decisions, every row carrying a complete sampler specification, all
of it surviving corpus reconstruction through the real engine.

**The preregistered non-mechanical criterion is inert.** Novel `observationHash` coverage returns
~100% for any pilot at all, including one that changed nothing:

```
baseline decision rows          56805
distinct beforeSha256           56805
hashes appearing more than once     0
```

The hash is a nonce — the projected state carries `rngRawState`, the match `id` and the instance
counters. The control is what settles it rather than that argument: **two disjoint 56-game halves
of the same baseline share zero hashes.** And it is not a hashing bug. A position abstraction that
strips every identity and nonce and keys cards by definition and current stats still finds
positions repeating on **0.13% of rows within a single corpus**. Two samples of a 30-card-deck game
with shuffles share essentially nothing however you hash them. That metric family is dead at this
scale; the track is stopped rather than quietly re-specified.

**Substitute measurement, labelled as one.** Both corpora already record the probability the
behaviour policy put on the action it executed. Temperature is multiplicative and reaches only
plays whose logit is already close; an additive floor reaches ones the policy wrote off.

| p(executed action) | pilot (mixture) | baseline (tempered) |
|---|---|---|
| `[0.001, 0.01)` | **1.22%** | 0.10% |
| `[0.01, 0.1)` | **11.37%** | 2.74% |
| `[0.85, 1.01)` | 48.92% | 68.67% |
| median | **0.8429** | 0.9723 |

Executed an action the policy gave < 0.01: **1.24% versus 0.10% — 12x.**

**A ceiling the routing choice creates, worth knowing before assuming otherwise.** Nothing below
1e-3 was executed in either corpus, and that is structural. A play with `π(P) == 0` gets an even
split of `ε/K` — about 0.021 at mean K = 7.25 — but a *spelling* with `π(i) == 0` inside a play
whose `π(P) > 0` gets exactly zero. So **"ε makes every legal action reachable" is false.**
Canonical routing would have the same hole somewhere else.

Nothing here speaks to playing strength. No optimizer ran, no holdout was captured, no checkpoint
was produced, and it does not show the exploration is *useful* — only that it happens.

Scripts, whose stdout every number above is: `work/mixture-pilot/`.

---

## 13. The night of 2026-09-09 → 10 — a fixed policy, an aimless update, and a blind instrument

Six hypotheses were opened and closed in one night, and then a seventh measurement explained why
all six had to fail. Each has its own brain page; this is the arc.

### 13a. The policy could not be reordered at all

The median top-two logit margin on the parent was **14.7**. A clipped surrogate cannot reorder a
gap that size, so exploration, data volume, calibration, critic-as-chooser and lethal conversion
were all interventions on a policy that was pinned. **Label smoothing over the legal mask removes
the constraint for free** — 21× smaller p90 margins at an *identical* arena score, so it costs no
strength. Note the trap: torch's own `label_smoothing` spreads target mass over `-inf`-masked
illegal actions and yields a non-finite loss; the smoothing has to be done over the legal actions
only.

### 13b. With the policy movable, the update is large, differential and unaimed

A full 560-game generation on the smoothed parent reorders the argmax on **1.9% of decisions**, and
those reorderings are a **coin toss** with respect to position quality (47.8% better, 51.5% worse).
The update is not too small and not undirected — it is not *aimed*. That points at the objective.

### 13c. Judging an objective no longer costs a generation

`netsoak action-value` branches a decision and plays each branch to terminal with Greedy on both
seats, so a label costs engine steps rather than scorer calls (`GreedyAi.Choose` is a pure static).
**987 usable non-circular labels from one generation, in minutes.** By that measure the policy has
a real but small edge: played the best action 56.6% against a per-state chance baseline of 49.8%.

It also nearly produced a false headline — advantage-vs-label correlates at **r = +0.34** with a
clean quintile gradient, which collapses to **+0.087 and +0.001 inside each outcome group**. Both
quantities contain who won.

### 13d. The offline bake-off ranked GAE first, and the arena did not confirm it

Ranked offline against those 987 labels: terminal return (what the trainer uses) scores 0.044,
below the 0.092 noise floor; **GAE(0.9) scores 0.151**, and the λ curve peaks in the middle, which
is the shape the bias-variance tradeoff predicts. `--gae-lambda` was implemented on that basis, off
by default and bit-identical to prior behaviour at 0.

**The arena returned 25-31 (44.6%) vs Greedy, one game below the baseline** — and §13e shows that
number carries no information: it is 1/5 decisive pairs against the baseline's 1/4. GAE does
sharpen the policy where terminal diffuses it (mean entropy change −0.026 vs +0.011). **Keep
`--gae-lambda` available and do not change the default**: it is better on an offline proxy and
**untested** in play. The labeller's predictive validity for strength is likewise unestablished —
its one prediction was checked against a measurement that could not answer.

### 13e. And then: the arena could not have shown a difference

Every generation-2 arena ran at `gamesPerSeat 1` — **one seed per matchup**. The schedule plays
each matchup twice with the candidate and opponent exchanged on the same seed and decks, so a
matchup whose winner does not depend on which policy plays it reads 1-1 and contributes exactly
50%.

```
smoothed baseline
  headline                 26-30 = 46.4% over 56 games
  matchups where swapping the two policies changed nothing: 24/28
  everything else            2-6 over 8 games  95% CI [7.1%, 59.1%]
  and as INDEPENDENT trials 1/4 matchups won  95% CI [4.6%, 69.9%]
```

**26-30 is `24-24` plus `2-6`**, and the two games of a decisive matchup are perfectly correlated,
so the trial is the matchup: the headline rests on **four observations**, and every interval spans
50%. The GAE child's 29-27 against its own baseline is thinner still — **one** decisive pair out of
28.

Two explanations for the invariance suggested themselves and **both died to the next measurement**,
which is the reason to record them:

- *The bot is a Greedy clone.* No. An identical policy replays a swapped game move for move;
  **0/28 swap pairs share an `actionTraceSha256`** for any of the three candidates.
- *The deck pairings are lopsided.* No. Self-play with the **same checkpoint on both seats**, 20
  seeds per pairing: **0/28 pairings reject a fair coin**, pooled seat-0 rate 48.6%, range 35–65%.
  `cohesive-v1-20260907` is balanced.

What is left was the schedule — and **more seeds do not fix it either.** `power-baseline-n4`, four
seeds per matchup and 448 measured games:

```
  vs greedy-ai.v2
    headline            109-115 = 48.7% over 224 games
    invariant pairs     105/112 (94%)
    decisive pairs      2/7 won   95% CI [8.2%, 64.1%]
```

**The invariant fraction went UP, 86% → 94%.** Four times the games bought seven decisive pairs
instead of four, because the signal is about 6% dense and games buy only √n against a constant.
Roughly **6,400 games** would be needed for a hundred decisive pairs. And it is not that the
policies play alike in a shallow sense: 76% of invariant pairs have the same turn count as well, so
the swap changes the moves without changing the shape of the game.

The real content of that number is a *finding about the policy*, not only about the instrument:
**on nine games in ten, it does not matter whether the bot or Greedy is playing.** The bot is a
Greedy-strength player, and win rate against Greedy is close to the least sensitive possible way to
detect a small departure from that.

**This does not touch §13a–§13c**, which are measured off the corpus. It touches the *motivation*:
the deficit those findings set out to explain was never established. Until a re-measurement at real
resolution produces an interval that excludes 50%, "the bot is stuck at 46.4%" is one reading of
four coin flips.

**The rule to carry forward: never report an arena score without the invariant count beside it.**
`work/arena-power/arena_power.py` takes an arena directory and prints both.

### 13f. Where the sensitive instrument already is

The arena stays the promotion gate — it catches regressions and proves a clean deterministic run,
which is what it was built for. It is the wrong tool for *development*, where the differences are
small by construction.

The instrument with the power is the one built in §13c: **987 non-circular per-decision labels from
a single generation, against 7 decisive game pairs from 448 arena games.** What it could not do was
compare *checkpoints* — it labels the actions of the corpus that produced it.

**`netsoak action-choice` closes that** (2026-09-10). Given a labels file it replays each labelled
decision and asks a checkpoint what it would play there, so two checkpoints are compared on a common
state set with the state distribution held fixed. The policy is offered `Project`ed state and fed
`ProjectEvents`ed transitions, so its deck memory evolves as it would in play; the play key is
computed from the **authoritative** state, because that is what the labeller used.

```
separating labelled states     987
states every arm is SCORABLE on 984

  arm           unlabelled     best     rate             95% CI   chance     edge
  baseline               2      561    57.0%       [53.8, 60.0]    49.8%    +7.2pp
  terminal               3      561    57.0%       [53.9, 60.1]    49.8%    +7.2pp
  gae09                  2      560    56.9%       [53.7, 59.9]    49.8%    +7.1pp

PAIRED DIFFERENCES - only states where the two arms scored differently count
  baseline vs terminal             6       6    +0.00pp       p = 1.0000
  baseline vs gae09                4       3    +0.10pp       p = 1.0000
  terminal vs gae09                5       4    +0.10pp       p = 1.0000
```

**The first interval on bot quality this project has produced.** +7.2pp over a per-state chance
baseline, excluding chance — a real edge by a measure owing nothing to the critic.

**And the three checkpoints are indistinguishable at p = 1.0.** That is not the instrument failing;
it agrees with §13b, where a full generation reorders the argmax on 1.9% of decisions. The children
barely differ from the parent, and that now has a number and a p-value rather than a two-game arena
wobble.

Read it for what it is: the label is a Greedy continuation's outcome, not the action's value under
any policy, so agreement is **not strength** — it is "chooses more of what a Greedy finish rewards".
Greedy is deliberately not an arm, because its own choice is the first move of the continuation that
defines the label.

### 13g. What the update actually spends itself on

With a sensitive instrument in hand, the question that a null cannot answer became askable: not
"did the update help?" but **which way does the objective point?** Turn the step size up until the
effect leaves the noise.

**The window is under one order of magnitude wide.** `--learning-rate 1e-3` and `1e-2` both die on
step 1 of 230 with `actor-critic probability ratio must be finite and positive`. The guard checks
`isfinite(ratio) and ratio > 0`, and it is the **minimum** that fails: not an explosion, an
underflow. The policy drives a behaviour action's probability to zero in a single step.

Inside the window, 1e-4 / 2e-4 / 4e-4:

```
arm               entropy  approximateKLD    clipFraction      policyLoss  valueExplained  ratio p05  ratio p95   ratio min
lr 1e-4          0.291674        0.031940        0.091298        0.005028        0.431157     0.7997     1.1614    3.10e-03
lr 2e-4          0.240180        0.095360        0.136134        0.013948        0.415526     0.5463     1.1969    1.01e-05
lr 4e-4          0.163983        0.362879        0.142503        0.015591        0.399266     0.2713     1.2288    1.88e-16
```

**The ratio's 5th percentile collapses from 0.80 to 0.27 while its 95th moves from 1.16 to 1.23.**
The update pushes probability down violently and pulls almost nothing up. Entropy falls
monotonically.

And the choices barely follow. Argmax disagreement with the parent, on 1,550 paired labelled states:

```
  pair                         differ  and matter
  baseline vs lr1e4                27          10
  baseline vs lr2e4                30          11
  baseline vs lr4e4                42          21
  baseline vs gae09                19           8
```

**An 11× increase in KL divergence moves the argmax on 42 decisions instead of 27.** The policy is
pushed to the edge of numerical stability and changes its mind half again as often. **Almost the
entire update is spent becoming more certain of what it already thought** — which is also spending
back exactly what §13a's label smoothing bought.

*(In passing: GAE(0.9) re-ranks* less *than the terminal advantage, 19 states against 27.)*

**On direction, a retraction of my own hedge.** On the 987-state label set the 4e-4 arm chose the
better-labelled play more often than every other arm — 12-8, 11-7, 11-5, same sign three times at
p = 0.50 / 0.48 / 0.21. I reported it as suggestive and short of the preregistered bar. Re-measured
against a 1,567-state set sharing only 30% of the first set's states, **it reverses**: 12-9, 12-9,
14-10 the other way. The direction was noise, as its p-values said. **The objective's direction is
unmeasured, not suggestive.**

The lever is therefore not step size. Making the update re-rank rather than sharpen is a change to
the objective or to what constrains it; the measured pathology is one-sided, so an entropy penalty
is the direct counter and `--entropy-weight` already exists.

### 13h. The re-ranking is untargeted — and now that is a measurement, not an absence

If sharpening were merely *outcompeting* a re-ranking direction, penalising it should convert budget
into re-ranking. `--entropy-weight` at 0.01 / 0.05 / 0.20, learning rate held at 4e-4:

```
arm               entropy  approximateKLD    clipFraction      policyLoss  valueExplained  ratio p05  ratio min
ew 0.01          0.163983        0.362879        0.142503        0.015591        0.399266     0.2713  1.88e-16
ew 0.05          0.370427        0.249042        0.249431        0.033431        0.402194     0.1912  3.17e-09
ew 0.20          1.136286        0.663976        0.634619        0.120686        0.398525     0.0584  5.04e-04
```

**It works mechanically and clears its preregistered bar by a mile** — argmax disagreement with the
parent goes **37 → 102 → 236** against a criterion of ≥50, and the ratio collapse is genuinely
prevented.

**And quality falls monotonically**, to the project's first significant comparison:

```
  arm           unlabelled     best     rate             95% CI   chance     edge
  baseline               5      951    60.9%       [58.4, 63.3]    51.8%    +9.0pp
  lr4e4                 14      942    60.7%       [58.2, 63.1]    51.9%    +8.8pp
  ent005                26      918    59.6%       [57.1, 62.0]    51.9%    +7.7pp
  ent020               190      813    59.0%       [56.4, 61.6]    52.0%    +7.0pp

  baseline vs ent005              33      14    p = 0.0079
```

**Which the control overturns, for the fifth time in this project.** The baseline agrees with the
best-labelled play 61% of the time against a per-state chance of 52%, so *any* untargeted change
moves it downward — a pure-noise flip is expected to lose more than it wins.
`work/arena-power/random_flip_control.py` replaces the baseline's play, on exactly the states each
arm re-ranked, with a different labelled play drawn uniformly:

```
dose00004   re-ranked 42    observed net  -3   random null mean  -4.9 sd 2.9   z +0.66
ent005      re-ranked 128   observed net -20   random null mean -17.8 sd 4.8   z -0.47
ent020      re-ranked 236   observed net -21   random null mean -31.9 sd 6.7   z +1.62
```

**All three are indistinguishable from re-ranking at random.** The arms did not aim badly — they did
not aim. The significant quality drop is the cost of untargeted motion applied to an above-chance
policy, nothing more.

That separates the two diagnoses that have been tangled since generation 1. It is **not** that the
step is too small — `ent020` re-ranks 17% of states and gains nothing. It is **not** that the
objective points the wrong way — no arm is worse than chance. **The advantage carries no per-action
information at this scale**, which is what §13d measured directly when terminal return scored 0.044
against a 0.092 noise floor.

**So the sweep of *how* the update moves is finished.** Learning rate across a 4× range to the
stability edge, entropy weight across 20×, GAE against terminal, mixture sampling, label smoothing,
value-greedy selection, lethal invariants. None of it matters. The next thing to try has to change
**what the update is told**.

### 13i. The critic is not a teacher, and confidence is not a signal

Value-greedy selection — picking the action whose afterstate the critic values highest — was
reported once as a falsified hypothesis **on the strength of an arena that cannot distinguish these
checkpoints at all**, so the question was open again. It matters because §13h leaves the policy
gradient aimless: if one-ply lookahead re-ranks in an *aimed* way, there is a distillation target
inside the model already built.

`netsoak action-choice --selection value-greedy`, on 1,400 untruncated separating states:

```
  baseline  agreement  62.6%   chance  53.7%   edge  +9.0pp
  arm       agreement  59.2%   chance  53.7%   edge  +5.5pp
  re-ranked 856, mattered 378, arm better 165, baseline better 213, net -48
  random null: mean -83.9 sd 12.1, z +2.96
```

**It aims — z = +2.96, the only aimed re-ranking measured in this project — and it still loses by
3.4pp.** The critic's afterstate ordering carries real per-action information and **less of it than
the policy head already expresses.** Distilling it would make the policy worse.

**The control that saved this one.** The first reading was z = **+3.87**, on all scorable states.
The tell was 317 unlabelled choices against argmax's 5: the labeller caps branches at `--max-plays`,
argmax mostly reproduces the corpus action — which the labeller branches *first* — and so is almost
never dropped, while value-greedy ranges further and is dropped 60× more often. The surviving set
was conditioned on value-greedy having agreed with the corpus. `untruncated_only.py` restricts to
states with no more distinct plays than the cap, where truncation is impossible, and **asserts** it.

**No hybrid rescues it either.** Bucketed by the policy's own confidence in the action it executed —
which the corpus already records as `behaviorLogProbability`, for exactly the checkpoint under test:

```
  bucket       n            log p range   argmax   value-greedy     delta      95% CI on delta
  1          280         -8.19 .. -0.22    63.2%          60.7%     -2.5pp       [-8.2, +3.4]pp
  2          280         -0.22 .. -0.09    62.5%          60.0%     -2.5pp       [-8.1, +3.3]pp
  3          280         -0.09 .. -0.05    63.9%          58.2%     -5.7pp      [-11.5, +0.6]pp
  4          280         -0.05 .. -0.02    60.0%          60.7%     +0.7pp       [-5.2, +6.5]pp
  5          280         -0.02 .. -0.00    63.6%          56.4%     -7.1pp      [-12.7, -1.0]pp
```

The hypothesis was that lookahead pays where the policy head is **unsure**. Bucket 1 is the least
confident and it loses there too. Only the last bucket behaved as predicted: where the policy is
most certain, overriding it costs the most.

**And the `argmax` column is the bigger finding: 63.2, 62.5, 63.9, 60.0, 63.6 — flat, across eight
nats, 0.03% to 99.9%.** The policy is no more likely to be choosing well when it is certain than
when it is guessing. That kills every scheme keyed on its own uncertainty: gated lookahead,
uncertainty-driven exploration, confidence-sorted decision review. It completes rather than
contradicts the earlier ruling that high confidence is not itself a defect — **low confidence is not
a signal either.**

### 13j. The bot agrees with Greedy on 97.7% of decisions, and everything above collapses into it

`--selection greedy` adds a **reference arm**: what would `GreedyAi` — the player whose continuations
define the labels — choose at each labelled state? It was built to measure the headroom of the
imitate-Greedy family. It answered a bigger question.

```
all labelled states                11200
bot argmax == GREEDY               10938  (97.7%)
bot argmax == value-greedy          4357  (38.9%)
GREEDY     == value-greedy          4392  (39.2%)
```

On the 1,400 untruncated separating states the two are indistinguishable in quality too — `62.6%`
against `62.7%`, with Greedy re-ranking **29 of 1,400** states.

**Every open puzzle in this document collapses into that one number.**

- **Why 0/28 arena swap pairs replayed identically while 24/28 had the same winner** (§13e). At 97.7%
  per-decision agreement a 114-decision game has a **6.7%** chance of never diverging, so essentially
  every game differs somewhere and almost none differ enough to change who wins. Two facts that
  looked contradictory are one number.
- **Why the arena cannot measure anything.** It is scoring a Greedy clone against Greedy. The
  schedule made it worse; the floor under it is that the two players are the same player.
- **Why terminal-return RL goes nowhere** (§13h). The policy sits at a Greedy fixed point and the
  update's re-ranking is random-equivalent, so nothing pulls it off.
- **Why confidence is flat against correctness** (§13i). A clone of a heuristic has no reason for its
  probability mass to track its correctness.

**And it does NOT close the labelling family — it opens it.** The tempting conclusion is "the labels
are Greedy's opinion, so training on them cannot beat Greedy." The reference arm shows that is wrong.
The label is not *what Greedy picks*; it is *which branch wins when Greedy finishes from it*, and
**Greedy's own choice matches the label-best play only 62.7% of the time.** On roughly 37% of
separating decisions the label rewards a play Greedy would not make.

So the label is a target that **differs from what the policy currently does** — the property every
objective tried so far has lacked. The standing objection to distilling it, that it caps at Greedy
strength by construction, is **falsified**: it caps at whatever the Greedy-*continuation* yardstick
can express, which is 37 points above where Greedy itself sits. The yardstick is still a weak
reference and a line that only pays under strong play is still undervalued — that bounds the ceiling
without putting it at 62.7%.

**Baseline to beat: 62.6% agreement on 1,400 untruncated separating states, chance 53.7%** — and a
gain is measurable in minutes against the random-flip null, not at n=7 game pairs in an hour.

### 13k. Label distillation — the first aimed update, and the ceiling it measures

§13j says the label is the only target measured to *differ* from what the policy already does. So
train on it. `netsoak play-index-labels` re-expresses the labels on the trainer's legal-action-index
axis (axis agreement **checked**, 23,096 verifications, no mismatch) and
`train-distill` fits the policy head to them: uniform mass over the best-labelled indices, masked to
the **labelled** indices rather than merely the legal ones, on separating decisions only.

**The split is by EPISODE and the selection is on the held-out score**, because rows from one game
share a deck, a shuffle and an opponent, and choosing the epoch by its fitted score picks the most
memorised one.

Two runs, 2.75× apart in data, scored on **held-out episodes only**:

| | labels | held-out states | baseline | arm | paired p | random-flip z |
|---|---|---|---|---|---|---|
| s20/p8 | 58,663 | 281 | 66.5% | **68.0%** | 0.6989 | **+3.33** |
| s60/p8 | 161,958 | 812 | 63.9% | **65.4%** | 0.4044 | **+5.03** |

**+1.5pp both times. The effect size replicates; the significance does not arrive.**

**z = +5.03 is the finding.** Every policy-gradient arm sat between −0.47 and +1.62 (§13h);
value-greedy reached +2.96 while scoring *below* baseline (§13i). A random re-ranking of the same
449 states nets −32.4 ± 8.8; this nets +12. **The first update in this project that both aims and
points the right way.**

**And p = 0.40 is the other half.** It changes its mind on 449 of 812 held-out states — 55% — for
1.5 points of quality.

**That combination measures the ceiling.** Both training curves:

```
s20/p8   parent fit=0.6040 HELD-OUT=0.6000  →  epoch 3 fit=0.6438 HELD-OUT=0.6360  →  epoch 8 fit=0.7684 HELD-OUT=0.6000
s60/p8   parent fit=0.5929 HELD-OUT=0.6047  →  epoch 2 fit=0.6227 HELD-OUT=0.6323  →  epoch 8 fit=0.7310 HELD-OUT=0.5966
```

The network **fits the labels to 73–77% and cannot generalise them past ~63%** — a ten-point gap, in
both runs, on 2.75× different data. The Greedy-continuation label is largely unpredictable from the
observation features: a single deterministic continuation carries a lot of state-specific luck, and
only about **three points above a Greedy clone are learnable**.

That bears on the AlphaZero-shaped option, which shares the structure — one noisy rollout-derived
label per action, generalised through this feature set — with a stronger reference. **This says a
large part of the loss happens in the feature set, not the reference.**

**A correction I had to make to my own preregistration mid-experiment.** It named agreement on the
full 1,400 untruncated separating states as primary — states that include the episodes the model
trained on, which is exactly the contamination its own hazard list warned about.
`work/arena-power/holdout_verdict.py` scores held-out episodes only.

### 13l. The distilled bot beats Greedy, and the arena works again

§13e explained the arena's uselessness as "it is scoring a Greedy clone against Greedy". That is an
explanation, and it makes a testable prediction. The distilled checkpoint re-ranks half its
decisions, so **preregistered before the run**: the invariant fraction should fall below 80%, and
anything above 88% falsifies the explanation.

```
distill-s60-seed7-vs-greedy-n4        156-68 = 69.6% over 224 games
  invariant 58/112 (52%)   decisive 49/54 won   95% CI [80.1%, 96.0%]

distill-s60-vs-greedy-n8              278-170 = 62.1% over 448 games
  invariant 138/224 (62%)  decisive 70/86 won   95% CI [71.9%, 88.2%]

distill-s60-vs-baseline-and-greedy-n4
  vs the smoothed baseline            144-80 = 64.3%   decisive 43/54  CI [67.1%, 88.2%]
  vs greedy-ai.v2                     137-87 = 61.2%   decisive 35/45  CI [63.7%, 87.5%]
```

**Three arenas, two independent training seeds, every one clean.** The baseline at the same four
seeds: 109-115 = 48.7%, two decisive pairs out of seven.

**The invariant fraction fell 94% → 60%.** The arena was never broken in a way that mattered; it was
measuring two copies of the same player. **And the bot beats Greedy** — 35 of 45 decisive pairs, CI
excluding 50% by a wide margin, against the baseline's own 48.7% with two decisive pairs out of
seven at the same four seeds.

How far it moved, on 30,854 labelled states: **baseline 97.6% agreement with Greedy, distilled
49.4%.** The distillation did not nudge the policy, it replaced it.

**THE CAVEAT THAT MATTERS MOST.** The label *is* a Greedy continuation, so beating Greedy is partly
what the objective was built to do — "which branch wins when Greedy finishes" rewards steering into
positions Greedy handles badly. **This establishes strength against one reference, not general
strength.** The 64.3% against the smoothed baseline is better evidence, but that baseline is itself a
97.6% Greedy clone, so it is not independent either. **The project has no independent opponent**, and
building one — a model distilled from a different reference, or a search-based player — is the
obvious next thing.

**And the yardstick got the direction right but not the ordering.** Seed 1337 peaked at 63.2%
held-out label agreement and scored 61–62% in the arena; seed 7 peaked *lower* at 62.0% and scored
**69.6%**. The two arenas' intervals overlap, so the arena difference is not established either —
but nothing here supports using label agreement to rank checkpoints *within* the family. It answered
"does distillation help"; it should not be trusted for "which child is better". Same shape as the
+1.5pp label gain against a twelve-point arena move.

**Not promoted, and not a request for promotion.**

### 13m. The yardstick is distribution-sensitive, and the lead grows on the model's own states

Every choice-level number in §13j-§13l carries the same caveat: **the evaluation states belong to
whoever captured the corpus.** That was untestable while every checkpoint played the same game. The
distilled model agrees with the baseline on 49.9% of decisions, so it has trajectories of its own.

**224 fresh games** from the distilled checkpoint, clean, labelled to 4,480 states and 26,265 labels
with zero undecided. **The model never trained on any of them** -- out of sample entirely, not a
held-out split.

```
  baseline  agreement  60.5%   chance  54.0%   edge  +6.5pp
  distilled agreement  63.9%   chance  54.0%   edge  +9.9pp
  greedy    agreement  61.5%   chance  54.0%   edge  +7.4pp

distilled re-ranked 327 of 524, mattered 128, better 73 / worse 55, net +18
RANDOM null: mean -14.9 sd 7.4   z +4.42       McNemar p = 0.1326
```

| states scored on | baseline | distilled | lead |
|---|---|---|---|
| the **baseline's** trajectories | 63.9% | 65.4% | +1.5pp |
| the **distilled model's** trajectories | 60.5% | 63.9% | **+3.4pp** |

**The lead more than doubles on the positions the distilled model actually reaches** -- the
direction a genuinely better policy should show, and the opposite of the flattery concern, since it
was the *baseline* being scored at home for the smaller gap. Preregistered prediction was "holds,
no larger than +4pp"; it is +3.4pp.

**So an agreement number is not portable. Always name whose trajectories it came from.**

Still p = 0.13: two state sets both favour the distilled model, at +1.5pp and +3.4pp, neither
significant alone and not independent enough to combine. The z = +4.42 null is the solid half.

Incidentally, `baseline vs greedy` on these foreign states is **4 and 9 discordant out of 524** --
the 97.6% clone finding is not an artifact of the corpus it was measured on either.

### 13n. Auditing the new tooling for the defect that hid the critic damage

The value-head bug was a config field declared, written into every manifest, and never read. That is
a *family*, so the rest of the day's tooling got the same question: what does this claim that the
code never does, and what is it assumed to leave alone that nobody measured?

**Three more, all found by asking rather than by failing.**

1. **The reference arm claimed a checkpoint it never consulted.** `--selection greedy` answers from
   `GreedyAi`, yet every row carried the `checkpointSha256` of whatever model was passed positionally
   -- and the run started a python scorer to load it. A join on that sha would have attributed
   Greedy's plays to the baseline. Now: no scorer, null sha, identity `greedy-ai.v2`. **Verified
   behaviourally identical, 4,480 of 4,480 play keys unchanged.**

2. **The agreement metric scored a different population than the instrument it is compared against.**
   The loss masks to the LABELLED actions and the metric took its argmax over that same mask, but a
   deployed policy argmaxes over the LEGAL ones. Restricted now to decisions where every legal action
   carries a label, so it asks `action-choice`'s question. **I predicted this would lower the number;
   it raised it** -- parent 60.47% to 63.92% -- because well-posed states have fewer plays and are
   easier, so the restriction changed which states are counted more than it changed the argmax. The
   justification is comparability, not the direction I claimed.

   It also **moved the selected epoch from 3 to 2**, and the new selection is better on both
   independent measures. The per-epoch losses are byte-identical to the previous run, which is the
   check that only the metric moved.

3. **The holdout fraction was a magic `0.2` duplicated in the trainer and the verdict script.** A run
   with a different `--holdout-fraction` would have been scored against the wrong episodes silently.
   The verdict now takes the run directory, reads `holdoutFraction` from its manifest, and **asserts**
   the represented match count against `heldOutMatches` instead of printing it as advice.

**Where that leaves the candidates:**

| checkpoint | vs Greedy | critic calibErr | explVar | sound? |
|---|---|---|---|---|
| `distill-s60-seed7` | 69.6% | 0.735 | 0.158 | **no** |
| `distill-s60-lr1e4` | 62.1% (448 games) | 0.780 | 0.146 | **no** |
| `distill-s60-vw05` | 60.3% | 0.162 | 0.306 | yes |
| **`distill-s60-vw05-wellposed`** | **64.3%** | **0.154** | 0.270 | **take this one** |

*parent critic: calibErr 0.3018, explVar 0.2010.* The recommended checkpoint is 144-80 over 224
games, 41/50 decisive pairs, 95% CI [69.2%, 90.2%], clean -- and on held-out episodes it scores
65.9% against the baseline's 63.9%, z = +4.49, McNemar p = 0.2356.

**The best arena score belongs to the worst-conditioned model.** Promoting on the headline alone
would have picked it.

Scripts, whose stdout every number above is: `work/credit-assignment/`, `work/arena-power/`.

---

## 14. 2026-10-05 — Greedy changed, and the yardstick moved

Sam asked for the opponent a human faces to stop making obviously non-human plays. The server's
only opponent is `GreedyAi`, so the change was made there, and Sam then chose to move the soak and
the league onto the new bot rather than keep two.

**What `greedy-ai.v3` does that v2 did not** (`src/Kingdom.Net.Server/Bots/GreedyAi.cs`):

- takes lethal when the attackers that can legally hit face cover the enemy hero's health and
  armor, before playing a card or trading; a damage spell that covers the remaining gap goes face;
- aims a single-target hostile card at the highest-attack enemy minion (damage spells: the biggest
  minion they kill, otherwise face) — v2 took the leftmost;
- has each attacker trade into a minion it kills, biggest first, before one it only dents.

Everything else is v2's loop: first affordable card in hand order, always trade when there is no
lethal, hero power last.

**What this breaks, on purpose:**

- **v2 is gone from the code.** There is no flag that brings it back; check out a commit before
  `e2c32d79` to reproduce a v2 number.
- **An existing league directory will not load.** `PolicyLeagueManifest.Validate` refuses any
  league whose `PermanentSanityOpponents` names `greedy-ai.v2`, and says why. Start a new league
  with `league-init`. Continuing an old one would put v2 and v3 evaluations in one history.
- **No recorded number carries over.** The champion's 69.6% against Greedy, the 97.7% agreement
  figure in §13j, every arena cell labelled `greedy-ai.v2`, and every action-value label built with
  `--continuation greedy` describe v2. The champion was distilled from v2-continuation labels and
  has never played v3.
- **Captured data is still labelled truthfully.** The 1,008-game frozen capture and every
  human-inbox episode before the deploy say `greedy-ai.v2` in their manifests and replay
  unchanged — a replay is intents, not a policy. Episodes captured after the deploy say
  `greedy-ai.v3`. Do not pool the two as one behaviour policy.
- **The balance matrix moved too**, because the same bot drives it. The first v3 matrix is at
  `F:\emberfalldata\balance-matrix\greedy-v3-20261005\`.

**Before the next training step:** re-measure the champion against v3, rebuild any Greedy
continuation labels you intend to train on, and re-run the §13e arena-power check, since which
matchups are decided regardless of policy may have changed.

The Unity `PlayingAi` was not changed. It only fills our own seat and is no longer in step with
this bot.

---

## 15. 2026-10-06 — The audit's first three follow-ups, and where the time actually goes

An audit of the whole programme (2026-10-05/06) ranked three things above everything else: the
teacher is the ceiling (every training signal is Greedy, or a Greedy continuation that ties most
plays), the data is about a hundred times short, and the sum-pooled network cannot express pairwise
board facts. Sam's order of work was a label benchmark, a card-edit transfer probe, then throughput.
Each left a result file; this section is the index and the one finding that changes the plan.

- **Human review is a validation set, not training data.** `work/target-audit/benchmark_label_sources.py`
  scores any label source against every reviewed decision with a tie-aware pairwise measure. The
  09-27 claim that rollout labels tracked human judgment was an artifact of counting an all-tied
  label as a hit.
- **A checkpoint survives a small card edit if it may read the new definitions.**
  `work/card-edit-transfer/result-20261006.md`. The exporter's `--definition-spec-from` and the
  trainer's `evaluate --override-card-definitions` are the two halves.
- **The first `greedy-ai.v3` corpus on current cards exists**: 2,240 games, 212,820 decisions, seed
  offset 25,000,000, exported against the g3 action vocabulary and definition spec
  (`exports\rl-v8-greedy-v3-cohesive-train-2240-20261006-7152c00d8af6dbbc-g3spec`).
- **Games now run in parallel** (`--workers N` on `ai`, `dataset`, `dataset-matrix`, `rollout`,
  `arena`), and the output is the same bytes at any worker count.
  `work/parallel-rollouts/result-20261006.md`.

**The finding.** Parallel games bought three to four times, not twelve, and the reason is not the
rules engine or the bot. A soak game allocates about 180 MB, and 99% of the allocation and about
90% of the wall time is the client/server loopback every NetSoak game is played through: roughly
1.5 MB and 0.7 ms per intent in snapshots and hashes, against about 1 KB and a few microseconds
for Greedy to choose. The machine saturates on memory traffic at about 45 Greedy games a second
however the work is split across threads or processes. A capture driver that plays on
`LocalMatchHost` directly, as `ActionValueLabels` already does for continuations, removes that
cost; nothing else in capture is worth optimising first.

**That driver exists** (same day): `--direct` on `ai`, `dataset` and `dataset-matrix`. A Greedy
capture went from 31 games a second to 283 on twelve workers, and every file it writes is the
loopback's, byte for byte. `work/direct-capture/result-20261006.md`. `rollout` and `arena` still
play through the loopback; their limit is the scorer, not the transport.

**And capture is not the slow stage anyway.** For the 2,240-game corpus: capture 4m18s (serial),
corpus build 2m10s, RL export 15m07s. The exporter was one Python process at about 235 rows a
second.

**The exporter is parallel now** (same day): `export-rl --workers N`, one per logical CPU by
default. The same export takes 62 s on twelve workers and 6m11s on one, and every file is the old
exporter's, byte for byte. `work/exporter-speedup/result-20261006.md`. A shard is the smallest unit
of parallel work, so a corpus this size cannot use more than about twelve workers.

**Scoring on the CPU beats the GPU for this model.** One decision round trip is about 3.6 ms on one
CPU thread and 8.8 ms on the GPU, so the `--device auto` default picks the slower device. `cpu`
and `cuda` round differently: an argmax arena played the same games on both, a sampled rollout
did not. Pick one device per capture lineage.

**The 22,400-game corpus exists** (same day, night), on a new card freeze taken after the
NIGHTVEIL, STONEWARDEN, GRAVEKEEP and EMBERWARD edits: digest `052c38b6fc203ee5`, build 8dae6ada,
`greedy-ai.v3` on both seats. `work/corpus-10x/run.sh` wrote all of it.

| | games | decisions | seed offset | capture | corpus build | export |
|---|---|---|---|---|---|---|
| `greedy-v3-cohesive-train-22400-20261006-052c38b6fc203ee5` | 22,400 | 2,068,239 | 40,000,000 | 48 s | 1,367 s | 758 s |
| `greedy-v3-cohesive-holdout-560-20261006-052c38b6fc203ee5` | 560 | 51,250 | 50,000,000 | 2 s | 37 s | 27 s |

It is one capture of 800 games a pair, not ten of 80: the trainer reads one export directory and
the corpus builder refuses a repeated matchId. The train export took no pins and wrote a fresh
action vocabulary and definition spec, because the g3 spec cannot see a condition's `kind` or a
`DAMAGE` `instead`; the holdout export is pinned to it and reports no unknown definition values.
The 2,240-game corpus on digest 7152c00d and its export are deleted. The corpus build is now the
slow stage, at about 16 games a second on one process. Nothing is trained on this yet.

---

## Brain pages this is drawn from

Everything cited above lives in gbrain. The spine, in order:

`decisions/emberfall-current-card-set-is-the-next-training-freeze` ·
`notes/emberfall-current-card-baseline-20260907` ·
`decisions/emberfall-quarantine-pathological-flat-action-episodes` ·
`notes/emberfall-current-card-self-play-pilot-20260907` ·
`decisions/emberfall-step-up-next-self-play-to-560-games` ·
`notes/emberfall-current-card-self-play-step560-20260907` ·
`decisions/emberfall-enable-cuda-and-training-progress-20260907` ·
`notes/emberfall-cuda-training-benchmark-20260907` ·
`decisions/emberfall-cohesive-ordered-pair-deck-pool-20260907` ·
`notes/emberfall-grave-tide-legal-enumerator-explosion-20260908` ·
`notes/emberfall-generation-2-freeze-smoke-20260908` ·
`notes/emberfall-generation-2-greedy-cohesive-corpus-560-20260908` ·
`notes/emberfall-generation-2-cohesive-self-play-{pilot,step560}-{preregistration,result}-20260908` ·
`notes/emberfall-generation-2-step560-policy-saturation-diagnosis-20260908` ·
`notes/emberfall-simple-corpus-policy-saturation-hypothesis-20260908` ·
`notes/emberfall-reusable-counterfactual-policy-evaluator-design-20260908` ·
`decisions/emberfall-policy-confidence-by-decision-class-20260908` ·
`decisions/emberfall-decision-aware-confidence-in-standard-results-20260908` ·
`notes/emberfall-step560-{five-confident,larger}-decision-*-sample-20260908` ·
`notes/emberfall-visual-decision-review-lab-concept-20260908` ·
`decisions/emberfall-visual-decision-review-poc-scope-20260908` ·
`notes/emberfall-generation-2-temperature-4-cohesive-pilot-{preregistration,result}-20260908` ·
`decisions/emberfall-high-confidence-is-not-evidence-of-ignoring-alternatives` ·
`notes/emberfall-sams-bet-one-legitimate-option` ·
`decisions/emberfall-optimize-training-for-game-strength-not-perfect-tactics-20260909` ·
`decisions/emberfall-scale-temperature-4-self-play-to-560-games-20260909` ·
`notes/emberfall-generation-2-temperature-4-step560-preregistration-20260909` ·
`decisions/emberfall-qualify-cuda-for-t4-step560-20260909` ·
`notes/emberfall-generation-2-temperature-4-step560-result-20260909` ·
`decisions/emberfall-do-not-promote-t4-step560-child-20260909` ·
`notes/emberfall-t4-step560-child-decision-review-{bundle,result}-20260909` ·
`decisions/emberfall-confidence-review-judges-expressed-probability-calibration-20260909` ·
`notes/emberfall-terminal-return-credit-assignment-obstacle-20260909` ·
`decisions/emberfall-policy-must-take-immediate-guaranteed-lethal-20260909` ·
`notes/emberfall-lethal-horizon-curriculum-for-learned-planning-20260909` ·
`decisions/emberfall-normalized-entropy-vs-novel-action-analysis-20260909` ·
`notes/emberfall-t4-normalized-entropy-vs-distinct-novelty-result-20260909` ·
`notes/emberfall-entropy-collapse-and-mixture-sampling-assessment-20260909` ·
`decisions/emberfall-mixture-sampling-pilot-eps-015` ·
`decisions/emberfall-mixture-behavior-probability-and-play-key-contract-20260909` ·
`notes/gbrain-codex-partition-was-one-way`
`notes/emberfall-mixture-sampler-implementation-20260909` ·
`notes/emberfall-mixture-sampling-pilot-preregistration-20260909` ·
`notes/emberfall-mixture-sampling-pilot-result-20260909`
