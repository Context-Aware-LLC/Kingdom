# Emberfall training corpus

The training corpus keeps the server replay journal small and stable. `NetSoak dataset` writes two
raw inputs:

- `replays/<matchId>.jsonl` — seed, accepted intents, and outcome from the existing replay adapter;
- `episode-manifests.jsonl` — ordered decks plus engine, card-data, and bot-policy provenance.

Only clean terminal matches receive a manifest row. The manifest references its replay by a path
relative to the manifest file and pins the replay, card data, build revision, every accepted
intent's before/after projected-state hashes, and both players' terminal projected-state hashes.

## Live human-game capture

The deployed server enables `EMBERFALL_TRAINING_INBOX=/data/training-inbox`. Every clean terminal
match publishes a self-contained replay under `replays/` and appends its manifest row last; voided,
abandoned, or capture-failed matches never receive a row. The ordinary operational journal remains
under `/data/replays`.

From the repository root, start the local watcher before playing:

```powershell
python tooling/scripts/capture_my_games.py
```

It polls Fly, verifies each finalized replay checksum, and appends new rows idempotently to
`F:\EmberfallData\human-inbox`. Stop it with Ctrl+C. Use `--once` to synchronize pending games and
exit, `--out <directory>` to select another inbox, or `EMBERFALL_HUMAN_INBOX` to change the default.
The directory is still raw input: materialize a new immutable corpus version with the normal
`Emberfall.Tools.TrainingCorpus build` command before exporting it for training.

Live capture currently labels ordinary connected seats `human`, roster bots with their policy
identity, and synthetic soak seats `ai-soak`. Capturing all clean live matches is acceptable only
while the deployment has no outside players; add account-scoped opt-in before public matchmaking.

## Capture

The output directory may be anywhere, including an external drive:

```powershell
dotnet run --project src/Emberfall.Tools.NetSoak -c Release -- `
  dataset 200 "E:\EmberfallData\raw\run-001"
```

Use a new directory for each run. Match IDs and manifest rows are unique within a run, and capture
refuses to append a duplicate episode.

For a diverse training capture, `dataset-matrix` expands the requested playable classes into every
unordered pairing, alternates seats inside each pairing, and writes the whole schedule through one
replay store and episode manifest. IDs and seeds are stable across runs with the same configuration:

```powershell
dotnet run --project src/Emberfall.Tools.NetSoak -c Release -- `
  dataset-matrix 200 "E:\EmberfallData\raw\matrix-001" `
  --classes EMBERWARD,TIDECALLER,GRAVEKEEP,VERDANT

# Use every playable class instead:
dotnet run --project src/Emberfall.Tools.NetSoak -c Release -- `
  dataset-matrix 200 "E:\EmberfallData\raw\matrix-all-001" --all-classes

# Pin one curated deck to every ordered primary/secondary pairing:
dotnet run --project src/Emberfall.Tools.NetSoak -c Release -- `
  dataset-matrix 200 "E:\EmberfallData\raw\matrix-cohesive-001" --all-classes `
  --deck-dir "E:\EmberfallData\deck-pools\cohesive-v1" --game-seed-offset 8000000
```

`200` is matches per pairing, not the total, and must be positive and even so each class occupies
each seat equally. An explicit class list must contain at least two unique, non-neutral classes. The
command reports composition and clean/outcome counts for each pairing and
fails the complete run on the same integrity, rejection, stall, replay, and telemetry conditions as
the existing single-matchup `dataset` command.
When `--deck-dir` is present, the directory must provide exactly one legal deck for every selected
ordered class pairing. The command validates the complete pool before the first match, prints its
canonical SHA-256, and records the selected deck lists in every episode manifest.
Use distinct `--game-seed-offset` values to create deterministic training and reserved-holdout
populations with no overlapping match seeds.

## Materialize

The .NET builder, rather than Python, replays through the real Emberfall core and enumerates legal
actions. Every observation is projected for the acting player before it is serialized.

```powershell
dotnet run --project src/Emberfall.Tools.TrainingCorpus -c Release -- build `
  --manifests "E:\EmberfallData\raw\run-001\episode-manifests.jsonl" `
  --cards "unity\Assets\CardData" `
  --out "E:\EmberfallData\corpus\v1"
