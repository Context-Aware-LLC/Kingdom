# RL project status — GENERATION 1 (historical, through 2026-09-05)

**Archived 2026-09-09.** This describes generation 1 only, pinned to card digest `3e168f24…`, which
the generation-2 freeze left behind. The current record is
[`rl-training-history.md`](rl-training-history.md).
Kept for the generation-1 promotion evidence and the infrastructure inventory; do not read it as
current state.

**2026-10-05:** every "Greedy" and `greedy-ai.v2` below is a bot that no longer exists in code. It
was replaced by `greedy-ai.v3`, which plays differently; see §14 of the history.

---

> **2026-09-05 current result: PROMOTED.** Candidate E (`af027b49…`) is the official generation-1 champion after passing every training, independent holdout and competitive gate. It beat its training parent 85-83 and the previous official champion 86-82. Global mixing of learner rows removed long stretches of outcome-biased shards; reserved MSE improved 0.6315 to 0.5246 and calibration error 0.0611 to 0.0359. All 2,688 measured arena/baseline games were clean. Greedy remains a weakness (78-90), and the champion win is narrow. Cards remain frozen. See the experiment evidence and durable promotion records in `evidence/training-benchmark/2026-09-05-kl-controlled-promotion.md` (private training repo). The earlier results below are historical; interpretations from forced-concession runs are unreliable.

We now have a complete learned-policy tracer from live inference through a durable opponent pool, sampled mixed-opponent updates, and a gated promotion decision. The first actor-critic child was correctly rejected because it did not outperform its parent and it still failed the Greedy sanity check. Two ordinary four-class generations then failed their value/entropy gates. A policy-preserving critic repair passed an independent holdout, and the first fresh actor-critic generation initialized from that critic met its numerical training and reserved-holdout thresholds. Trainer 8.2 closed the auditability gap with a supported actor-critic holdout command that verifies the training gate and atomically claims the sole child evaluation before it runs. The resulting 168-game all-class generation passed every training gate but failed its one-shot reserved holdout because calibration worsened by 0.02747, above the allowed 0.02 increase. It therefore stops before competitive evaluation or promotion, and the official league remains unchanged.
A fair estimate:
- The infrastructure needed for a checkpoint to play matches is complete at tracer-bullet quality.
- A credible self-play system now has the complete repeated-generation control loop at tracer-bullet quality. Policy quality and rollout scale are the remaining work, not league or optimizer-continuation plumbing.
Layer	Where it stands
Deterministic engine and match harness	Ready
Replay capture and validation	Ready
Legal-action enumeration	Ready
Actor-relative observation/action representation	Frozen at feature schema v8
Offline policy/value training	Ready for behavior cloning
Live checkpoint inference	Ready through a long-lived Python JSON/stdio scorer
Model-controlled match runner	Ready for checkpoint-vs-Greedy and checkpoint-vs-checkpoint matches
Self-play learning objective	Ready as a one-pass clipped Monte Carlo actor-critic update with optimizer continuation
Policy arena	Ready with fixed schedules, paired seats, durable reports, and hard gates
Promotion and opponent pool	Ready with a durable league, bounded history, mixed-opponent rollout scheduling, and hard promotion gates