```

The builder writes `decisions.jsonl`, `episodes.jsonl`, and `dataset-manifest.json` through temporary
`.partial` files. It refuses replay checksum drift, card-data or contract drift, illegal recorded
actions, terminal-state drift, and terminal-outcome disagreement. `dataset-manifest.json` contains
record counts and output checksums and is path-independent for reproducible builds.

Python tensor, analytics, and conversational exporters should consume these model-neutral rows.
They must not reimplement game rules or reconstruct hidden authority state.

Dataset schema v3 adds `knownOpponentHand` to the `playerMemory` object introduced in v2. The four
sorted card-definition multisets are the actor's known remaining deck, the actor's public
graveyard, the opponent's public graveyard, and definitions the actor can still prove are in the
opponent's hand. The last multiset is reconstructed only from per-viewer projected public
transitions: a public card bounced into the opponent's redacted hand adds one occurrence, and a
later public play removes one matching occurrence. Unknown draws never become known, and no hidden
instance id is retained. Sorting removes deck order and hidden-zone identity; the opponent's
unrevealed deck remains excluded.

### Search labels

`rollout --selection fair-search` plays `FairSearchSeatPolicy` in both seats with one checkpoint
and records what each searched decision found. The games are the search's own, so `--temperature`
and `--epsilon` are refused; the budget is `--search-plays`, `--search-sims` and
`--search-rollout-temperature`, `--scorer-processes` sizes the scorer pool (a seat runs a round's
rollouts at once), and `--search-guess-pairing` withholds the opponent's second class from the
world sampler. The checkpoint is told it either way: it is a model input, sent as
`playerMemory.opponentSecondClass` on every score.

```powershell
dotnet run --project src/Emberfall.Tools.NetSoak -c Release -- rollout 2 `
  "F:\EmberfallData\exports\<rl-export>" "F:\EmberfallData\runs\<run>\best-model.pt" `
  "F:\EmberfallData\rollouts\<new-capture>" --all-classes --deck-dir "<deck pool>" `
  --selection fair-search --search-plays 4 --search-sims 32 --workers 4 --scorer-processes 10
```

A searched decision's manifest row carries `searchVersion`, `searchCandidateActionIndices` (one
legal-action index per candidate play), `searchVisits`, `searchMeanValues` (null where a candidate
was never visited), `searchPlayPriors`, `searchChosenActionIndex`, and `searchPlayGroups` with
`searchPlayKeySchemaVersion`: the distinct play of every legal action, from `DistinctPlayKey`.
The fields are all present or all absent, and never beside the `behavior*` fields. A decision the
seat did not search (one distinct play, a pending choice, no world it could build) is an ordinary
row; the command's `[rollout] search:` line counts each kind.

The seat never enumerates concede, so the builder replays a searched decision WITHOUT it: concede
is listed first, and including it would shift every recorded index by one. Ordinary rows of the
same game keep concede, as every earlier corpus does. For each searched row the builder checks
that the candidates are legal, that the chosen index is the replayed intent, and that the play
groups equal a fresh `DistinctPlayKey` computation over the replayed actions. The rule is handed
to `TrainingCorpusBuilder` as a `PlayKeyRule` because this assembly sits beneath
`Emberfall.Policy`; a builder given none refuses a corpus that carries search labels.

## Export

The dependency-free streaming exporters validate this directory and can write all large artifacts
to an external drive. Full format, split, padding, and restart semantics are documented in
`tooling/scripts/training_exporters/README.md`.

```powershell
python -m tooling.scripts.training_exporters export-rl `
  --input "E:\EmberfallData\corpus\v1" `
  --out "E:\EmberfallData\exports\rl-v1" `
  --seed "season-001" --shard-rows 10000

python -m tooling.scripts.training_exporters export-chat `
  --input "E:\EmberfallData\corpus\v1" `
  --out "E:\EmberfallData\exports\chat-v1" `
  --seed "season-001" --shard-rows 10000
```

Decision-row `reward` and `done` describe the immediate transition. `episodeReturn` carries the
eventual `-1`/`0`/`+1` outcome from that row's actor perspective, so the winning player retains a
training signal when the opponent performs the final action (for example, concession).