What is genuinely complete
The pipeline can already:
- Run clean, deterministic, seat-balanced matches across all eight classes.
- Capture accepted intents, outcomes, seeds, decks, engine/card versions, and per-seat policy provenance.
- Replay through the authoritative engine and enumerate the complete legal action set.
- Produce actor-relative observations without hidden-information leakage.
- Encode visible entities, static card definitions and rules, public graveyard/deck memory, known opponent-hand memory, ongoing effects, and off-board legal targets.
- Train and checkpoint a masked policy/value network.
- Evaluate that checkpoint against held-out decisions.
- Encode live decisions through the exact representation used by export shards.
- Load a verified checkpoint and return legal-action logits, actor-relative value, and argmax index.
- Keep a verified checkpoint warm behind a narrow JSON/stdio protocol and drive .NET match seats with it.
- Select actions through deterministic checkpoint argmax or reproducible seeded temperature sampling.
- Run fixed class-pair arenas with the candidate in both seats against Greedy and one or more checkpoints.
- Persist a hash-verified champion and bounded checkpoint history, schedule every active checkpoint into sampled rollouts with paired learner seats, and update only champion-authored decisions.
- Use recorded behavior probabilities in a clipped current/behavior surrogate, normalize advantages over the complete eligible rollout population, and persist compatible AdamW state across actor-critic generations.
- Reject or promote a candidate atomically from a durable arena report; rejected candidates and decisions remain auditable.
- Replay every measured game from the same engine and policy seeds, compare action-transcript digests, and gate rejects, stalls, resyncs, replay disagreement, latency, determinism, and crashes.
The large frozen capture is useful: 1,008 greedy-ai.v2 games and 119,900 decisions. Its raw manifest still matches the current engine and wire contracts, so it can be rematerialized with corpus schema v3 and the current exporter without recapturing matches.
Current frozen artifacts
The completed real training artifacts remain frozen at:
- Corpus schema v3
- RL feature schema v8
- Exporter 9.0.0
- Trainer 8.1.0 and checkpoint schema 4
- 1,008 episodes and 119,900 decisions
- A three-epoch CUDA warm-start checkpoint selected at epoch 2
The repository's supported holdout evaluator is now Trainer 8.2.0; it did not regenerate or
rewrite those historical artifacts.
`choiceSequence` remains in export provenance for reconstructing Unearth intents but is absent from model batches and the policy network. The real v8 smoke validated the full corpus, export, checkpoint, and held-out evaluation path.
Shortest critical path
1. Freeze the representation. Complete.
   Remove choiceSequence from model batches, bump the feature contract, and call that the first self-play-ready observation contract.
2. Rematerialize and smoke-test. Complete.
   Rebuild the existing 1,008-game raw capture with the pinned card snapshot, export the final schema, and train for 3–5 epochs. This proves the complete real pipeline and produces the warm-start checkpoint.
3. Build a live decision/scoring seam. Complete at the in-process Python seam.
   The model needs a callable interface equivalent to:
   projected observation
   + player memory
   + legal actions
   + checkpoint/export vocabulary
   → logits, value, selected action index
   `DecisionEncoder` is now shared by export and live inference, and `PolicyValueScorer.score(...)` returns logits, value, and the selected action index. A real v8 checkpoint scored a real corpus decision on CUDA through this seam. The remaining deployment work is the long-lived process and .NET runner connection in step 4.
4. Add a learned-policy match runner. Complete.
   NetSoak and BotSeatRunner now use a per-seat policy interface supporting:
   - greedy-ai.v2
   - deterministic checkpoint argmax
   - seeded checkpoint sampling with temperature
   The implemented runner keeps one Python scorer process warm over a narrow JSON/stdio protocol, reuses the authoritative legal-action enumerator, and maps the selected logit back to the exact .NET intent. The v8 CUDA checkpoint has exercised argmax-vs-Greedy, seeded-sampling-vs-Greedy, and checkpoint-vs-checkpoint paths; arena-grade evidence remains step 5.
5. Build the arena before training on self-play. Complete.
   Run checkpoint-versus-Greedy and checkpoint-versus-checkpoint games with:
   - Fixed deck pairs and seeds
   - Both seat assignments
   - Per-matchup reporting
   - Reject, stall, latency, and determinism gates
   - Checkpoint SHA and sampling configuration as policy identity
   The existing 99.66% policy accuracy is imitation accuracy, not evidence the model wins games.

   The first complete v8 argmax arena ran on CPU over all 28 unordered class pairings with the candidate in both seats. Scorer readiness moved checkpoint loading outside decision latency. Durable JSON and Markdown reports are at `F:\EmberfallData\arenas\v8-step5-all-class-20260831`.

   Verbatim summary generated from the durable JSON report in the arena session:

   ```text
   ARENA_EVIDENCE v1
   build=7e5f4b9cf5b7d0dce1b75d89259e0110bc1fa046
   measured_games=112 verification_replays=112 executions=224
   gates clean=True undecided=0 rejects=0 stalls=0 resyncs=0 replay_disagreements=0 latency_breaches=0 determinism_failures=0 crashes=0
   candidate_vs_greedy wins=0 losses=56 draws=0
   candidate_vs_epoch3 wins=28 losses=28 draws=0
   candidate_latency_ms decisions=582 mean=12.677 p95=16.776 max=61.617
   ```

   Competitive results:
   - Best epoch-2 checkpoint versus `greedy-ai.v2`: 0 wins, 56 losses, 0 draws.
   - Best epoch-2 checkpoint versus the epoch-3 checkpoint: 28 wins, 28 losses, 0 draws — exactly one win per paired seat assignment in every class matchup, so there is no arena evidence that either checkpoint's argmax policy improved.

   This rejects “high held-out imitation accuracy means the checkpoint survives its teacher.” Before spending a generation on actor-critic training, inspect the first policy divergences and their compounding match effect; the arena itself is no longer the uncertainty.
6. Add an actual improvement objective. Complete and hardened for staged scaling.
   The on-policy Monte Carlo actor-critic path now:
   - Sample legal actions from the checkpoint.
   - Record behavior log probability, predicted value, checkpoint SHA, reward, and outcome.
   - Derive raw advantage from episode_return - predicted_value and normalize it once over every eligible learner-authored row.
   - Train a PPO-style clipped surrogate from the current/behavior chosen-action probability ratio at the recorded sampling temperature.
   - Train the existing value head on terminal return.
   - Add entropy regularization to prevent immediate collapse.
   - Report approximate KL, clip fraction, ratio percentiles, entropy, raw advantage statistics, value loss, value bias, mean absolute error, and explained variance.
   - Resume compatible AdamW state from actor-critic parents, deliberately start fresh from supervised/legacy parents, and reject incomplete or incompatible optimizer metadata.
   It also verifies replay/legal-action alignment, reproduces behavior scores from the exact parent before updating, pins the parent model contract across rollout exports, treats all exported partitions as storage shards for one on-policy generation, excludes opponent-authored rows before statistics and updates, and performs exactly one pass so the collected policy does not silently become stale. The initial tracer is in `evidence/training-benchmark/2026-08-31-actor-critic-tracer.md`; the hardened real mixed-opponent result and its verbatim diagnostics are in `evidence/training-benchmark/2026-08-31-actor-critic-hardening.md`.
7. Add promotion and an opponent pool. Complete.
   Never train exclusively against the latest checkpoint. Retain:
   - GreedyAI as a permanent sanity opponent
   - The current champion
   - Several historical checkpoints
   - The new candidate
   Promote only after a seat-balanced arena shows improvement over the parent without severe matchup regressions.

   The durable generation-0 league is at `F:\EmberfallData\leagues\v8-step7-final-v3-20260831`. It pins the RL model contract and card-data digest, archives checkpoints by SHA-256, requires several historical checkpoints from initialization onward, retains Greedy as a permanent sanity opponent, keeps the champion plus a bounded history, and records every promotion evaluation.

   `league-rollout` uses a deterministic round robin whose game count must cover every active checkpoint with the learner in both seats. The real pool smoke covered the champion and both historical checkpoints, exported all three behavior identities, and trainer 7.0 admitted 16 champion-authored decisions while excluding all 6 opponent-authored decisions from statistics and updates.

   `league-promote` arenas the candidate against Greedy, the champion, and every active historical checkpoint, then independently arenas the champion against the shared sanity/history opponents. Promotion requires clean operational gates, direct improvement over the parent, an absolute checkpoint-matchup floor, and no severe drop from the parent's per-matchup baseline. The first actor-critic child failed the direct-improvement gate, so the league correctly rejected it and retained generation 0. The generated evidence summary, exact identities, counters, and competitive results are quoted in `evidence/training-benchmark/2026-08-31-policy-league.md`.
Remaining supporting gaps
- Checkpoint SHA, sampling temperature, behavior log probability, and predicted value survive inference trace, rollout manifests, corpus construction, RL export, and trainer loading per decision. The trainer reproduces the parent scores before updating and uses the recorded probability in the clipped surrogate.
- Warm-start weights and compatible AdamW state now continue across actor-critic descendants. Supervised and older parents take the manifest-recorded fresh-state fallback.
- The initial league now freezes card-data digest `3e168f2406b1b570ae4dde5773bf353bd4ebda67ba83d52fb5d9ecd5e23308d3`. Mixed card-definition revisions remain deliberately unsupported.
The hardened six-game tracer kept the ratio effectively at one and produced zero clipping, but value explained variance was `-0.728`. The guarded progression now stands as follows:

1. The paired-seat four-class stage completed 36/36 games against all three active checkpoints with zero rejects, stalls, resyncs, or replay disagreements. Trainer 7.0 admitted 94 champion-authored decisions, ignored 44 opponent rows, and stayed inside the KL, clip-fraction, entropy, value-loss, and mean-absolute-error bounds. Value explained variance improved to `-0.040` but remained non-positive, so the predeclared stop rule fired. Exact commands, identities, hashes, and diagnostics are in `evidence/training-benchmark/2026-08-31-actor-critic-36-game-pilot.md`.
2. A fresh four-checkpoint league required 48 games per balanced cycle, so the follow-up captured independent 48-game training and reserved holdout populations instead of an invalid 72-game schedule. The sole continued update correctly resumed AdamW state and improved same-population value loss and MAE, but training explained variance remained `-0.0049` and entropy fell 26.94% from the prior generation, beyond the 25% stop. The child was not evaluated on the reserved holdout. Exact commands, hashes, parent baselines, and the no-retry decision are in `evidence/training-benchmark/2026-08-31-actor-critic-fresh-48-holdout.md`.
3. The strict critic-only diagnostic trained only `value_head.*`, preserved policy logits exactly, and passed its independently seeded holdout. Holdout explained variance improved from `-0.230` to `+0.203`, MSE from `1.253` to `0.777`, MAE from `1.009` to `0.807`, and calibration from `0.418` to `0.147`. This proved the representation could support a useful critic but did not create a stronger policy. Exact gates and hashes are in `evidence/training-benchmark/2026-08-31-critic-only-48-holdout.md`.
4. A new four-class actor-critic generation initialized from that repaired critic completed independent training and holdout captures. The numerical thresholds passed, but the generic evaluator did not create an atomic pre-evaluation claim. The evidence record quotes the generated verification output, exact identities, commands, metrics, hashes, and this historical limitation in `evidence/training-benchmark/2026-08-31-critic-initialized-48-holdout.md`.
5. Trainer 8.2 now gives actor-critic holdout evaluation the critic-only path's atomic claim-and-receipt behavior and verifies the complete training gate before claiming. The next staged generation must use that command. Competitive improvement remains unproven: do not promote the experimental child until the all-class stage stays healthy and a seat-balanced arena beats the current champion without failing the permanent Greedy sanity opponent.
6. The preregistered all-class stage captured independent 168-game training and holdout populations over all 28 class pairings. Its sole update passed every training gate, improving training MSE from `0.583` to `0.480` and explained variance from `+0.378` to `+0.497`. The atomic holdout improved MSE, MAE, and explained variance, but calibration worsened from `0.2161` to `0.2436`, a `0.02747` increase against the `0.02` limit. The gate failed, so no arena, retry, or promotion is permitted. Exact commands, hashes, and metrics are in `evidence/training-benchmark/2026-09-04-actor-critic-168-all-class.md`.

Verbatim status from the generated training-gate verifier:

```text
CRITIC_INITIALIZED_TRAINING_GATE v1
parent records=120 mse=0.8388560707229379 mae=0.8426103278674418 ev=0.13100456494457557 entropy=8.530284200484554e-05
child records=120 mse=0.7731566468874613 mae=0.7992016116778056 ev=0.1856970548068586 entropy=8.595419009604181e-05 kl=9.908820985948807e-11 clip_fraction=0.0
gates mse_improved=True mae_improved=True positive_ev=True entropy_within_limit=True kl_within_limit=True clip_within_limit=True finite_diagnostics=True behavior_scores_reproduced=True passed=True
```

Verbatim status from the generated holdout receipt summary:

```text
CRITIC_INITIALIZED_HOLDOUT_EVIDENCE v1
receipt_sha256=14852d0b9bd88e045c7b33fb1517718d4b7c87e6077a76affe0ecc3b0e929bfc one_shot_supported=False claim_created_before_child=False
parent records=122 mse=0.8210125846205631 mae=0.8277585552470972 ev=0.1463915228933007 calibration=0.239928449923936 entropy=8.63785361184082E-05
child records=122 mse=0.7988373727489084 mae=0.8031143655573003 ev=0.1712864469386648 calibration=0.25135394352198137 entropy=7.163146396213379E-05
gates positive_ev=True mse_improved=True mae_improved=True calibration_within_tolerance=True entropy_within_limit=True passed=True
note=Captured from the sole observed generic evaluator stdout after review; the generic command did not atomically enforce or prove one-shot sequencing.
```

The league remains pinned to card-data digest `3e168f2406b1b570ae4dde5773bf353bd4ebda67ba83d52fb5d9ecd5e23308d3`. Current repository card data has moved on, so reproducing this generation must select `F:\EmberfallData\card-data\all-class-56-20260829-v4-memory-short` through `EMBERFALL_CARDS`; the runtime correctly rejects a mismatched corpus.

The live path, rollout provenance, opponent diversity, update isolation, optimizer continuation, determinism, rejection gate, and durable league transitions are proven. Value quality and competitive improvement remain the live uncertainties.
