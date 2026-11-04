using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Kingdom.Policy;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Training.Corpus;
using static Kingdom.Tools.NetSoak.SoakCli;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// B3 soak-harness seam (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b3-networked-session-design.md §10). Three modes:
    ///   loopback [N]           — N deterministic offline matches vs B1's real MatchSession
    ///   ai [N] [outDir]        — N AI-vs-AI matches played to a NATURAL end, writing the
    ///                            `ai_soak` balance sample (C-G1-8 step 3b) to outDir
    ///   dataset [N] [outDir]   — the same natural-end soak plus durable replays and versioned
    ///                            episode sidecars for TrainingCorpus materialization
    ///   dataset-matrix N outDir (--classes A,B,... | --all-classes)
    ///                          [--deck-dir DIR] [--game-seed-offset N] [--workers N]
    ///                          — one dataset spanning every unordered requested class pairing
    ///   `--workers N` (ai, dataset, dataset-matrix) plays N games at once. Seeds are per game
    ///   and results are written in schedule order, so the output is the same bytes at any N.
    ///   The checkpoint, search and league commands live in Kingdom.Tools.TrainingSoak.
    ///   matrix [N] [outDir]    — the all-pairs balance soak (EF-032): every ordered pair of the
    ///                            playable classes played as its own N-match soak, emitting an
    ///                            N×N win-rate grid. Every cell is gated on cleanliness AND a
    ///                            non-zero intent mix; any tripped cell fails the run (exit 1).
    ///   ladder [N] [outDir] [--hp N]
    ///                          — N matches over the same corpus decks, but with a fresh RANDOM
    ///                            PAIR drawn for every match instead of an even sweep of the grid.
    ///                            This is the shape a player queueing into bot-fill meets; use it
    ///                            when one large sample beats a per-matchup grid.
    ///   corpus [N] [outDir] [--hp N]
    ///                          — the same all-pairs soak over the HAND-AUTHORED balance decks in
    ///                            tests/Kingdom.Core.Tests/Fixtures/BalanceCorpus (EF-030), one
    ///                            axis entry per deck FILE instead of per class. Self-pairs are
    ///                            skipped; same gates, same artifacts.
    ///   live &lt;wss-url&gt; [N]     — N matches over real sockets against a deployed server (scripted
    ///                            mulligan+concede — the connectivity/fault smoke, no AI)
    ///   liveai [N] | liveai &lt;wss-url&gt; [N]  — EF-075: real GreedyAi driving BOTH seats to a decided
    ///                            end over REAL sockets. Bare form boots the real server on a local
    ///                            ephemeral port (self-contained, no deploy); the url form points the
    ///                            same drive at a deployed server (`--pace ms` for its token bucket).
    ///   join &lt;wss-url&gt;         — ONE pass-bot client that pairs with whoever connects next
    ///                            (the Unity editor playing BoardNetworked — b3 §14 step 4 —
    ///                            or a stand-in opponent for a C1 rehearsal). Mulligans, then
    ///                            ends its turns until the match reaches a decided outcome.
    ///   fault                  — deterministic self-test of the EF-076 latency/loss/disconnect
    ///                            injector against a scripted transport; no server needed.
    /// The same injector attaches to `live` via `--latency`/`--jitter`/`--loss`/`--disconnect`.
    /// loopback/live assert (a) zero integrity failures (the client's RC-F8 verify, armed every
    /// snapshot), (b) a decided terminal outcome that AGREES with the server's replay log, and
    /// SURFACE the RC-F7 mis-attribution count (the appliedSeq evidence gate, ruling 2).
    /// Exit 0 = clean.
    /// </summary>
    public static partial class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                string mode = args.Length > 0 ? args[0] : "loopback";
                return mode switch
                {
                    "loopback" => await RunLoopbackAsync(ParseCount(args, 1, 10)),
                    "ai" => await RunAiSoakAsync(ParseCount(args, 1, 200),
                                                 ParseOutDir(args, 2),
                                                 ParseClassFlag(args, "--p0"),
                                                 ParseClassFlag(args, "--p1"),
                                                 captureTraining: false,
                                                 workers: ParseWorkers(args),
                                                 direct: args.Contains("--direct", StringComparer.Ordinal)),
                    "dataset" => await RunAiSoakAsync(ParseCount(args, 1, 200),
                                                      ParseDatasetOutDir(args, 2),
                                                      ParseClassFlag(args, "--p0"),
                                                      ParseClassFlag(args, "--p1"),
                                                      captureTraining: true,
                                                      workers: ParseWorkers(args),
                                                      direct: args.Contains("--direct", StringComparer.Ordinal)),
                    "dataset-matrix" => await RunTrainingMatrixAsync(args),
                    "matrix" => await RunMatrixAsync(ParseCount(args, 1, 40), ParseOutDir(args, 2),
                                                     noSecondary: args.Contains("--no-secondary", StringComparer.Ordinal),
                                                     primaryOnly: args.Contains("--primary-only", StringComparer.Ordinal),
                                                     drafts: (int)ParseUlongFlag(args, "--drafts", 2)),
                    "class-pass" => await RunClassPassAsync(args),
                    "corpus" => await RunCorpusAsync(ParseCount(args, 1, 20), ParseOutDir(args, 2), ParseHeroHp(args)),
                    "ladder" => await RunLadderAsync(ParseCount(args, 1, 200), ParseLadderOutDir(args, 2), ParseHeroHp(args)),
                    "decks" => RunDeckDump(ParseClassFlag(args, "--p0"), ParseClassFlag(args, "--p1")),
                    "trace" => await RunTraceAsync(ParseCount(args, 1, 3),
                                                   ParseClassFlag(args, "--p0"),
                                                   ParseClassFlag(args, "--p1")),
                    "live" when args.Length >= 2 => await RunLiveAsync(new Uri(args[1]), ParseCount(args, 2, 1), FaultProfile.Parse(args)),
                    "live" => Fail("usage: live <wss-url> [matches] [--latency ms] [--jitter ms] [--loss permille] [--disconnect frames]"),
                    "liveai" => await RunLiveAiAsync(args),
                    "onnx-parity" when args.Length >= 3 => OnnxParity.RunFixture(args),
                    "onnx-parity" => Fail("usage: onnx-parity <fixture.jsonl> <bot-model-dir>  (fixture from: python -m tooling.scripts.training_trainer onnx-parity-fixture)"),
                    "fault" => await RunFaultSelfTestAsync(),
                    "join" when args.Length >= 2 => await RunJoinAsync(new Uri(args[1])),
                    "join" => Fail("usage: join <wss-url>"),
                    "probe" when args.Length >= 2 => await RunProbeAsync(new Uri(args[1])),
                    "probe" => Fail("usage: probe <wss-url>"),
                    "stall" when args.Length >= 2 => await RunStallAsync(new Uri(args[1]), ParseCount(args, 2, 40)),
                    "stall" => Fail("usage: stall <wss-url> [thinkSeconds]"),
                    _ => Fail($"unknown mode '{mode}' — use: loopback [N] | ai [N] [outDir] [--workers N] [--direct] | dataset [N] [outDir] [--workers N] [--direct] | dataset-matrix <matches-per-pair> <outDir> (--classes A,B,... | --all-classes) [--deck-dir DIR] [--game-seed-offset N] [--workers N] [--direct] | matrix [N] [outDir] | class-pass <CLASS> [N] [outDir] [--seed N] | corpus [N] [outDir] [--hp N] | ladder [N] [outDir] [--hp N] | live <wss-url> [N] [--latency ms] [--jitter ms] [--loss permille] [--disconnect frames] | liveai [N] | liveai <wss-url> [N] | join <wss-url> | stall <wss-url> [thinkSeconds] | fault  (checkpoint, arena, rollout and the league commands: Kingdom.Tools.TrainingSoak)"),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SOAK ERROR: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        // ── loopback ────────────────────────────────────────────────────────────────

        private static async Task<int> RunLoopbackAsync(int matches)
        {
            string cardData = FindCardData();
            FileCardDatabase db = FileCardDatabase.LoadDirectory(cardData);
            int integrityFailures = 0, misattributions = 0, undecided = 0, outcomeDisagreements = 0;

            for (int i = 0; i < matches; i++)
            {
                using var peer = new SoakLoopback(db, 0xE17BEEF5EEDUL + (ulong)i, $"soak-{i}");
                int localMisses = 0;
                // Per-seat device tokens (C-G1-8). Stable and clearly synthetic: ai_soak is a
                // first-class telemetry sample source (telemetry-spec §1 `source`), so its matches
                // must be attributable — but to two DISTINCT seats, or per-account rollups collapse.
                var opts = new ClientOptions
                {
                    OnMisattribution = _ => Interlocked.Increment(ref localMisses),
                };
                using var c0 = peer.Client(0, opts with { DeviceToken = SoakDevice(0) });
                using var c1 = peer.Client(1, opts with { DeviceToken = SoakDevice(1) });
                peer.Bind(c0, c1);

                Task s0 = c0.ConnectAsync(new Uri("loopback://soak"));
                Task s1 = c1.ConnectAsync(new Uri("loopback://soak"));
                peer.Start();
                await Task.WhenAll(s0, s1);

                // Scripted drive to a decided outcome (real AI is the F6/Phase-3 driver).
                Task<CommandResult> m0 = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
                Task<CommandResult> m1 = c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));
                await peer.RelayAsync();
                await Task.WhenAll(m0, m1);

                _ = c1.SubmitAsync(new ConcedeIntent(c1.Player));
                await peer.RelayAsync();

                integrityFailures += c0.ResyncCount + c1.ResyncCount;
                misattributions += localMisses;
                if (c0.Current.Outcome == MatchOutcome.InProgress) undecided++;

                MatchOutcome? logged = peer.Replay.Matches[peer.Id.Value].Outcome;
                if (logged is null || logged.Value != c0.Current.Outcome) outcomeDisagreements++;
            }

            return Report("loopback", matches, integrityFailures, misattributions, undecided, outcomeDisagreements);
        }

        // ── ai (the balance sample — C-G1-8 step 3b) ──────────────────────────────

        private const string DefaultSoakOut = "artifacts/ai-soak";
        private const string DefaultDatasetOut = "artifacts/training-corpus-raw";

        private static Task<int> RunTrainingMatrixAsync(string[] args)
        {
            if (args.Length < 3 || !int.TryParse(args[1], out int matchesPerPairing))
                throw new ArgumentException(
                    "usage: dataset-matrix <positive-even-matches-per-pair> <outDir> "
                    + "(--classes A,B,... | --all-classes) "
                    + "[--deck-dir DIR] [--game-seed-offset N] [--workers N]");
            bool allClasses = args.Length >= 4
                && string.Equals(args[3], "--all-classes", StringComparison.Ordinal);
            bool explicitClasses = args.Length >= 5
                && string.Equals(args[3], "--classes", StringComparison.Ordinal);
            if (!allClasses && !explicitClasses)
                throw new ArgumentException(
                    "usage: dataset-matrix <positive-even-matches-per-pair> <outDir> "
                    + "(--classes A,B,... | --all-classes) "
                    + "[--deck-dir DIR] [--game-seed-offset N] [--workers N]");
            string outDir = args[2];
            if (string.IsNullOrWhiteSpace(outDir))
                throw new ArgumentException("dataset-matrix outDir must not be empty.");

            int nextOption = allClasses ? 4 : 5;
            string? deckDirectory = null;
            ulong gameSeedOffset = 0;
            int workers = 1;
            bool direct = false;
            bool sawDeckDirectory = false, sawGameSeedOffset = false, sawWorkers = false;
            for (int i = nextOption; i < args.Length; i += 2)
            {
                string option = args[i];
                if (string.Equals(option, "--direct", StringComparison.Ordinal))
                {
                    if (direct) throw new ArgumentException("Duplicate dataset-matrix option '--direct'.");
                    direct = true;
                    i--;   // a bare switch: the next option starts one argument on, not two
                    continue;
                }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{option} requires a value.");
                string value = args[i + 1];
                switch (option)
                {
                    case "--deck-dir" when !sawDeckDirectory:
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--deck-dir must not be empty.");
                        deckDirectory = value;
                        sawDeckDirectory = true;
                        break;
                    case "--game-seed-offset" when !sawGameSeedOffset:
                        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                            out gameSeedOffset))
                            throw new ArgumentException(
                                "--game-seed-offset must be an unsigned integer.");
                        sawGameSeedOffset = true;
                        break;
                    case "--workers" when !sawWorkers:
                        workers = ValidateWorkers(value);
                        sawWorkers = true;
                        break;
                    case "--deck-dir":
                    case "--game-seed-offset":
                    case "--workers":
                        throw new ArgumentException($"Duplicate dataset-matrix option '{option}'.");
                    default:
                        throw new ArgumentException($"Unknown dataset-matrix option '{option}'.");
                }
            }

            IReadOnlyList<CardClass> classes;
            if (allClasses)
            {
                classes = TrainingCapturePlan.AllPlayableClasses();
            }
            else
            {
                var parsed = new List<CardClass>();
                string[] tokens = args[4].Split(',', StringSplitOptions.None);
                if (tokens.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("--classes must not contain empty entries.");
                foreach (string token in tokens)
                {
                    if (!Enum.TryParse(token.Trim(), ignoreCase: true, out CardClass cardClass))
                        throw new ArgumentException($"Unknown capture class '{token.Trim()}'.");
                    parsed.Add(cardClass);
                }
                classes = parsed;
            }

            TrainingCapturePlan plan = TrainingCapturePlan.Create(
                matchesPerPairing, classes, gameSeedOffset);
            return RunAiSoakAsync(
                plan.Games.Count, outDir, null, null, captureTraining: true, capturePlan: plan,
                deckDirectory: deckDirectory, workers: workers, direct: direct);
        }

        /// <summary>
        /// N AI-vs-AI matches on the real <see cref="Kingdom.Net.Server.MatchSession"/>, each played
        /// to a natural end, emitting one `match_complete` row per match into a
        /// <see cref="JsonlTelemetrySink"/>. This is the ≥200-match sample of telemetry-spec §1, and
        /// it is ground LOCALLY on purpose (decision 2026-07-22): card balance is a property of the
        /// deterministic rules engine, so the numbers are bit-identical to what the Fly box would
        /// produce, and Fly's job is the separate, much smaller durability proof.
        ///
        /// <para><b>Seats alternate every match.</b> Player 0 moves first and player 1 gets the spark
        /// token; both are real advantages of unknown size. Pinning EMBERWARD to seat 0 for the whole
        /// run would fold that seat advantage into the class win-rate and there would be no way to
        /// separate them afterwards — the sample would look clean and mean something else. Alternating
        /// makes the seat column its own readable number.</para>
        ///
        /// <para><b>Every row is stamped 1970-01-01T00:16:40Z, and that is deliberate.</b> The
        /// loopback runs on a synthetic clock that never advances, so `endedAtUtc` is the harness
        /// epoch rather than a wall time. Reaching for <c>DateTimeOffset.UtcNow</c> would make the
        /// column look real at the cost of the property this whole split rests on — same seed, same
        /// run, byte-identical wherever it is ground. An obviously synthetic stamp cannot be mistaken
        /// for a ladder row; a plausible one could. Order soak rows by `matchId`, not by time.</para>
        /// </summary>
        private static async Task<int> RunAiSoakAsync(int matches, string outDir,
                                                      CardClass? p0Class, CardClass? p1Class,
                                                      bool captureTraining,
                                                      TrainingCapturePlan? capturePlan = null,
                                                      string? deckDirectory = null,
                                                      int workers = 1,
                                                      bool direct = false)
        {
            if (capturePlan is not null && !captureTraining)
                throw new ArgumentException("A training capture plan requires captureTraining=true.");
            string cardData = FindCardData();
            FileCardDatabase db = FileCardDatabase.LoadDirectory(cardData);
            (MatchSetup evenSeats, MatchSetup oddSeats) = ResolveSoakSetups(p0Class, p1Class, db);
            PolicyArenaDeckPool? deckPool = capturePlan is not null && deckDirectory is not null
                ? PolicyArenaDeckPool.Load(
                    deckDirectory,
                    db,
                    capturePlan.Pairings
                        .SelectMany(pairing => new[] { pairing.FirstClass, pairing.SecondClass }))
                : null;
            Dictionary<int, (MatchSetup Even, MatchSetup Odd)>? plannedSetups = capturePlan?.Pairings
                .Select((pairing, index) => new
                {
                    Index = index,
                    Setups = deckPool is null
                        ? ResolveSoakSetups(pairing.FirstClass, pairing.SecondClass, db)
                        : (deckPool.Resolve(pairing.FirstClass, pairing.SecondClass),
                            deckPool.Resolve(pairing.SecondClass, pairing.FirstClass)),
                })
                .ToDictionary(x => x.Index, x => (x.Setups.Item1, x.Setups.Item2));
            var validator = new CommandProcessor(db);
            var policies = new ISeatPolicy[]
            {
                new GreedySeatPolicy(validator, db),
                new GreedySeatPolicy(validator, db),
            };
            // One fsync per row is the committer's whole cost once a game takes a few
            // milliseconds, and this run can be run again. Both files are flushed once, below.
            var sink = new JsonlTelemetrySink(outDir, EnvLabel(), fsyncEachWrite: false);
            var attribution = new MatchAttribution(
                AccountId.FromDeviceToken(SoakDevice(0)),
                AccountId.FromDeviceToken(SoakDevice(1)),
                TelemetrySource.AiSoak);
            JsonlReplayStore? durableReplay = captureTraining
                ? new JsonlReplayStore(Path.Combine(outDir, "replays"))
                : null;
            JsonlEpisodeManifestWriter? manifestWriter = captureTraining
                ? new JsonlEpisodeManifestWriter(outDir, fsyncEachRow: false)
                : null;
            string? cardDataHash = captureTraining ? CorpusFiles.Sha256CardData(cardData) : null;
            string? buildRevision = captureTraining ? ResolveBuildRevision(cardData) : null;

            Console.WriteLine($"[ai] {matches} match(es) → {sink.Path}");
            if (direct)
                Console.WriteLine("[ai] table: direct (rules core only; this run cannot find a desync)");
            if (captureTraining)
                Console.WriteLine($"[dataset] replays → {Path.Combine(outDir, "replays")}; manifests → {manifestWriter!.Path}");
            if (capturePlan is null)
            {
                Console.WriteLine($"[ai] matchup: {evenSeats.P0Class} vs {evenSeats.P1Class}"
                                  + $"  ({evenSeats.P0Deck.Count}-card decks, {evenSeats.HeroMaxHealth} HP, seats alternate each match)");
            }
            else
            {
                Console.WriteLine($"[dataset-matrix] {capturePlan.Pairings.Count} pairing(s), "
                                  + $"{capturePlan.Games.Count} total match(es)");
                Console.WriteLine($"[dataset-matrix] first game seed: {capturePlan.Games[0].Seed}");
                if (deckPool is not null)
                    Console.WriteLine($"[dataset-matrix] deck pool: {deckPool.SourceDirectory} "
                                      + $"(sha256 {deckPool.Sha256})");
                foreach (TrainingPairing pairing in capturePlan.Pairings)
                    Console.WriteLine($"  {pairing.FirstClass} vs {pairing.SecondClass}: "
                                      + $"{pairing.Matches} match(es), seats alternate");
            }

            int integrityFailures = 0, misattributions = 0, undecided = 0, outcomeDisagreements = 0;
            int rejects = 0, capped = 0, totalTurns = 0, p0Wins = 0, draws = 0;
            int terminalRaceRejects = 0;
            // Intent mix. Not decoration: the 2026-07-22 invalid run happened because the driver
            // SKIPPED cards it could not aim, and a skip leaves no trace anywhere — no reject, no
            // error, just a quieter game. A driver that silently does less is indistinguishable from
            // a driver with less to do, which is the same ambiguity DroppedRows exists to break.
            int aimedPlays = 0, barePlays = 0, minionAttacks = 0, heroSwings = 0, heroPowers = 0;
            var rejectReasons = new List<string>();
            var winsByClass = new Dictionary<CardClass, int>();
            var playedByClass = new Dictionary<CardClass, int>();
            int pairingCount = capturePlan?.Pairings.Count ?? 0;
            var pairingFirstWins = new int[pairingCount];
            var pairingSecondWins = new int[pairingCount];
            var pairingDraws = new int[pairingCount];
            var pairingFailures = new int[pairingCount];

            // A game is PLAYED here, on whichever worker is free, and touches nothing shared but the
            // card database and its own replay file. Everything that has an order -- the tallies,
            // the telemetry file, the manifest, the console -- happens in Commit below.
            async Task<SoakGame> PlayAsync(int i, CancellationToken token)
            {
                var game = new SoakGame();
                TrainingCaptureGame? planned = capturePlan?.Games[i];
                bool swap = planned is null ? (i & 1) == 1 : (planned.MatchIndex & 1) == 1;
                (MatchSetup Even, MatchSetup Odd) setups = planned is null
                    ? (evenSeats, oddSeats)
                    : plannedSetups![planned.PairingIndex];
                MatchSetup setup = swap ? setups.Odd : setups.Even;
                CardClass class0 = setup.P0Class, class1 = setup.P1Class;
                string matchId = planned?.MatchId ?? $"ai-soak-{i}";
                if (manifestWriter?.Contains(matchId) == true)
                    throw new InvalidOperationException(
                        $"Dataset output already contains '{matchId}'. Choose an empty output directory.");
                game.Class0 = class0;
                game.Class1 = class1;

                ulong seed = planned?.Seed ?? 0xE17BEEF5EEDUL + (ulong)i;
                using ISoakTable table = direct
                    ? new DirectSoakTable(db, seed, matchId, setup, game.Telemetry, attribution, durableReplay)
                    : new LoopbackSoakTable(db, seed, matchId, setup, game.Telemetry, attribution,
                                            durableReplay, SoakDevice(0), SoakDevice(1));
                int matchRejects = 0;
                bool matchCapped = false;
                var decisionHashes = new List<EpisodeDecisionHash>();
                await table.StartAsync();

                int steps = 0;
                while (table.View(0).Outcome == MatchOutcome.InProgress)
                {
                    if (steps++ >= IntentCap) { matchCapped = true; break; }

                    bool acted = false;
                    for (int seat = 0; seat < 2; seat++)
                    {
                        GameState view = table.View(seat);
                        PlayerId player = table.Player(seat);
                        PlayerIntent? intent = await policies[seat].ChooseAsync(view, player, token);
                        if (intent is null) continue;
                        string? beforeObservationHash = captureTraining
                            ? table.ViewHash(seat)
                            : null;

                        switch (intent)
                        {
                            case PlayCardIntent p:
                                if (p.Targets.Count > 0) game.AimedPlays++; else game.BarePlays++;
                                break;
                            case AttackIntent a:
                                if (a.Attacker == view.PlayerOf(player).Hero.Id) game.HeroSwings++;
                                else game.MinionAttacks++;
                                break;
                            case HeroPowerIntent:
                                game.HeroPowers++;
                                break;
                        }

                        CommandResult r = await table.SubmitAsync(seat, intent);
                        if (!r.Accepted)
                        {
                            // Every intent was gated through CanExecute against this client's own
                            // view before it was sent, so a reject is not the AI guessing — it is the
                            // validator and the server disagreeing. Keep the reason and the match:
                            // a bare count cannot be told apart from a flaky harness.
                            //
                            // NOTE (EF-032): this path shares the matrix's benign end-of-match race —
                            // a GameOver reject when the OTHER seat's lethal ended the match before this
                            // already-chosen intent reached the server. RunMatchupAsync classifies that
                            // reason apart (see its reject handler); this `ai` path still counts it as a
                            // reject, which is why a single `ai` run can occasionally read one reject at
                            // the very end. Left as-is deliberately so the sanctioned ai_soak sample is
                            // not disturbed; fold in the same GameOver check here if that ever bites.
                            if (capturePlan is not null && r.Reason == RejectReason.GameOver)
                            {
                                game.TerminalRaceRejects++;
                            }
                            else
                            {
                                matchRejects++;
                                game.RejectReasons.Add($"m{i} {intent.GetType().Name} -> {r.Reason}");
                            }
                        }
                        else if (captureTraining)
                        {
                            decisionHashes.Add(new EpisodeDecisionHash(
                                decisionHashes.Count,
                                player.Value,
                                beforeObservationHash!,
                                table.ViewHash(seat)));
                        }
                        acted = true;
                    }

                    // Neither seat has a move and the match has not ended: a stall, not a stalemate.
                    // Counted as capped rather than looped on, for the same reason as IntentCap.
                    if (!acted) { matchCapped = true; break; }
                }

                MatchOutcome outcome = table.View(0).Outcome;
                MatchOutcome? logged = table.LoggedOutcome;
                int localMisses = table.Misattributions;
                game.Planned = planned;
                game.Outcome = outcome;
                game.Resyncs = table.Resyncs;
                game.Misattributions = localMisses;
                game.Turns = table.View(0).TurnNumber;
                game.Rejects = matchRejects;
                game.Capped = matchCapped;
                game.OutcomeDisagrees = logged is null || logged.Value != outcome;

                // Clean as far as the game itself can tell. Whether its telemetry rows landed is
                // only known when they are written, in Commit, and a capture episode needs both.
                game.PlayedClean = outcome != MatchOutcome.InProgress
                    && logged == outcome
                    && game.Resyncs == 0
                    && localMisses == 0
                    && matchRejects == 0
                    && !matchCapped;
                if (captureTraining && game.PlayedClean)
                {
                    string replayPath = durableReplay!.PathFor(table.Id);
                    game.Manifest = new EpisodeManifest(
                        EpisodeManifest.CurrentSchemaVersion,
                        matchId,
                        seed,
                        table.Player(0).Value,
                        table.Player(1).Value,
                        setup.P0Class,
                        setup.P1Class,
                        setup.P0Deck,
                        setup.P1Deck,
                        setup.HeroMaxHealth,
                        setup.SparkTokenId,
                        EpisodeManifest.CurrentEngineContractVersion,
                        ProtocolContract.Version,
                        buildRevision!,
                        cardDataHash!,
                        GreedyAi.PolicyVersion,
                        GreedyAi.PolicyVersion,
                        Path.Combine("replays", Path.GetFileName(replayPath)).Replace('\\', '/'),
                        CorpusFiles.Sha256File(replayPath),
                        table.ViewHash(0),
                        table.ViewHash(1),
                        decisionHashes,
                        outcome);
                }
                return game;
            }

            // Called once per game, in schedule order, never concurrently.
            bool Commit(int i, SoakGame game)
            {
                TrainingCaptureGame? planned = game.Planned;
                MatchOutcome outcome = game.Outcome;
                Bump(playedByClass, game.Class0);
                Bump(playedByClass, game.Class1);
                aimedPlays += game.AimedPlays;
                barePlays += game.BarePlays;
                minionAttacks += game.MinionAttacks;
                heroSwings += game.HeroSwings;
                heroPowers += game.HeroPowers;
                terminalRaceRejects += game.TerminalRaceRejects;
                rejects += game.Rejects;
                rejectReasons.AddRange(game.RejectReasons);
                if (game.Capped) capped++;
                integrityFailures += game.Resyncs;
                misattributions += game.Misattributions;
                totalTurns += game.Turns;
                if (outcome == MatchOutcome.InProgress) undecided++;
                if (game.OutcomeDisagrees) outcomeDisagreements++;

                int droppedRowsBefore = sink.DroppedRows;
                game.Telemetry.ReplayInto(sink);
                bool telemetryDropped = sink.DroppedRows != droppedRowsBefore;
                bool episodeClean = game.PlayedClean && (capturePlan is null || !telemetryDropped);
                if (captureTraining && episodeClean) manifestWriter!.Append(game.Manifest!);

                switch (outcome)
                {
                    case MatchOutcome.Player0Win: p0Wins++; Bump(winsByClass, game.Class0); break;
                    case MatchOutcome.Player1Win: Bump(winsByClass, game.Class1); break;
                    case MatchOutcome.Draw: draws++; break;
                }
                if (planned is not null)
                {
                    if (!episodeClean) pairingFailures[planned.PairingIndex]++;
                    else if (outcome == MatchOutcome.Draw) pairingDraws[planned.PairingIndex]++;
                    else
                    {
                        CardClass winner = outcome == MatchOutcome.Player0Win ? game.Class0 : game.Class1;
                        if (winner == planned.FirstClass) pairingFirstWins[planned.PairingIndex]++;
                        else pairingSecondWins[planned.PairingIndex]++;
                    }
                }

                if ((i + 1) % 25 == 0) Console.WriteLine($"[ai] {i + 1}/{matches} …");
                return true;
            }

            if (workers > 1) Console.WriteLine($"[ai] workers: {workers}");
            long started = Stopwatch.GetTimestamp();
            await OrderedParallel.RunAsync(matches, workers, PlayAsync, Commit);
            sink.FlushToDisk();
            manifestWriter?.FlushToDisk();
            Console.WriteLine(ThroughputLine("ai", matches, workers, Stopwatch.GetElapsedTime(started)));

            int decided = matches - undecided;
            Console.WriteLine();
            Console.WriteLine($"=== NetSoak [ai_soak] — {matches} match(es) ===");
            Console.WriteLine($"  rows written                 : {matches - sink.DroppedRows}  (dropped {sink.DroppedRows})");
            if (sink.LastError is not null)
                Console.WriteLine($"  last sink error              : {sink.LastError.GetType().Name}: {sink.LastError.Message}");
            Console.WriteLine($"  integrity failures (resyncs) : {integrityFailures}");
            Console.WriteLine($"  rejected intents             : {rejects}");
            if (capturePlan is not null)
                Console.WriteLine($"  benign terminal-race rejects : {terminalRaceRejects}");
            for (int r = 0; r < rejectReasons.Count && r < 20; r++)
                Console.WriteLine($"      {rejectReasons[r]}");
            Console.WriteLine($"  capped / stalled matches     : {capped}");
            Console.WriteLine($"  undecided outcomes           : {undecided}");
            Console.WriteLine($"  replay-outcome disagreements : {outcomeDisagreements}");
            Console.WriteLine($"  RC-F7 mis-attributions       : {misattributions}");
            Console.WriteLine($"  draws                        : {draws}");
            Console.WriteLine($"  mean turns                   : {Tenths(totalTurns, matches)}");
            Console.WriteLine("  --- intent mix (zero in any row means the driver cannot reach that verb) ---");
            Console.WriteLine($"  cards played, untargeted     : {barePlays}");
            Console.WriteLine($"  cards played, aimed          : {aimedPlays}");
            Console.WriteLine($"  minion attacks               : {minionAttacks}");
            Console.WriteLine($"  hero swings (weapon)         : {heroSwings}");
            Console.WriteLine($"  hero powers                  : {heroPowers}");
            Console.WriteLine("  --- balance ---");
            foreach (var kv in playedByClass)
                Console.WriteLine($"  {kv.Key,-12} {Get(winsByClass, kv.Key),4} W / {kv.Value,4} played   {Pct(Get(winsByClass, kv.Key), kv.Value)}");
            Console.WriteLine($"  first seat (p0) {p0Wins,4} W / {matches,4} played   {Pct(p0Wins, matches)}   <- seat advantage, read separately");
            if (capturePlan is not null)
            {
                Console.WriteLine("  --- pairing outcomes ---");
                for (int i = 0; i < capturePlan.Pairings.Count; i++)
                {
                    TrainingPairing pairing = capturePlan.Pairings[i];
                    Console.WriteLine($"  {pairing.FirstClass} vs {pairing.SecondClass}: "
                                      + $"{pairingFirstWins[i]}-{pairingSecondWins[i]}"
                                      + $" draws={pairingDraws[i]} failures={pairingFailures[i]}");
                }
            }

            bool clean = integrityFailures == 0 && undecided == 0 && outcomeDisagreements == 0
                         && capped == 0 && rejects == 0 && sink.DroppedRows == 0;
            Console.WriteLine(clean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            _ = decided;
            return clean ? 0 : 1;
        }

        // ── matrix (EF-032: the all-pairs balance soak) ────────────────────────────

        private const string DefaultMatrixOut = "artifacts/balance-matrix";

        /// <summary>Every deck-legal class, in enum order — the axes of the balance matrix.</summary>
        private static CardClass[] PlayableClasses()
            => Enum.GetValues<CardClass>().Where(c => c != CardClass.NEUTRAL).ToArray();

        /// <summary>
        /// One class against each other primary class, with BOTH decks rebuilt every match around a
        /// randomly drawn secondary (Sam, 2026-10-05). The matrix answers "how does this class do
        /// against that one" with one deck per cell; this asks the same of the class itself, so a
        /// result cannot be one lucky build or one favourable pairing. Seats alternate per match.
        /// </summary>
        private static async Task<int> RunClassPassAsync(string[] args)
        {
            if (args.Length < 2 || !Enum.TryParse(args[1], ignoreCase: true, out CardClass subject)
                || subject == CardClass.NEUTRAL)
                return Fail("usage: class-pass <CLASS> [matches-per-opponent] [outDir] [--seed N]");

            int perOpponent = ParseCount(args, 2, 10);
            string outDir = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
                ? args[3] : $"artifacts/class-pass-{subject}";
            ulong seed = ParseUlongFlag(args, "--seed", 0xC1A55UL);

            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            var validator = new CommandProcessor(db);
            var sink = new JsonlTelemetrySink(outDir, EnvLabel());
            var attribution = new MatchAttribution(
                AccountId.FromDeviceToken(SoakDevice(0)),
                AccountId.FromDeviceToken(SoakDevice(1)),
                TelemetrySource.AiSoak);
            var rng = new DeterministicRandom(seed);
            CardClass[] classes = PlayableClasses();

            Console.WriteLine($"[class-pass] {subject} ({GreedyAi.PolicyVersion}), {perOpponent} match(es) per opponent, "
                              + $"random secondary both sides, seed {seed} → {sink.Path}");

            var rows = new List<(CardClass opponent, int wins, int played)>();
            bool allClean = true;
            ulong matchIndex = 0;
            foreach (CardClass opponent in classes)
            {
                if (opponent == subject) continue;
                int wins = 0, played = 0;
                for (int i = 0; i < perOpponent; i++)
                {
                    matchIndex++;
                    CardClass subjectSecondary = RandomSecondary(subject, classes, rng, db);
                    CardClass opponentSecondary = RandomSecondary(opponent, classes, rng, db);
                    IReadOnlyList<int> subjectDeck = GenerateClassDeck(subject, subjectSecondary, db, matchIndex);
                    IReadOnlyList<int> opponentDeck = GenerateClassDeck(opponent, opponentSecondary, db, matchIndex);

                    bool subjectFirst = (i & 1) == 0;
                    MatchSetup setup = subjectFirst
                        ? new MatchSetup(subject, subjectDeck, opponent, opponentDeck, heroMaxHealth: 30)
                        : new MatchSetup(opponent, opponentDeck, subject, subjectDeck, heroMaxHealth: 30);
                    MatchupResult r = await RunMatchupAsync(
                        1, setup, setup, db, validator, sink, attribution, seed + matchIndex * 100_003UL,
                        setup.P0Class.ToString(), setup.P1Class.ToString());

                    int won = Get(r.WinsByClass, subject);
                    wins += won;
                    played += Get(r.PlayedByClass, subject);
                    allClean &= r.CoreClean;
                    Console.WriteLine(
                        $"  {subject}/{subjectSecondary,-11} vs {opponent,-11}/{opponentSecondary,-11}"
                        + $"  seat {(subjectFirst ? 0 : 1)}  {(won == 1 ? "WIN " : "loss")}"
                        + $"{(r.CoreClean ? "" : "  NOT CLEAN: " + (r.Crash ?? r.CoreIssues()))}");
                }
                rows.Add((opponent, wins, played));
            }

            Console.WriteLine();
            Console.WriteLine($"  {subject} vs        wins/played   win rate");
            int totalWins = 0, totalPlayed = 0;
            foreach ((CardClass opponent, int wins, int played) in rows)
            {
                Console.WriteLine($"  {opponent,-12}   {wins,3}/{played,-3}      {Pct(wins, played)}");
                totalWins += wins;
                totalPlayed += played;
            }
            Console.WriteLine($"  {"ALL",-12}   {totalWins,3}/{totalPlayed,-3}      {Pct(totalWins, totalPlayed)}");
            Console.WriteLine(allClean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            return allClean ? 0 : 1;
        }

        /// <summary>A secondary the deck generator can actually build <paramref name="primary"/>
        /// with, drawn uniformly from the other playable classes.</summary>
        private static CardClass RandomSecondary(CardClass primary, CardClass[] classes,
            DeterministicRandom rng, ICardDatabase db)
        {
            var options = new List<CardClass>();
            foreach (CardClass c in classes)
                if (c != primary && DefaultDeckGenerator.CanGenerate(primary, c, db, maxSecondaryCopies: MaxSecondaryClassCards))
                    options.Add(c);
            if (options.Count == 0)
                throw new DeckGenerationException($"{primary} has no secondary it can build a legal deck with.");
            return options[rng.NextInt(options.Count)];
        }

        /// <summary>
        /// EF-032 — the all-pairs balance soak. Every ordered pair of the four playable classes is
        /// played as its own <paramref name="matchesPerCell"/>-match soak (seats alternate inside each
        /// cell, so the cell's number is the matchup and not the seat), and the 4×4 win-rate grid is
        /// written to <paramref name="outDir"/> as Markdown + CSV.
        ///
        /// <para><b>Every cell is gated twice.</b> First the same cleanliness one <c>ai</c> run asserts
        /// — no integrity failures, no undecided or replay-disagreeing outcomes, no capped/stalled
        /// matches, no rejects, no dropped telemetry rows. Second, and this is the EF-032 addition, a
        /// <b>non-zero intent mix</b>: the driver must have played cards, attacked with a minion, and
        /// used a hero power. That second gate exists because of the 2026-07-22 harness-artifact
        /// incident — a driver that silently SKIPS the moves it cannot make produces a clean-looking,
        /// meaningless sample, and a win-rate grid built on it is balance noise wearing a number. Weapon
        /// swings and aimed plays are reported but NOT gated: they are class-dependent (a class with no
        /// weapon legitimately never swings), so gating them would fail an honest cell. Any cell that
        /// trips either gate makes the whole run FAIL and exit non-zero.</para>
        ///
        /// <para><b>A cell that THROWS is contained to that cell, and this is not softening the
        /// grammar-frost ruling.</b> An engine/data fault (the interpreter refusing an unresolvable
        /// selector, say) used to escape to <c>Main</c>, so the first bad matchup aborted the run,
        /// discarded every cell already ground, and wrote no artifact at all — the operator learned
        /// one exception message and nothing about the other N²-1 matchups. The throw is still loud:
        /// the cell is marked <c>CRASH</c>, its message is printed and carried into both artifacts, it
        /// can never be CLEAN, and the run still exits non-zero. What changes is that the remaining
        /// cells are still measured, so one broken card yields a complete map of what it breaks
        /// instead of a single stack trace.</para>
        /// </summary>
        /// <param name="noSecondary">`--no-secondary`: every deck draws from its primary class and
        /// NEUTRAL alone, with no secondary-class copies at all (Sam, 2026-10-06). The default keeps
        /// the <see cref="MaxSecondaryClassCards"/> cap, so the two differ by at most 5 cards a deck.</param>
        /// <param name="primaryOnly">`--primary-only`: the primary class ALONE — no secondary and no
        /// NEUTRAL either (Sam, 2026-10-06, "restrict to primary class only"). The 2026-10-06 run
        /// with neutrals in showed every deck about half NEUTRAL, so a class cell was measuring
        /// 15 of its own cards; this measures all 30.</param>
        /// <param name="drafts">`--drafts N`: decks dealt per class per cell, cycled every two games so
        /// each draft plays both seat orders (default 2, Sam, 2026-10-06). `--drafts 1` is the old
        /// one-deck-per-cell matrix.</param>
        private static async Task<int> RunMatrixAsync(int matchesPerCell, string outDir, bool noSecondary = false,
                                                      bool primaryOnly = false, int drafts = 2)
        {
            if (drafts < 1) throw new ArgumentException("--drafts must be at least 1.");
            int maxSecondaryCopies = noSecondary || primaryOnly ? 0 : MaxSecondaryClassCards;
            bool includeNeutral = !primaryOnly;
            // The matrix gets its own folder by default so its telemetry never merges with the
            // sanctioned single-matchup `ai_soak` sample; an explicit outDir still wins.
            if (string.Equals(outDir, DefaultSoakOut, StringComparison.Ordinal)) outDir = DefaultMatrixOut;

            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            var validator = new CommandProcessor(db);
            var sink = new JsonlTelemetrySink(outDir, EnvLabel());
            var attribution = new MatchAttribution(
                AccountId.FromDeviceToken(SoakDevice(0)),
                AccountId.FromDeviceToken(SoakDevice(1)),
                TelemetrySource.AiSoak);

            CardClass[] classes = PlayableClasses();
            Console.WriteLine($"[matrix] {classes.Length}×{classes.Length} = {classes.Length * classes.Length} cells,"
                              + $" {matchesPerCell} match(es) each, {drafts} draft(s) per class"
                              + $"{(primaryOnly ? ", primary-class decks only" : noSecondary ? ", primary + NEUTRAL decks only" : "")}"
                              + $" → {sink.Path}");

            var cells = new List<MatchupResult>();
            int prevDropped = 0, cellIndex = 0;
            bool allClean = true;

            foreach (CardClass a in classes)
                foreach (CardClass b in classes)
                {
                    var dealt = new List<(MatchSetup even, MatchSetup odd)>(drafts);
                    for (ulong reroll = 0; reroll < (ulong)drafts; reroll++)
                        dealt.Add(ResolveSoakSetups(a, b, db, maxSecondaryCopies, reroll, includeNeutral));
                    ulong seedBase = 0xE17BEEF5EEDUL + (ulong)(++cellIndex) * 100_003UL;
                    MatchupResult r = await RunMatchupAsync(matchesPerCell, dealt, db, validator,
                                                            sink, attribution, seedBase,
                                                            a.ToString(), b.ToString());
                    cells.Add(r);

                    int dropped = sink.DroppedRows - prevDropped;
                    prevDropped = sink.DroppedRows;
                    bool cellClean = r.CoreClean && dropped == 0 && r.IntentMixOk;
                    allClean &= cellClean;

                    string why = cellClean ? "" : "  <- " + (r.IntentMixOk ? "" : "intent-mix ") + r.CoreIssues()
                                                           + (dropped > 0 ? $" dropped={dropped}" : "");
                    Console.WriteLine(
                        $"  {a,-11} vs {b,-11}  {Pct(Get(r.WinsByClass, a), Get(r.PlayedByClass, a))}"
                        + $"  plays {r.CardsPlayed,5} atk {r.MinionAttacks,5} hp {r.HeroPowers,4}"
                        + $"   {(cellClean ? "CLEAN" : r.Crash is not null ? "CRASH" : "FAIL")}{why}");
                    for (int rr = 0; rr < r.RejectReasons.Count && rr < 4; rr++)
                        Console.WriteLine($"        {r.RejectReasons[rr]}");
                }

            WriteMatrixArtifacts(outDir, classes, cells, matchesPerCell,
                deckNote: primaryOnly
                    ? " Decks: primary class ONLY (`--primary-only`) — no NEUTRAL, no secondary."
                    : noSecondary
                        ? " Decks: primary class + NEUTRAL only (`--no-secondary`), no secondary-class copies."
                        : $" Decks: primary class + NEUTRAL, plus up to {MaxSecondaryClassCards} copies from the opponent's class.",
                drafts: drafts);

            int terminalRaceRejects = cells.Sum(c => c.TerminalRejects);
            int crashedCells = cells.Count(c => c.Crash is not null);
            Console.WriteLine();
            if (crashedCells > 0)
                Console.WriteLine($"  {crashedCells} cell(s) CRASHED on an engine/data fault — see the "
                                  + "'Cells that CRASHED' section of the artifact for each message.");
            if (terminalRaceRejects > 0)
                Console.WriteLine($"  ({terminalRaceRejects} benign end-of-match GameOver reject(s) across all cells — the "
                                  + "seat that lost the lethal race, not a rules fault)");
            Console.WriteLine($"  matrix → {Path.Combine(outDir, "matrix-winrate.md")}");
            Console.WriteLine(allClean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            return allClean ? 0 : 1;
        }

        /// <summary>
        /// Play <paramref name="matches"/> AI-vs-AI matches of one matchup (seats alternating each
        /// match) on the real MatchSession, tallying outcomes and the intent mix into a
        /// <see cref="MatchupResult"/>. This is the loopback twin of the loop inside
        /// <see cref="RunAiSoakAsync"/> — kept a distinct method so the matrix can run a cell without a
        /// per-match report, and so <c>ai</c>'s sanctioned sample is never disturbed by matrix work.
        /// Telemetry still lands in the shared <paramref name="sink"/>; the caller reads its drop count.
        ///
        /// <para><b><paramref name="p0Label"/>/<paramref name="p1Label"/> name the two CONTENDERS,
        /// which is not always their class.</b> The matrix passes class names and reads the class
        /// tallies; the EF-030 corpus passes deck-file names, because two corpus decks can share a
        /// primary class and a class-keyed tally would silently merge them into one number. Both
        /// tallies are kept so either caller reads the axis it actually asked for.</para>
        /// </summary>
        private static Task<MatchupResult> RunMatchupAsync(
            int matches, MatchSetup evenSeats, MatchSetup oddSeats, ICardDatabase db,
            CommandProcessor validator, JsonlTelemetrySink sink, MatchAttribution attribution, ulong seedBase,
            string p0Label, string p1Label)
            => RunMatchupAsync(matches, new[] { (evenSeats, oddSeats) }, db, validator, sink, attribution,
                               seedBase, p0Label, p1Label);

        /// <summary>
        /// One cell over several drafted deck pairs (Sam, 2026-10-06: "don't run it with the same deck
        /// for every game"). Game <c>i</c> plays draft <c>(i / 2) % drafts.Count</c>, so every draft
        /// gets both seat orders before the next one is dealt and no draft is tied to one seat.
        /// </summary>
        private static async Task<MatchupResult> RunMatchupAsync(
            int matches, IReadOnlyList<(MatchSetup even, MatchSetup odd)> drafts, ICardDatabase db,
            CommandProcessor validator, JsonlTelemetrySink sink, MatchAttribution attribution, ulong seedBase,
            string p0Label, string p1Label)
        {
            var policies = new ISeatPolicy[]
            {
                new GreedySeatPolicy(validator, db),
                new GreedySeatPolicy(validator, db),
            };
            int integrityFailures = 0, misattributions = 0, undecided = 0, outcomeDisagreements = 0;
            int rejects = 0, capped = 0, totalTurns = 0, p0Wins = 0, draws = 0, terminalRejects = 0;
            int aimedPlays = 0, barePlays = 0, minionAttacks = 0, heroSwings = 0, heroPowers = 0;
            var rejectReasons = new List<string>();
            var winsByClass = new Dictionary<CardClass, int>();
            var playedByClass = new Dictionary<CardClass, int>();
            var winsByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
            var playedByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
            var cardPlays = new Dictionary<int, int>();
            string? crash = null;

            for (int i = 0; i < matches; i++)
            {
                try
                {
                    bool swap = (i & 1) == 1;
                    (MatchSetup evenSeats, MatchSetup oddSeats) = drafts[(i / 2) % drafts.Count];
                    MatchSetup setup = swap ? oddSeats : evenSeats;
                    CardClass class0 = setup.P0Class, class1 = setup.P1Class;
                    // The odd setup is (p1Deck, p0Deck), so on a swap the label at seat 0 is p1's.
                    string label0 = swap ? p1Label : p0Label, label1 = swap ? p0Label : p1Label;

                    using var peer = new SoakLoopback(db, seedBase + (ulong)i, $"matrix-{seedBase:x}-{i}",
                                                      setup, sink, attribution);
                    int localMisses = 0;
                    var opts = new ClientOptions { OnMisattribution = _ => Interlocked.Increment(ref localMisses) };
                    using var c0 = peer.Client(0, opts with { DeviceToken = SoakDevice(0) });
                    using var c1 = peer.Client(1, opts with { DeviceToken = SoakDevice(1) });
                    peer.Bind(c0, c1);

                    Task s0 = c0.ConnectAsync(new Uri("loopback://soak"));
                    Task s1 = c1.ConnectAsync(new Uri("loopback://soak"));
                    peer.Start();
                    await Task.WhenAll(s0, s1);

                    var clients = new[] { c0, c1 };
                    int steps = 0;
                    while (c0.Current is not null && c0.Current.Outcome == MatchOutcome.InProgress)
                    {
                        if (steps++ >= IntentCap) { capped++; break; }

                        bool acted = false;
                        foreach (NetworkedMatchSession c in clients)
                        {
                            // The match can END on the FIRST seat's action inside this very iteration
                            // (a lethal attack). The second seat must not then submit the move it chose
                            // against its pre-lethal view — the server correctly rejects that as GameOver,
                            // and whether the seat sees the terminal snapshot in time is async-timing
                            // dependent, so without this guard the reject flakes run to run. Checking here,
                            // not only at the while-top, closes that within-iteration race. It changes no
                            // outcome (the rejected move never applied) — it only stops the soak knocking
                            // on a finished match.
                            if (c0.Current is null || c0.Current.Outcome != MatchOutcome.InProgress) break;

                            PlayerIntent? intent = await policies[c.Seat].ChooseAsync(c.Current, c.Player);
                            if (intent is null) continue;

                            switch (intent)
                            {
                                case PlayCardIntent p:
                                    if (p.Targets.Count > 0) aimedPlays++; else barePlays++;
                                    // Track per-definition play counts so the corpus run can report
                                    // cards that were drawn but never played (NOR-350).
                                    foreach (CardInstance handCard in c.Current.PlayerOf(c.Player).Hand)
                                        if (handCard.Id == p.Card) { Bump(cardPlays, handCard.DefinitionId); break; }
                                    break;
                                case AttackIntent a:
                                    if (a.Attacker == c.Current.PlayerOf(c.Player).Hero.Id) heroSwings++;
                                    else minionAttacks++;
                                    break;
                                case HeroPowerIntent:
                                    heroPowers++;
                                    break;
                            }

                            Task<CommandResult> submitted = c.SubmitAsync(intent);
                            await peer.RelayAsync();
                            CommandResult r = await submitted;
                            if (!r.Accepted)
                            {
                                // A GameOver reject is the benign end-of-match race: the match ended
                                // (typically on the other seat's lethal, in this same iteration) before this
                                // already-chosen intent reached the server, and the client's local snapshot
                                // can lag that terminal state — so the guard above cannot catch every case.
                                // It applied nothing and is not a soak failure; count it apart. A real reject
                                // (anything else) is the validator and the server genuinely disagreeing, and
                                // still fails the cell.
                                if (r.Reason == RejectReason.GameOver) terminalRejects++;
                                else
                                {
                                    rejects++;
                                    rejectReasons.Add($"{p0Label}v{p1Label} m{i} {intent.GetType().Name} -> {r.Reason}");
                                }
                            }
                            acted = true;
                        }

                        if (!acted) { capped++; break; }
                    }

                    MatchOutcome outcome = c0.Current?.Outcome ?? MatchOutcome.InProgress;
                    integrityFailures += c0.ResyncCount + c1.ResyncCount;
                    misattributions += localMisses;
                    totalTurns += c0.Current?.TurnNumber ?? 0;
                    if (outcome == MatchOutcome.InProgress) undecided++;

                    MatchOutcome? logged = peer.Replay.Matches[peer.Id.Value].Outcome;
                    if (logged is null || logged.Value != outcome) outcomeDisagreements++;

                    // Tallied only once the match has actually produced an outcome. A match that threw
                    // never reaches here, so it cannot inflate the win-rate denominator with a game that
                    // nobody played — a crashed cell reports the rate of what it managed to grind.
                    Bump(playedByClass, class0);
                    Bump(playedByClass, class1);
                    Bump(playedByLabel, label0);
                    Bump(playedByLabel, label1);

                    switch (outcome)
                    {
                        case MatchOutcome.Player0Win: p0Wins++; Bump(winsByClass, class0); Bump(winsByLabel, label0); break;
                        case MatchOutcome.Player1Win: Bump(winsByClass, class1); Bump(winsByLabel, label1); break;
                        case MatchOutcome.Draw: draws++; break;
                    }
                }
                catch (Exception ex)
                {
                    // An engine or data fault, not a soak failure the counters can express: the
                    // interpreter refusing an unresolvable selector, a deck that cannot be generated, a
                    // token that does not resolve. Record it against THIS cell and stop grinding this
                    // matchup — the remaining matchups are independent and still worth measuring. The
                    // caller can never call a crashed cell clean, so nothing is being swallowed.
                    crash = $"m{i} {ex.GetType().Name}: {ex.Message}";
                    break;
                }
            }

            // Every draft shares the cell's class pair, so the first one names it.
            return new MatchupResult(drafts[0].even.P0Class, drafts[0].even.P1Class, p0Label, p1Label, matches,
                integrityFailures, misattributions, undecided, outcomeDisagreements, rejects, capped, terminalRejects,
                totalTurns, p0Wins, draws, aimedPlays, barePlays, minionAttacks, heroSwings, heroPowers,
                rejectReasons, winsByClass, playedByClass, winsByLabel, playedByLabel, cardPlays, crash);
        }

        /// <summary>Everything one matchup's soak produced, so the matrix can gate it and tabulate it.</summary>
        private readonly struct MatchupResult
        {
            public readonly CardClass P0Class, P1Class;

            /// <summary>What the two contenders are called on this run's axis — a class name for the
            /// matrix, a deck-file name for the EF-030 corpus. Never assume it parses as a CardClass.</summary>
            public readonly string P0Label, P1Label;

            public readonly int Matches;
            public readonly int IntegrityFailures, Misattributions, Undecided, OutcomeDisagreements, Rejects, Capped, TerminalRejects;
            public readonly int TotalTurns, P0Wins, Draws;
            public readonly int AimedPlays, BarePlays, MinionAttacks, HeroSwings, HeroPowers;
            public readonly IReadOnlyList<string> RejectReasons;
            public readonly Dictionary<CardClass, int> WinsByClass, PlayedByClass;
            public readonly Dictionary<string, int> WinsByLabel, PlayedByLabel;
            /// <summary>Play count per card definition ID across all matches in this cell (NOR-350).</summary>
            public readonly Dictionary<int, int> CardPlays;

            /// <summary>The engine/data exception that stopped this cell, or null if it ran to term.</summary>
            public readonly string? Crash;

            public MatchupResult(CardClass p0, CardClass p1, string p0Label, string p1Label, int matches,
                int integrityFailures, int misattributions, int undecided, int outcomeDisagreements,
                int rejects, int capped, int terminalRejects, int totalTurns, int p0Wins, int draws,
                int aimedPlays, int barePlays, int minionAttacks, int heroSwings, int heroPowers,
                List<string> rejectReasons, Dictionary<CardClass, int> winsByClass, Dictionary<CardClass, int> playedByClass,
                Dictionary<string, int> winsByLabel, Dictionary<string, int> playedByLabel,
                Dictionary<int, int> cardPlays, string? crash)
            {
                Crash = crash;
                WinsByLabel = winsByLabel; PlayedByLabel = playedByLabel;
                P0Label = p0Label; P1Label = p1Label;
                P0Class = p0; P1Class = p1; Matches = matches;
                IntegrityFailures = integrityFailures; Misattributions = misattributions; Undecided = undecided;
                OutcomeDisagreements = outcomeDisagreements; Rejects = rejects; Capped = capped; TerminalRejects = terminalRejects;
                TotalTurns = totalTurns; P0Wins = p0Wins; Draws = draws;
                AimedPlays = aimedPlays; BarePlays = barePlays; MinionAttacks = minionAttacks;
                HeroSwings = heroSwings; HeroPowers = heroPowers;
                RejectReasons = rejectReasons; WinsByClass = winsByClass; PlayedByClass = playedByClass;
                CardPlays = cardPlays;
            }

            public int CardsPlayed => BarePlays + AimedPlays;

            /// <summary>
            /// Same cleanliness a single <c>ai</c> run asserts, minus the shared sink's drops (the
            /// caller owns those). A crashed cell is never clean — the counters below only describe
            /// the matches that ran, so on their own they would read clean for a cell that died on
            /// match 0 having tallied nothing.
            /// </summary>
            public bool CoreClean => Crash is null
                                     && IntegrityFailures == 0 && Undecided == 0 && OutcomeDisagreements == 0
                                     && Capped == 0 && Rejects == 0;

            /// <summary>The non-zero cleanliness counters, for a self-diagnosing FAIL line.</summary>
            public string CoreIssues()
            {
                var parts = new List<string>();
                if (Crash is not null) parts.Add($"CRASH {Crash}");
                if (IntegrityFailures > 0) parts.Add($"integrity={IntegrityFailures}");
                if (Undecided > 0) parts.Add($"undecided={Undecided}");
                if (OutcomeDisagreements > 0) parts.Add($"disagree={OutcomeDisagreements}");
                if (Capped > 0) parts.Add($"capped={Capped}");
                if (Rejects > 0) parts.Add($"rejects={Rejects}");
                return parts.Count == 0 ? "clean-core" : string.Join(" ", parts);
            }

            /// <summary>The EF-032 gate: the driver reached every verb any matchup can reach.</summary>
            public bool IntentMixOk => CardsPlayed > 0 && MinionAttacks > 0 && HeroPowers > 0;
        }

        /// <summary>
        /// Write the win-rate grid (row class vs column class) as Markdown + CSV. The Markdown grid is
        /// the human read; the CSV is the machine one, milli-units so the file stays float-free like
        /// everything it came from. <paramref name="cells"/> is row-major: <c>row*len + col</c>.
        /// </summary>
        private static void WriteMatrixArtifacts(string outDir, CardClass[] classes,
                                                 List<MatchupResult> cells, int matchesPerCell,
                                                 string deckNote = "", int drafts = 1)
        {
            Directory.CreateDirectory(outDir);
            MatchupResult Cell(int ri, int ci) => cells[ri * classes.Length + ci];

            var md = new System.Text.StringBuilder();
            md.Append("# Kingdom balance matrix (EF-032)\n\n");
            md.Append($"All-pairs AI-vs-AI soak — {matchesPerCell} matches per cell, seats alternating,"
                      + $" {drafts} drafted deck(s) per class cycled every two games.{deckNote}\n\n");
            md.Append("Cell = **row class** win-rate vs the **column class** (the mirror diagonal sits near 50% by construction). ");
            md.Append("Synthetic-clock sample, ground locally (decision 2026-07-22); order rows by matchId, never by time.\n\n");
            md.Append("Every cell is gated on cleanliness AND a non-zero intent mix; a benign end-of-match "
                      + "`GameOver` reject (the seat that lost the lethal race) is counted apart in the CSV's "
                      + "`terminal_race_rejects` column and never fails a cell.\n\n");

            md.Append("| row \\ col |");
            foreach (var c in classes) md.Append($" {c} |");
            md.Append("\n|---|");
            foreach (var _ in classes) md.Append("---|");
            md.Append("\n");
            for (int ri = 0; ri < classes.Length; ri++)
            {
                md.Append($"| **{classes[ri]}** |");
                for (int ci = 0; ci < classes.Length; ci++)
                {
                    var cell = Cell(ri, ci);
                    md.Append($" {Pct(Get(cell.WinsByClass, classes[ri]), Get(cell.PlayedByClass, classes[ri]))} |");
                }
                md.Append("\n");
            }

            md.Append("\n## Per-cell soak health\n\n");
            md.Append("| matchup | row winrate | cards | minion atk | hp swings | hero pw | draws | mean turns | gate |\n");
            md.Append("|---|---|---|---|---|---|---|---|---|\n");
            for (int ri = 0; ri < classes.Length; ri++)
                for (int ci = 0; ci < classes.Length; ci++)
                {
                    var cell = Cell(ri, ci);
                    bool clean = cell.CoreClean && cell.IntentMixOk;
                    string gate = clean ? "CLEAN"
                                : cell.Crash is not null ? "CRASH"
                                : (!cell.IntentMixOk ? "intent-mix" : "FAIL");
                    md.Append($"| {classes[ri]} vs {classes[ci]} "
                        + $"| {Pct(Get(cell.WinsByClass, classes[ri]), Get(cell.PlayedByClass, classes[ri]))} "
                        + $"| {cell.CardsPlayed} | {cell.MinionAttacks} | {cell.HeroSwings} | {cell.HeroPowers} "
                        + $"| {cell.Draws} | {Tenths(cell.TotalTurns, cell.Matches)} | {gate} |\n");
                }
            // Crashed cells get their exception spelled out in full. The grid above can only show
            // CRASH in five characters, and an engine/data fault is the one result an operator must
            // be able to act on without re-running the soak to read the message.
            var crashed = new List<(int ri, int ci)>();
            for (int ri = 0; ri < classes.Length; ri++)
                for (int ci = 0; ci < classes.Length; ci++)
                    if (Cell(ri, ci).Crash is not null) crashed.Add((ri, ci));

            if (crashed.Count > 0)
            {
                md.Append($"\n## Cells that CRASHED ({crashed.Count})\n\n");
                md.Append("An engine or data fault, not a balance signal — the matchup stopped grinding at the "
                          + "match named below. These cells' win-rates cover only the matches that completed "
                          + "and must not be read as balance.\n\n");
                foreach (var (ri, ci) in crashed)
                    md.Append($"- **{classes[ri]} vs {classes[ci]}** — `{Cell(ri, ci).Crash}`\n");
            }

            File.WriteAllText(Path.Combine(outDir, "matrix-winrate.md"), md.ToString());

            var csv = new System.Text.StringBuilder();
            csv.Append("row_class,col_class,row_winrate_milli,matches,row_wins,row_played,draws,undecided,"
                       + "capped,rejects,integrity_failures,outcome_disagreements,terminal_race_rejects,"
                       + "cards_played,minion_attacks,hero_swings,hero_powers,mean_turns_tenths,core_clean,intent_mix_ok,crash\n");
            for (int ri = 0; ri < classes.Length; ri++)
                for (int ci = 0; ci < classes.Length; ci++)
                {
                    var cell = Cell(ri, ci);
                    int rw = Get(cell.WinsByClass, classes[ri]);
                    int rp = Get(cell.PlayedByClass, classes[ri]);
                    int milli = rp == 0 ? 0 : rw * 1000 / rp;
                    csv.Append($"{classes[ri]},{classes[ci]},{milli},{cell.Matches},{rw},{rp},{cell.Draws},"
                        + $"{cell.Undecided},{cell.Capped},{cell.Rejects},{cell.IntegrityFailures},{cell.OutcomeDisagreements},{cell.TerminalRejects},"
                        + $"{cell.CardsPlayed},{cell.MinionAttacks},{cell.HeroSwings},{cell.HeroPowers},"
                        + $"{cell.TotalTurns * 10 / Math.Max(1, cell.Matches)},{cell.CoreClean},{cell.IntentMixOk},{CsvQuote(cell.Crash)}\n");
                }
            File.WriteAllText(Path.Combine(outDir, "matrix-winrate.csv"), csv.ToString());
        }

        /// <summary>
        /// RFC4180 field for the one free-text column in the CSV. An exception message routinely
        /// carries commas and quotes, and an unquoted one silently shifts every column after it —
        /// which would make the machine-readable half of the artifact quietly wrong exactly when
        /// something has already gone wrong.
        /// </summary>
        private static string CsvQuote(string? s)
            => string.IsNullOrEmpty(s) ? "" : "\"" + s!.Replace("\"", "\"\"") + "\"";

        // ── ladder (random pairing, the bot-fill shape) ────────────────────────────

        private const string DefaultLadderOut = "artifacts/balance-ladder";

        /// <summary>Fixed base seed, so a ladder run of the same length over the same decks draws the
        /// same sequence of pairings. Its own family, distinct from the matrix's and the corpus's, so
        /// no two modes can ever grind the same match under two different labels in one sample.</summary>
        private const ulong LadderSeedBase = 0x1ADDE5EEDUL;

        /// <summary>
        /// N matches over the corpus decks, drawing a fresh unordered pair uniformly at random for
        /// each one.
        ///
        /// <para><b>What this measures that <see cref="RunCorpusAsync"/> does not.</b> The grid gives
        /// every ordered pair the same number of matches, which is the right shape for reading one
        /// matchup off a cell and the wrong one for asking "how does this roster behave". A player
        /// queueing into bot-fill gets a random opponent — <c>BotPool.TryAcquire</c> draws uniformly
        /// among the idle bots — so a random-pairing sample is the population they actually face, and
        /// it spends its whole budget on that question instead of on 56 separately-underpowered cells.
        /// The trade is real: at N matches over n decks each ordered pair gets about N/(n(n-1)) of
        /// them, so an individual matchup is noisier here than in a grid of the same size. Read the
        /// standings from a ladder run and the matchups from a corpus run.</para>
        ///
        /// <para>Seats are randomised by the draw itself: the first deck drawn takes seat 0. With
        /// enough matches the seat-0 counts printed alongside the standings converge on N·2/n, and a
        /// visible skew there means the draw is broken, not that a deck is strong.</para>
        ///
        /// <para>Gating splits by what each half is a statement about. CLEANLINESS is per match — no
        /// integrity failure, no undecided or replay-disagreeing outcome, no capped/stalled match, no
        /// real reject, no dropped telemetry row — and any tripped match fails the run. The INTENT MIX
        /// is checked once over the whole run, because a single legal decided game can spend every
        /// crystal on cards and never fire a hero power; per match it would fail honest games.</para>
        /// </summary>
        private static async Task<int> RunLadderAsync(int matches, string outDir, int heroMaxHealth)
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            string corpusDir = FindBalanceCorpus();
            var decks = BalanceCorpusLoader.Load(corpusDir, db);
            int n = decks.Count;
            if (n < 2)
                return Fail($"[ladder] {n} deck(s) is not enough to draw a pair — add a second *.deck.json to {corpusDir}.");

            var validator = new CommandProcessor(db);
            var sink = new JsonlTelemetrySink(outDir, EnvLabel());
            var attribution = new MatchAttribution(
                AccountId.FromDeviceToken(SoakDevice(0)),
                AccountId.FromDeviceToken(SoakDevice(1)),
                TelemetrySource.AiSoak);

            Console.WriteLine($"[ladder] {n} deck(s) from {corpusDir}, {heroMaxHealth} hero HP");
            foreach (var d in decks)
                Console.WriteLine($"           {d.label,-28} {d.primary}/{d.secondary}  {d.cardIds.Count} cards");
            Console.WriteLine($"[ladder] {matches} match(es), random pairing → {sink.Path}");

            var rng = new DeterministicRandom(LadderSeedBase);
            var wins = new Dictionary<string, int>(StringComparer.Ordinal);
            var played = new Dictionary<string, int>(StringComparer.Ordinal);
            var seatZero = new Dictionary<string, int>(StringComparer.Ordinal);
            var headToHead = new Dictionary<string, int>(StringComparer.Ordinal);   // "A|B" → A's wins
            var pairPlayed = new Dictionary<string, int>(StringComparer.Ordinal);
            var allCardPlays = new Dictionary<int, int>();
            var failures = new List<string>();
            int draws = 0, terminalRaceRejects = 0, totalTurns = 0, prevDropped = 0;
            int cardsPlayed = 0, minionAttacks = 0, heroSwings = 0, heroPowers = 0;
            bool allClean = true;

            for (int m = 0; m < matches; m++)
            {
                // Uniform over unordered pairs, with the FIRST index taking seat 0. Drawing j in
                // [0, n-2) and stepping it past i is the standard trick for "distinct from i" without
                // a rejection loop — a loop would consume a variable number of RNG draws and make the
                // sequence depend on how often it happened to collide.
                int i = rng.NextInt(n);
                int j = rng.NextInt(n - 1);
                if (j >= i) j++;

                var a = decks[i];
                var b = decks[j];
                var setup = new MatchSetup(a.primary, a.cardIds, b.primary, b.cardIds, heroMaxHealth);

                // Both arguments are the same setup: RunMatchupAsync only alternates seats across the
                // matches WITHIN one call, and each call here is a single match whose seats the draw
                // above has already randomised.
                MatchupResult r = await RunMatchupAsync(1, setup, setup, db, validator, sink,
                                                        attribution, LadderSeedBase + (ulong)m * 100_003UL,
                                                        a.label, b.label);

                int dropped = sink.DroppedRows - prevDropped;
                prevDropped = sink.DroppedRows;

                // CoreClean per match, intent mix across the RUN — deliberately not both per match.
                // IntentMixOk wants a non-zero count in each of cards played, minion attacks and hero
                // powers, which is a statement about a CELL of many matches: one legal, decided game
                // can genuinely spend every crystal on cards and never fire a hero power. Gating each
                // match on it fails honest games and says "intent-mix" about a run that is fine.
                bool clean = r.CoreClean && dropped == 0;
                if (!clean)
                {
                    allClean = false;
                    failures.Add($"m{m} {a.label} vs {b.label}: {r.CoreIssues()}"
                                 + (dropped > 0 ? $" dropped={dropped}" : ""));
                }
                cardsPlayed += r.CardsPlayed;
                minionAttacks += r.MinionAttacks;
                heroSwings += r.HeroSwings;
                heroPowers += r.HeroPowers;

                Bump(played, a.label); Bump(played, b.label);
                Bump(seatZero, a.label);
                Bump(pairPlayed, PairKey(a.label, b.label));
                int aWins = Get(r.WinsByLabel, a.label);
                int bWins = Get(r.WinsByLabel, b.label);
                if (aWins > 0) { Bump(wins, a.label); Bump(headToHead, a.label + "|" + b.label); }
                else if (bWins > 0) { Bump(wins, b.label); Bump(headToHead, b.label + "|" + a.label); }
                else draws++;

                terminalRaceRejects += r.TerminalRejects;
                totalTurns += r.TotalTurns;
                foreach (var kv in r.CardPlays)
                    allCardPlays[kv.Key] = Get2(allCardPlays, kv.Key) + kv.Value;

                if ((m + 1) % 100 == 0)
                    Console.WriteLine($"  ... {m + 1}/{matches} matches, {failures.Count} unclean so far");
            }

            // ── standings ────────────────────────────────────────────────────────
            int labelWidth = decks.Max(d => d.label.Length);
            var ranked = decks.Select(d => d.label)
                              .OrderByDescending(l => Get(played, l) == 0 ? 0 : Get(wins, l) * 1000 / Get(played, l))
                              .ThenBy(l => l, StringComparer.Ordinal)
                              .ToList();

            Console.WriteLine();
            Console.WriteLine($"=== ladder standings — {matches} match(es), random pairing, {heroMaxHealth} HP ===");
            Console.WriteLine($"  {"deck".PadRight(labelWidth)}  played   won   rate   seat0");
            foreach (string label in ranked)
                Console.WriteLine($"  {label.PadRight(labelWidth)}  {Get(played, label),6}  {Get(wins, label),4}"
                                  + $"  {Pct(Get(wins, label), Get(played, label))}  {Get(seatZero, label),6}");
            Console.WriteLine($"  draws {draws}   mean turns {Tenths(totalTurns, matches)}"
                              + $"   expected played/deck {matches * 2 / n}   expected seat0/deck {matches / n}");
            Console.WriteLine($"  intent mix: cards {cardsPlayed}  minion atk {minionAttacks}"
                              + $"  hero swings {heroSwings}  hero powers {heroPowers}");

            // The run-level intent mix, the check RunMatchupAsync's IntentMixOk makes per cell. A
            // sample where one whole category never fired is measuring a driver that cannot use it,
            // not decks — the same reason the matrix and corpus refuse a cell for it.
            bool mixOk = cardsPlayed > 0 && minionAttacks > 0 && heroPowers > 0;
            if (!mixOk)
            {
                allClean = false;
                Console.WriteLine("  INTENT MIX EMPTY — a whole intent category never fired across the run.");
            }

            Console.WriteLine();
            Console.WriteLine("=== head-to-head (row wins / matches drawn) ===");
            Console.WriteLine($"  {"row \\ col".PadRight(labelWidth)} " + string.Join(" ", ranked.Select(l => l.PadLeft(labelWidth))));
            foreach (string row in ranked)
            {
                var cells = new List<string>(ranked.Count);
                foreach (string col in ranked)
                {
                    if (string.Equals(row, col, StringComparison.Ordinal)) { cells.Add("n/a".PadLeft(labelWidth)); continue; }
                    int drawn = Get(pairPlayed, PairKey(row, col));
                    cells.Add($"{Get(headToHead, row + "|" + col)}/{drawn}".PadLeft(labelWidth));
                }
                Console.WriteLine($"  {row.PadRight(labelWidth)} " + string.Join(" ", cells));
            }

            // The same silent-confound detector the corpus run carries: a deck card that was drawn
            // across a whole run and never once played is a DRIVER hole, not a weak card.
            var deckCardIds = new HashSet<int>();
            foreach (var d in decks)
                foreach (int id in d.cardIds) deckCardIds.Add(id);
            var neverPlayed = deckCardIds.Where(id => Get2(allCardPlays, id) == 0).OrderBy(id => id).ToList();

            Console.WriteLine();
            string Named(int id) => db.TryGet(id, out CardDefinition d) ? $"{id,-6} {d.Handle}" : $"{id,-6} <unknown>";
            if (neverPlayed.Count == 0)
                Console.WriteLine("  drawn-but-never-played: (none)");
            else
                foreach (int id in neverPlayed)
                    Console.WriteLine($"  NEVER PLAYED: {Named(id)}");

            WriteLadderArtifacts(outDir, decks, ranked, matches, heroMaxHealth, wins, played, seatZero,
                                 headToHead, pairPlayed, draws, totalTurns, allCardPlays, neverPlayed, failures, db);

            Console.WriteLine();
            if (terminalRaceRejects > 0)
                Console.WriteLine($"  ({terminalRaceRejects} benign end-of-match GameOver reject(s) — the seat that "
                                  + "lost the lethal race, not a rules fault)");
            foreach (string f in failures.Take(10)) Console.WriteLine($"  UNCLEAN {f}");
            if (failures.Count > 10) Console.WriteLine($"  ... and {failures.Count - 10} more unclean match(es)");
            Console.WriteLine($"  ladder → {Path.Combine(outDir, "ladder-standings.md")}");
            Console.WriteLine(allClean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            return allClean ? 0 : 1;
        }

        /// <summary>Order-independent key for one unordered pair, so "A drew B" and "B drew A" tally
        /// into the same bucket.</summary>
        private static string PairKey(string a, string b)
            => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        private static string ParseLadderOutDir(string[] args, int idx)
            => args.Length > idx && !args[idx].StartsWith("--", StringComparison.Ordinal) ? args[idx] : DefaultLadderOut;

        /// <summary>The ladder's twin of <see cref="WriteCorpusArtifacts"/>: a standings table and a
        /// head-to-head grid, plus a row-per-deck CSV for anything that wants to diff two runs.</summary>
        private static void WriteLadderArtifacts(
            string outDir,
            IReadOnlyList<(string label, CardClass primary, CardClass secondary, IReadOnlyList<int> cardIds)> decks,
            IReadOnlyList<string> ranked, int matches, int heroMaxHealth,
            Dictionary<string, int> wins, Dictionary<string, int> played, Dictionary<string, int> seatZero,
            Dictionary<string, int> headToHead, Dictionary<string, int> pairPlayed,
            int draws, int totalTurns, Dictionary<int, int> cardPlays, IReadOnlyList<int> neverPlayed,
            IReadOnlyList<string> failures, ICardDatabase db)
        {
            Directory.CreateDirectory(outDir);
            int n = decks.Count;

            var md = new System.Text.StringBuilder();
            md.Append("# Kingdom bot ladder\n\n");
            md.Append($"{matches} AI-vs-AI matches over the {n} decks below, **a fresh random pair drawn for each match** ")
              .Append($"(not an even sweep of the grid), at {heroMaxHealth} hero HP.\n\n");
            md.Append("This is the population a player queueing into bot-fill meets — `BotPool.TryAcquire` draws ")
              .Append("uniformly among the idle bots. Read STANDINGS off this run; read individual MATCHUPS off a ")
              .Append("`corpus` run, which gives every ordered pair the same number of matches.\n\n");
            md.Append("`seat0` is how often each deck took the first seat. It converges on `matches / decks`; a skew ")
              .Append("there is a broken draw, not a strong deck.\n\n");

            md.Append("| deck | character | cards |\n|---|---|---|\n");
            foreach (var d in decks.OrderBy(d => d.label, StringComparer.Ordinal))
                md.Append($"| `{d.label}` | {d.primary}/{d.secondary} | {d.cardIds.Count} |\n");

            md.Append("\n## Standings\n\n| deck | played | won | rate | seat0 |\n|---|---|---|---|---|\n");
            foreach (string label in ranked)
                md.Append($"| **{label}** | {Get(played, label)} | {Get(wins, label)} | ")
                  .Append($"{Pct(Get(wins, label), Get(played, label)).Trim()} | {Get(seatZero, label)} |\n");
            md.Append($"\nDraws: {draws}. Mean turns: {Tenths(totalTurns, matches)}. ")
              .Append($"Expected played/deck: {matches * 2 / n}. Expected seat0/deck: {matches / n}.\n");

            md.Append("\n## Head-to-head (row wins / matches drawn)\n\n");
            md.Append("| row \\ col | " + string.Join(" | ", ranked) + " |\n");
            md.Append("|---" + string.Concat(Enumerable.Repeat("|---", ranked.Count)) + "|\n");
            foreach (string row in ranked)
            {
                md.Append($"| **{row}** ");
                foreach (string col in ranked)
                    md.Append(string.Equals(row, col, StringComparison.Ordinal)
                        ? "| n/a "
                        : $"| {Get(headToHead, row + "|" + col)}/{Get(pairPlayed, PairKey(row, col))} ");
                md.Append("|\n");
            }

            md.Append("\n## Per-card play counts\n\n");
            var deckCardIds = new SortedSet<int>();
            foreach (var d in decks)
                foreach (int id in d.cardIds) deckCardIds.Add(id);
            md.Append("| card | handle | played |\n|---|---|---|\n");
            foreach (int id in deckCardIds)
                md.Append($"| {id} | `{(db.TryGet(id, out CardDefinition def) ? def.Handle : "<unknown>")}` | ")
                  .Append($"{Get2(cardPlays, id)} |\n");
            md.Append(neverPlayed.Count == 0
                ? "\nNo deck card was drawn and never played.\n"
                : $"\n**{neverPlayed.Count} card(s) NEVER PLAYED** — a card in a deck that never resolves across a "
                  + "run this size is a driver hole until proven otherwise.\n");

            if (failures.Count > 0)
            {
                md.Append($"\n## Unclean matches ({failures.Count})\n\n");
                foreach (string f in failures) md.Append($"- {f}\n");
            }

            File.WriteAllText(Path.Combine(outDir, "ladder-standings.md"), md.ToString());

            var csv = new System.Text.StringBuilder();
            csv.Append("deck,primary_class,secondary_class,played,won,winrate_milli,seat0,matches_total,hero_hp\n");
            foreach (string label in ranked)
            {
                var d = decks.First(x => string.Equals(x.label, label, StringComparison.Ordinal));
                int pl = Get(played, label);
                csv.Append($"{CsvQuote(label)},{d.primary},{d.secondary},{pl},{Get(wins, label)},")
                   .Append($"{(pl == 0 ? 0 : Get(wins, label) * 1000 / pl)},{Get(seatZero, label)},{matches},{heroMaxHealth}\n");
            }
            File.WriteAllText(Path.Combine(outDir, "ladder-standings.csv"), csv.ToString());
        }

        // ── corpus (EF-030: the all-pairs soak over HAND-AUTHORED decks) ───────────

        private const string DefaultCorpusOut = "artifacts/balance-corpus";

        /// <summary>MatchSetup's own default, every EF-030 corpus number so far, AND (since
        /// 2026-08-27) the live game. Overridden by <c>--hp</c> — see <see cref="ParseHeroHp"/>.</summary>
        private const int DefaultCorpusHeroHp = 30;

        /// <summary>
        /// EF-030 — <see cref="RunMatrixAsync"/>'s grid with the class axis replaced by a deck axis.
        /// Every <c>*.deck.json</c> under <see cref="BalanceCorpusLoader.RepoRelativeDir"/> becomes one
        /// contender, every ordered pair of DISTINCT decks is played as its own
        /// <paramref name="matchesPerCell"/>-match soak (seats alternating inside the cell), and the
        /// resulting grid is written to <paramref name="outDir"/> as Markdown + CSV beside the same
        /// balance-sample JSONL the <c>ai</c> and <c>matrix</c> modes emit.
        ///
        /// <para><b>Why this exists next to <c>matrix</c> rather than replacing it.</b> The matrix
        /// grinds decks the <see cref="DefaultDeckGenerator"/> built, so it measures the class POOL —
        /// what a class can field when nobody is choosing. The corpus grinds decks a designer wrote,
        /// so it measures the archetypes that will actually be played. They answer different
        /// questions and a healthy patch wants both numbers; neither is the other's replacement.</para>
        ///
        /// <para><b>Self-pairs are skipped.</b> A deck against a literal copy of itself is 50% by
        /// construction once the seats alternate — it measures the seat, which the matrix's mirror
        /// diagonal already reports and which no balance decision reads off a hand-built deck. Five
        /// decks therefore grind 20 cells, not 25, and the diagonal prints as n/a.</para>
        ///
        /// <para><b>Gating is byte-identical to the matrix's</b> — cleanliness (no integrity failures,
        /// undecided or replay-disagreeing outcomes, capped/stalled matches, real rejects, dropped
        /// telemetry rows) AND a non-zero intent mix, with a crashed cell contained to itself and
        /// never clean. Any tripped cell fails the run (exit 1).</para>
        /// </summary>
        private static async Task<int> RunCorpusAsync(int matchesPerCell, string outDir, int heroMaxHealth)
        {
            // Its own folder by default, for the same reason the matrix takes one: the corpus sample
            // is a different population and must never merge into the sanctioned `ai_soak` rows.
            if (string.Equals(outDir, DefaultSoakOut, StringComparison.Ordinal)) outDir = DefaultCorpusOut;

            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            string corpusDir = FindBalanceCorpus();
            var decks = BalanceCorpusLoader.Load(corpusDir, db);

            var validator = new CommandProcessor(db);
            var sink = new JsonlTelemetrySink(outDir, EnvLabel());
            var attribution = new MatchAttribution(
                AccountId.FromDeviceToken(SoakDevice(0)),
                AccountId.FromDeviceToken(SoakDevice(1)),
                TelemetrySource.AiSoak);

            int n = decks.Count;
            int cellCount = n * (n - 1);
            Console.WriteLine($"[corpus] {n} deck(s) from {corpusDir}, {heroMaxHealth} hero HP");
            foreach (var d in decks)
                Console.WriteLine($"           {d.label,-28} {d.primary}/{d.secondary}  {d.cardIds.Count} cards");
            if (cellCount == 0)
                return Fail($"[corpus] {n} deck(s) is not enough for a pair — add a second *.deck.json to {corpusDir}.");
            Console.WriteLine($"[corpus] {cellCount} ordered pair(s) (self-pairs skipped),"
                              + $" {matchesPerCell} match(es) each → {sink.Path}");

            var grid = new MatchupResult?[n, n];
            int prevDropped = 0, cellIndex = 0;
            bool allClean = true;
            int labelWidth = decks.Max(d => d.label.Length);

            for (int ri = 0; ri < n; ri++)
                for (int ci = 0; ci < n; ci++)
                {
                    if (ri == ci) continue;
                    var a = decks[ri];
                    var b = decks[ci];

                    var even = new MatchSetup(a.primary, a.cardIds, b.primary, b.cardIds, heroMaxHealth);
                    var odd = new MatchSetup(b.primary, b.cardIds, a.primary, a.cardIds, heroMaxHealth);

                    // A distinct seed family from the matrix's, so a corpus cell and a matrix cell can
                    // never grind the same match under two different labels in one merged sample.
                    ulong seedBase = 0xC0DEC0BA11UL + (ulong)(++cellIndex) * 100_003UL;
                    MatchupResult r = await RunMatchupAsync(matchesPerCell, even, odd, db, validator,
                                                            sink, attribution, seedBase, a.label, b.label);
                    grid[ri, ci] = r;

                    int dropped = sink.DroppedRows - prevDropped;
                    prevDropped = sink.DroppedRows;
                    bool cellClean = r.CoreClean && dropped == 0 && r.IntentMixOk;
                    allClean &= cellClean;

                    string why = cellClean ? "" : "  <- " + (r.IntentMixOk ? "" : "intent-mix ") + r.CoreIssues()
                                                           + (dropped > 0 ? $" dropped={dropped}" : "");
                    Console.WriteLine(
                        $"  {a.label.PadRight(labelWidth)} vs {b.label.PadRight(labelWidth)}"
                        + $"  {Pct(Get(r.WinsByLabel, a.label), Get(r.PlayedByLabel, a.label))}"
                        + $"  plays {r.CardsPlayed,5} atk {r.MinionAttacks,5} hp {r.HeroPowers,4}"
                        + $"   {(cellClean ? "CLEAN" : r.Crash is not null ? "CRASH" : "FAIL")}{why}");
                    for (int rr = 0; rr < r.RejectReasons.Count && rr < 4; rr++)
                        Console.WriteLine($"        {r.RejectReasons[rr]}");
                }

            Console.WriteLine();
            Console.WriteLine($"=== corpus win-rate (row deck vs column deck) — {matchesPerCell} match(es) per cell ===");
            Console.WriteLine($"  {"row \\ col".PadRight(labelWidth)} " + string.Join(" ", decks.Select(d => d.label.PadLeft(labelWidth))));
            for (int ri = 0; ri < n; ri++)
            {
                var cellsText = new List<string>(n);
                for (int ci = 0; ci < n; ci++)
                {
                    MatchupResult? cell = grid[ri, ci];
                    cellsText.Add((cell is null
                        ? "  n/a"
                        : Pct(Get(cell.Value.WinsByLabel, decks[ri].label), Get(cell.Value.PlayedByLabel, decks[ri].label)))
                        .PadLeft(labelWidth));
                }
                Console.WriteLine($"  {decks[ri].label.PadRight(labelWidth)} " + string.Join(" ", cellsText));
            }

            WriteCorpusArtifacts(outDir, decks, grid, matchesPerCell);

            var ground = new List<MatchupResult>();
            for (int ri = 0; ri < n; ri++)
                for (int ci = 0; ci < n; ci++)
                    if (grid[ri, ci] is MatchupResult m) ground.Add(m);

            int terminalRaceRejects = ground.Sum(c => c.TerminalRejects);
            int crashedCells = ground.Count(c => c.Crash is not null);

            // Aggregate per-card play counts across all cells and report any deck card that was
            // never played across the whole run (NOR-350 — the silent corpus confound detector).
            var allCardPlays = new Dictionary<int, int>();
            foreach (MatchupResult cell in ground)
                foreach (var kv in cell.CardPlays)
                    allCardPlays[kv.Key] = Get2(allCardPlays, kv.Key) + kv.Value;

            var deckCardIds = new HashSet<int>();
            foreach (var d in decks)
                foreach (int id in d.cardIds)
                    deckCardIds.Add(id);

            var neverPlayed = new List<int>();
            foreach (int id in deckCardIds)
                if (!allCardPlays.ContainsKey(id) || allCardPlays[id] == 0)
                    neverPlayed.Add(id);
            neverPlayed.Sort();

            Console.WriteLine();
            Console.WriteLine("  --- per-card play counts (corpus decks, all cells combined) ---");
            // The id alone is unreadable in a tally a human scans for a confound, so carry the
            // handle beside it — the ids became numeric and this column was still sized for a handle.
            string Named(int id) => db.TryGet(id, out CardDefinition d) ? $"{id,-6} {d.Handle}" : $"{id,-6} <unknown>";

            foreach (int id in deckCardIds.OrderBy(s => s))
                Console.WriteLine($"  {Named(id),-44}  played {Get2(allCardPlays, id),5}");
            if (neverPlayed.Count == 0)
                Console.WriteLine("  drawn-but-never-played: (none)");
            else
                foreach (int id in neverPlayed)
                    Console.WriteLine($"  NEVER PLAYED: {Named(id)}");

            Console.WriteLine();
            if (crashedCells > 0)
                Console.WriteLine($"  {crashedCells} cell(s) CRASHED on an engine/data fault — see the "
                                  + "'Cells that CRASHED' section of the artifact for each message.");
            if (terminalRaceRejects > 0)
                Console.WriteLine($"  ({terminalRaceRejects} benign end-of-match GameOver reject(s) across all cells — the "
                                  + "seat that lost the lethal race, not a rules fault)");
            Console.WriteLine($"  corpus → {Path.Combine(outDir, "corpus-winrate.md")}");
            Console.WriteLine(allClean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            return allClean ? 0 : 1;
        }

        /// <summary>
        /// The corpus twin of <see cref="WriteMatrixArtifacts"/>: same two files, deck labels on both
        /// axes, and a null cell (the skipped self-pair) rendered <c>n/a</c> rather than 0% — a deck
        /// that was never played must not read as a deck that never won.
        /// </summary>
        private static void WriteCorpusArtifacts(
            string outDir,
            IReadOnlyList<(string label, CardClass primary, CardClass secondary, IReadOnlyList<int> cardIds)> decks,
            MatchupResult?[,] grid, int matchesPerCell)
        {
            Directory.CreateDirectory(outDir);
            int n = decks.Count;

            var md = new System.Text.StringBuilder();
            md.Append("# Kingdom balance corpus (EF-030)\n\n");
            // Name the directory actually ground: KINGDOM_BALANCE_CORPUS points this at other
            // populations (bot_roster_to_corpus.py), and a header claiming the hand-authored set
            // would mislabel those grids.
            string corpusDir = Environment.GetEnvironmentVariable("KINGDOM_BALANCE_CORPUS") is { Length: > 0 } env
                ? env : BalanceCorpusLoader.RepoRelativeDir;
            md.Append($"All-pairs AI-vs-AI soak over the {n} decks in "
                      + $"`{corpusDir}` — {matchesPerCell} matches per cell, seats alternating.\n\n");
            md.Append("Cell = **row deck** win-rate vs the **column deck**. Self-pairs are skipped (a deck against "
                      + "its own copy measures the seat, not the deck), so the diagonal is `n/a`.\n\n");
            md.Append("Synthetic-clock sample, ground locally (decision 2026-07-22); order rows by matchId, never by time. ");
            md.Append("Every cell is gated on cleanliness AND a non-zero intent mix; a benign end-of-match "
                      + "`GameOver` reject (the seat that lost the lethal race) is counted apart in the CSV's "
                      + "`terminal_race_rejects` column and never fails a cell.\n\n");

            md.Append("| deck | character | cards |\n|---|---|---|\n");
            foreach (var d in decks)
                md.Append($"| `{d.label}` | {d.primary}/{d.secondary} | {d.cardIds.Count} |\n");
            md.Append("\n");

            md.Append("| row \\ col |");
            foreach (var d in decks) md.Append($" {d.label} |");
            md.Append("\n|---|");
            foreach (var _ in decks) md.Append("---|");
            md.Append("\n");
            for (int ri = 0; ri < n; ri++)
            {
                md.Append($"| **{decks[ri].label}** |");
                for (int ci = 0; ci < n; ci++)
                {
                    MatchupResult? cell = grid[ri, ci];
                    md.Append(cell is null
                        ? " n/a |"
                        : $" {Pct(Get(cell.Value.WinsByLabel, decks[ri].label), Get(cell.Value.PlayedByLabel, decks[ri].label))} |");
                }
                md.Append("\n");
            }

            md.Append("\n## Per-cell soak health\n\n");
            md.Append("| matchup | row winrate | cards | minion atk | hp swings | hero pw | draws | mean turns | gate |\n");
            md.Append("|---|---|---|---|---|---|---|---|---|\n");
            for (int ri = 0; ri < n; ri++)
                for (int ci = 0; ci < n; ci++)
                {
                    if (grid[ri, ci] is not MatchupResult cell) continue;
                    bool clean = cell.CoreClean && cell.IntentMixOk;
                    string gate = clean ? "CLEAN"
                                : cell.Crash is not null ? "CRASH"
                                : (!cell.IntentMixOk ? "intent-mix" : "FAIL");
                    md.Append($"| {decks[ri].label} vs {decks[ci].label} "
                        + $"| {Pct(Get(cell.WinsByLabel, decks[ri].label), Get(cell.PlayedByLabel, decks[ri].label))} "
                        + $"| {cell.CardsPlayed} | {cell.MinionAttacks} | {cell.HeroSwings} | {cell.HeroPowers} "
                        + $"| {cell.Draws} | {Tenths(cell.TotalTurns, cell.Matches)} | {gate} |\n");
                }

            var crashed = new List<(int ri, int ci)>();
            for (int ri = 0; ri < n; ri++)
                for (int ci = 0; ci < n; ci++)
                    if (grid[ri, ci] is MatchupResult c && c.Crash is not null) crashed.Add((ri, ci));

            if (crashed.Count > 0)
            {
                md.Append($"\n## Cells that CRASHED ({crashed.Count})\n\n");
                md.Append("An engine or data fault, not a balance signal — the matchup stopped grinding at the "
                          + "match named below. These cells' win-rates cover only the matches that completed "
                          + "and must not be read as balance.\n\n");
                foreach (var (ri, ci) in crashed)
                    md.Append($"- **{decks[ri].label} vs {decks[ci].label}** — `{grid[ri, ci]!.Value.Crash}`\n");
            }

            File.WriteAllText(Path.Combine(outDir, "corpus-winrate.md"), md.ToString());

            var csv = new System.Text.StringBuilder();
            csv.Append("row_deck,col_deck,row_class,col_class,row_winrate_milli,matches,row_wins,row_played,draws,undecided,"
                       + "capped,rejects,integrity_failures,outcome_disagreements,terminal_race_rejects,"
                       + "cards_played,minion_attacks,hero_swings,hero_powers,mean_turns_tenths,core_clean,intent_mix_ok,crash\n");
            for (int ri = 0; ri < n; ri++)
                for (int ci = 0; ci < n; ci++)
                {
                    if (grid[ri, ci] is not MatchupResult cell) continue;
                    int rw = Get(cell.WinsByLabel, decks[ri].label);
                    int rp = Get(cell.PlayedByLabel, decks[ri].label);
                    int milli = rp == 0 ? 0 : rw * 1000 / rp;
                    csv.Append($"{CsvQuote(decks[ri].label)},{CsvQuote(decks[ci].label)},{decks[ri].primary},{decks[ci].primary},"
                        + $"{milli},{cell.Matches},{rw},{rp},{cell.Draws},"
                        + $"{cell.Undecided},{cell.Capped},{cell.Rejects},{cell.IntegrityFailures},{cell.OutcomeDisagreements},{cell.TerminalRejects},"
                        + $"{cell.CardsPlayed},{cell.MinionAttacks},{cell.HeroSwings},{cell.HeroPowers},"
                        + $"{cell.TotalTurns * 10 / Math.Max(1, cell.Matches)},{cell.CoreClean},{cell.IntentMixOk},{CsvQuote(cell.Crash)}\n");
                }
            File.WriteAllText(Path.Combine(outDir, "corpus-winrate.csv"), csv.ToString());
        }

        /// <summary>
        /// The balance-corpus directory: <c>KINGDOM_BALANCE_CORPUS</c> if it points somewhere real,
        /// else the repo-relative one found by walking up from the binary — the same shape as
        /// <see cref="FindCardData"/>, and for the same reason (the tool is run from the repo tree by
        /// hand and from an arbitrary CWD by CI). The path is RETURNED even when it does not exist, so
        /// the loader's error names the directory it looked in.
        /// </summary>
        private static string FindBalanceCorpus()
        {
            string? env = Environment.GetEnvironmentVariable("KINGDOM_BALANCE_CORPUS");
            if (!string.IsNullOrEmpty(env)) return env;

            string[] parts = BalanceCorpusLoader.RepoRelativeDir.Split('/');
            string? path = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(new[] { path }.Concat(parts).ToArray());
                if (Directory.Exists(candidate)) return candidate;
            }
            return BalanceCorpusLoader.RepoRelativeDir;
        }

        // ── trace (EF-033a diagnostic: the move log of a matrix match) ─────────────

        /// <summary>
        /// EF-033a — replay the FIRST <paramref name="matches"/> matches of a matrix cell and print
        /// every intent, with hero health after each one. Read-only and driven by the same
        /// <see cref="GreedyAi"/>, the same <see cref="ResolveSoakSetups"/> decks and the same seed
        /// base <see cref="RunMatrixAsync"/> gives that cell, so the log IS the matrix match — not a
        /// lookalike. It answers the only question a win-rate cannot: at which turn, and to what,
        /// the losing seat stopped having an answer.
        /// </summary>
        private static async Task<int> RunTraceAsync(int matches, CardClass? p0Class, CardClass? p1Class)
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            (MatchSetup even, MatchSetup odd) = ResolveSoakSetups(p0Class, p1Class, db);
            var validator = new CommandProcessor(db);
            var policies = new ISeatPolicy[]
            {
                new GreedySeatPolicy(validator, db),
                new GreedySeatPolicy(validator, db),
            };
            ulong seedBase = MatrixSeedBase(even.P0Class, even.P1Class);

            Console.WriteLine($"[trace] {even.P0Class} vs {even.P1Class} — matrix cell seedBase=0x{seedBase:x}, first {matches} match(es)");

            for (int i = 0; i < matches; i++)
            {
                bool swap = (i & 1) == 1;
                MatchSetup setup = swap ? odd : even;
                Console.WriteLine();
                Console.WriteLine($"───── match {i}  (p0={setup.P0Class}, p1={setup.P1Class}) ─────");

                using var peer = new SoakLoopback(db, seedBase + (ulong)i, $"matrix-{seedBase:x}-{i}", setup);
                using var c0 = peer.Client(0, new ClientOptions { DeviceToken = SoakDevice(0) });
                using var c1 = peer.Client(1, new ClientOptions { DeviceToken = SoakDevice(1) });
                peer.Bind(c0, c1);
                Task s0 = c0.ConnectAsync(new Uri("loopback://soak"));
                Task s1 = c1.ConnectAsync(new Uri("loopback://soak"));
                peer.Start();
                await Task.WhenAll(s0, s1);

                var clients = new[] { c0, c1 };
                int steps = 0, lastTurn = -1;
                while (c0.Current is not null && c0.Current.Outcome == MatchOutcome.InProgress)
                {
                    if (steps++ >= IntentCap) { Console.WriteLine("  [CAPPED]"); break; }
                    bool acted = false;
                    foreach (NetworkedMatchSession c in clients)
                    {
                        PlayerIntent? intent = await policies[c.Seat].ChooseAsync(c.Current, c.Player);
                        if (intent is null) continue;

                        GameState before = c.Current!;
                        if (before.TurnNumber != lastTurn && before.Phase == TurnPhase.Main)
                        {
                            lastTurn = before.TurnNumber;
                            Console.WriteLine($"  ── turn {lastTurn}  active=seat{before.SeatOf(before.ActivePlayer)}"
                                              + $"  hp {before.Players[0].Hero.CurrentHealth}/{before.Players[1].Hero.CurrentHealth}"
                                              + $"  board {before.Players[0].Board.Count}v{before.Players[1].Board.Count}");
                        }

                        string label = DescribeIntent(before, intent, db);
                        Task<CommandResult> submitted = c.SubmitAsync(intent);
                        await peer.RelayAsync();
                        CommandResult r = await submitted;

                        GameState after = c0.Current!;
                        if (intent is not EndTurnIntent && intent is not MulliganIntent)
                            Console.WriteLine($"      seat{before.SeatOf(c.Player)} {label}"
                                              + (r.Accepted ? "" : $"   REJECTED: {r.Reason}")
                                              + $"   -> hp {after.Players[0].Hero.CurrentHealth}/{after.Players[1].Hero.CurrentHealth}"
                                              + $" board {after.Players[0].Board.Count}v{after.Players[1].Board.Count}");
                        acted = true;
                    }
                    if (!acted) { Console.WriteLine("  [STALLED]"); break; }
                }

                GameState end = c0.Current!;
                Console.WriteLine($"  OUTCOME {end.Outcome} on turn {end.TurnNumber}"
                                  + $"  final hp {end.Players[0].Hero.CurrentHealth}/{end.Players[1].Hero.CurrentHealth}"
                                  + $"  ({setup.P0Class}={end.Players[0].Hero.CurrentHealth}, {setup.P1Class}={end.Players[1].Hero.CurrentHealth})");
            }
            return 0;
        }

        /// <summary>The seed <see cref="RunMatrixAsync"/> assigns the (a,b) cell — its enumeration order.</summary>
        private static ulong MatrixSeedBase(CardClass a, CardClass b)
        {
            CardClass[] classes = PlayableClasses();
            int cellIndex = 0;
            foreach (CardClass x in classes)
                foreach (CardClass y in classes)
                {
                    cellIndex++;
                    if (x == a && y == b) return 0xE17BEEF5EEDUL + (ulong)cellIndex * 100_003UL;
                }
            throw new ArgumentException($"{a} vs {b} is not a matrix cell.");
        }

        private static string DescribeIntent(GameState s, PlayerIntent intent, ICardDatabase db) => intent switch
        {
            PlayCardIntent p => $"PLAY {NameOfInstance(s, p.Card, db)}"
                                + (p.Targets.Count > 0 ? $" -> {string.Join(",", p.Targets.Select(t => NameOfInstance(s, t.Instance, db)))}" : " (no target)"),
            AttackIntent a => $"ATTACK {NameOfInstance(s, a.Attacker, db)} -> {NameOfInstance(s, a.Defender.Instance, db)}",
            HeroPowerIntent h => "HERO POWER"
                                 + (h.Targets.Count > 0 ? $" -> {NameOfInstance(s, h.Targets[0].Instance, db)}" : " (no target)"),
            EndTurnIntent => "end turn",
            _ => intent.GetType().Name,
        };

        /// <summary>Card name for an instance anywhere in either seat's hand or board, else "hero"/the raw id.</summary>
        private static string NameOfInstance(GameState s, InstanceId id, ICardDatabase db)
        {
            foreach (PlayerState ps in s.Players)
            {
                if (ps.Hero.Id.Equals(id)) return $"HERO({ps.Hero.CurrentHealth}hp)";
                foreach (CardInstance c in ps.Hand)
                    if (c.Id.Equals(id)) return Named(c, db);
                foreach (CardInstance c in ps.Board)
                    if (c.Id.Equals(id)) return Named(c, db);
            }
            return id.ToString();
        }

        private static string Named(CardInstance c, ICardDatabase db)
            => db.TryGet(c.DefinitionId, out Kingdom.Core.Cards.CardDefinition d)
                ? $"{d.Name}[{d.Cost}m {d.Attack?.ToString() ?? "-"}/{c.CurrentHealth}]"
                : c.DefinitionId.ToString();

        // ── decks (EF-033a diagnostic: what the harness actually deals) ────────────

        /// <summary>
        /// EF-033a — print the two 30-card decklists <see cref="ResolveSoakSetups"/> actually hands
        /// the seats for a given <c>--p0</c>/<c>--p1</c> pair, plus their multiset difference.
        ///
        /// <para>Read-only: it calls the same resolver the <c>ai</c> and <c>matrix</c> modes call and
        /// prints its output. It exists because the balance matrix's degenerate cells (NOR-186) can
        /// only be read as a class result if the two seats are dealt different card pools, and that
        /// question cannot be settled by reading <see cref="SecondaryFor"/> — it has to be printed.</para>
        /// </summary>
        private static int RunDeckDump(CardClass? p0Class, CardClass? p1Class)
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            (MatchSetup even, MatchSetup odd) = ResolveSoakSetups(p0Class, p1Class, db);

            Console.WriteLine($"[decks] matchup: {even.P0Class} (p0) vs {even.P1Class} (p1)   "
                              + $"{even.HeroMaxHealth} HP, {even.P0Deck.Count}/{even.P1Deck.Count} cards");
            Console.WriteLine($"[decks] hero powers: p0={even.P0Class} p1={even.P1Class}  (MatchEngine takes these from PrimaryClass)");
            Console.WriteLine($"[decks] odd-match seat swap: p0={odd.P0Class} p1={odd.P1Class}");
            Console.WriteLine();

            DumpOne($"{even.P0Class} (p0) deck", even.P0Deck, db);
            Console.WriteLine();
            DumpOne($"{even.P1Class} (p1) deck", even.P1Deck, db);
            Console.WriteLine();

            var a = CountBy(even.P0Deck);
            var b = CountBy(even.P1Deck);
            var allIds = a.Keys.Union(b.Keys).OrderBy(s => s).ToList();
            var diffs = allIds.Where(id => Get2(a, id) != Get2(b, id)).ToList();

            Console.WriteLine("=== multiset difference (p0 deck vs p1 deck) ===");
            if (diffs.Count == 0)
            {
                Console.WriteLine("  (empty) — the two seats receive the SAME 30 cards, copy for copy.");
            }
            else
            {
                Console.WriteLine($"  {"cardId",-40} {"p0",3} {"p1",3}");
                foreach (int id in diffs)
                    Console.WriteLine($"  {id,-40} {Get2(a, id),3} {Get2(b, id),3}");
            }
            Console.WriteLine();
            Console.WriteLine($"  distinct ids p0={a.Count}  p1={b.Count}  shared={a.Keys.Intersect(b.Keys).Count()}  differing={diffs.Count}");
            Console.WriteLine($"  identical decklists: {(diffs.Count == 0 ? "YES" : "NO")}");
            Console.WriteLine($"  p0 ordered list == p1 ordered list: {even.P0Deck.SequenceEqual(even.P1Deck)}");
            Console.WriteLine();

            // The number that decides whether an axis label describes the deck it names. Both seats
            // draw from PRIMARY ∪ SECONDARY ∪ NEUTRAL, and in a non-mirror cell SecondaryFor makes
            // that ONE pool for both — so a seat can easily hold more of its OPPONENT's class than
            // of its own, while the matrix still records the cell under its hero-power class.
            Console.WriteLine("=== class composition (which class's cards each seat is actually holding) ===");
            Console.WriteLine($"  {"class",-12} {"p0 (" + even.P0Class + ")",-22} {"p1 (" + even.P1Class + ")",-22}");
            var pools = new[] { even.P0Deck, even.P1Deck }
                .Select(dk => dk.Select(id => db.TryGet(id, out Kingdom.Core.Cards.CardDefinition cd) ? cd.Class : CardClass.NEUTRAL)
                                 .GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count()))
                .ToArray();
            foreach (CardClass c in Enum.GetValues<CardClass>())
            {
                int n0 = pools[0].TryGetValue(c, out int x0) ? x0 : 0;
                int n1 = pools[1].TryGetValue(c, out int x1) ? x1 : 0;
                if (n0 == 0 && n1 == 0) continue;
                Console.WriteLine($"  {c,-12} {n0,3} cards               {n1,3} cards");
            }
            Console.WriteLine($"  --> p0 holds {(pools[0].TryGetValue(even.P0Class, out int own0) ? own0 : 0)} of its OWN class "
                              + $"and {(pools[0].TryGetValue(even.P1Class, out int opp0) ? opp0 : 0)} of the OPPONENT's");
            Console.WriteLine($"  --> p1 holds {(pools[1].TryGetValue(even.P1Class, out int own1) ? own1 : 0)} of its OWN class "
                              + $"and {(pools[1].TryGetValue(even.P0Class, out int opp1) ? opp1 : 0)} of the OPPONENT's");
            return 0;
        }

        private static void DumpOne(string title, IReadOnlyList<int> deck, ICardDatabase db)
        {
            Console.WriteLine($"=== {title} — {deck.Count} cards, pre-shuffle order ===");
            for (int i = 0; i < deck.Count; i++)
            {
                Console.WriteLine(!db.TryGet(deck[i], out Kingdom.Core.Cards.CardDefinition d)
                    ? $"  {i + 1,2}. {deck[i],-40} (NOT IN DB)"
                    : $"  {i + 1,2}. {deck[i],-40} {d.Cost}m {d.Attack?.ToString() ?? "-"}/{d.Health?.ToString() ?? "-"}  {d.Type,-6} {d.Class,-12} {d.Name}");
            }
        }

        private static Dictionary<int, int> CountBy(IReadOnlyList<int> ids)
        {
            var d = new Dictionary<int, int>();
            foreach (int id in ids) d[id] = Get2(d, id) + 1;
            return d;
        }

        private static int Get2<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull
            => d.TryGetValue(k, out int v) ? v : 0;

        // ── class-vs-class deck resolution (EF-030) ────────────────────────────────

        /// <summary>Parse <c>--p0</c>/<c>--p1 &lt;CLASS&gt;</c>; null when absent, throws on a non-playable class.</summary>
        private static CardClass? ParseClassFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], flag, StringComparison.Ordinal)) continue;
                if (!Enum.TryParse(args[i + 1], ignoreCase: true, out CardClass c) || c == CardClass.NEUTRAL)
                    throw new ArgumentException(
                        $"{flag} '{args[i + 1]}' is not a playable class — one of: "
                        + string.Join(", ", Enum.GetNames<CardClass>().Where(n => n != nameof(CardClass.NEUTRAL))));
                return c;
            }
            return null;
        }

        /// <summary>
        /// <c>--hp N</c>, else <see cref="DefaultCorpusHeroHp"/> — which since 2026-08-27 is also what
        /// the live game deals, so the default now measures the shipped shape and the flag is for
        /// asking what-if.
        ///
        /// <para><b>The flag exists because the two numbers were once different and nothing said so.</b>
        /// <c>MatchGate.StartMatch</c> takes HP from <c>PrebuiltDecks</c> for EVERY match, submitted
        /// decks and bot-fill included; that pair carried an interim 20 while this defaulted to 30, so
        /// every corpus grid ground a life total no live match used and read systematically against
        /// aggro. Keep the two in step, and keep this flag: the next time someone wants to know what a
        /// deck does at a different life total, the answer should not be an edit.</para>
        ///
        /// <para>RETIREMENT TRIGGER: if <c>PrebuiltDecks.EmberwardVsTidecaller</c> stops resolving to
        /// <see cref="DefaultCorpusHeroHp"/>, the first paragraph is wrong — fix it here, in
        /// <c>tooling/scripts/bot_roster_to_corpus.py</c>, and in that method's own comment.</para>
        /// </summary>
        private static int ParseHeroHp(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--hp", StringComparison.Ordinal)) continue;
                if (!int.TryParse(args[i + 1], out int hp) || hp <= 0)
                    throw new ArgumentException($"--hp '{args[i + 1]}' is not a positive integer.");
                return hp;
            }
            return DefaultCorpusHeroHp;
        }

        /// <summary>Output dir is the first positional after the count that is not a <c>--flag</c>.</summary>
        private static string ParseOutDir(string[] args, int idx)
            => args.Length > idx && !args[idx].StartsWith("--", StringComparison.Ordinal) ? args[idx] : DefaultSoakOut;

        private static string ParseDatasetOutDir(string[] args, int idx)
            => args.Length > idx && !args[idx].StartsWith("--", StringComparison.Ordinal) ? args[idx] : DefaultDatasetOut;

        /// <summary>Matches the server's own resolution so a merged file reads the same either way.</summary>
        private static string EnvLabel()
        {
            string? explicitLabel = Kingdom.Net.Server.KingdomEnv.Get("KINGDOM_ENV");
            if (!string.IsNullOrWhiteSpace(explicitLabel)) return explicitLabel;
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLY_APP_NAME")) ? "local" : "fly";
        }

        // Generic over the key so the same counters serve the class-keyed matrix and the label-keyed
        // EF-030 corpus; a second pair of near-identical helpers is how the two axes drift apart.
        private static void Bump<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull => d[k] = Get(d, k) + 1;
        private static int Get<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull => d.TryGetValue(k, out int v) ? v : 0;

        // Integer-only formatting: the engine is float-free (ADR-0003) and a report that reaches for
        // a double to print one decimal place is how that discipline erodes at the edges.
        private static string Pct(int n, int of)
            => of == 0 ? "  n/a" : $"{n * 1000 / of / 10,3}.{n * 1000 / of % 10}%";

        private static string Tenths(int n, int of)
            => of == 0 ? "n/a" : $"{n * 10 / of / 10}.{n * 10 / of % 10}";

        // ── live ──────────────────────────────────────────────────────────────────

        /// <summary>Fixed base seed for live fault runs so a `--loss`/`--disconnect` gate is
        /// reproducible; each match XORs in its index so the two seats and successive matches don't
        /// share a drop pattern.</summary>
        private const ulong FaultSeedBase = 0xFA017F00DBADF00DUL;

        private static async Task<int> RunLiveAsync(Uri wsUri, int matches, FaultProfile fault)
        {
            Console.WriteLine($"[soak] live target: {wsUri}");
            if (fault.IsActive) Console.WriteLine($"[soak] FAULT INJECTION active — {fault}");
            int integrityFailures = 0, misattributions = 0, undecided = 0;

            for (int i = 0; i < matches; i++)
            {
                int localMisses = 0;
                var opts = new ClientOptions { OnMisattribution = _ => Interlocked.Increment(ref localMisses) };
                using var c0 = new NetworkedMatchSession(WireFor(0, i, fault), opts with { DeviceToken = SoakDevice(0) });
                using var c1 = new NetworkedMatchSession(WireFor(1, i, fault), opts with { DeviceToken = SoakDevice(1) });

                await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri))
                    .WaitAsync(TimeSpan.FromSeconds(20));
                if (c0.State != ConnectionState.Live || c1.State != ConnectionState.Live)
                    return Fail($"match {i}: did not reach Live (c0={c0.State}, c1={c1.State})");

                Task ended0 = WaitForState(c0, ConnectionState.Ended);
                Task ended1 = WaitForState(c1, ConnectionState.Ended);

                await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
                await c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));
                _ = c1.SubmitAsync(new ConcedeIntent(c1.Player));
                await Task.WhenAll(ended0, ended1).WaitAsync(TimeSpan.FromSeconds(30));

                integrityFailures += c0.ResyncCount + c1.ResyncCount;
                misattributions += localMisses;
                if (c0.Current.Outcome == MatchOutcome.InProgress) undecided++;
                Console.WriteLine($"[soak] live match {i}: outcome={c0.Current.Outcome} seats=({c0.Seat},{c1.Seat})");
            }

            return Report("live", matches, integrityFailures, misattributions, undecided, outcomeDisagreements: 0);
        }

        /// <summary>A real socket for the seat, wrapped in the fault injector only when a profile is
        /// active — so the default `live` path is byte-for-byte the old one.</summary>
        private static IWireTransport WireFor(int seat, int match, FaultProfile fault)
        {
            var socket = new WebSocketTransport();
            return fault.IsActive
                ? new FaultTransport(socket, fault, FaultSeedBase ^ ((ulong)match << 1) ^ (ulong)seat)
                : socket;
        }

        // ── liveai (real AI over real sockets — EF-075) ─────────────────────────────

        /// <summary>250ms poll floor for the snapshot wait — a safety net, not the pacing mechanism:
        /// each accepted intent's snapshot releases the wake immediately, so this only bounds how long
        /// a genuinely-quiet link idles before the stall guard ticks.</summary>
        private const int SnapshotWaitMs = 250;

        /// <summary>Consecutive quiet <see cref="SnapshotWaitMs"/> waits (⇒ 30s of no snapshot progress)
        /// before a match is declared stalled and capped, rather than looped on forever.</summary>
        private const int MaxIdleWaits = 120;

        /// <summary>
        /// EF-075: the composition the launch workplan calls for. `ai` had the real
        /// <see cref="GreedyAi"/> but ran in-process over <see cref="Kingdom.Net.Client.Loopback.LoopbackTransport"/>;
        /// `live` had real sockets but drove a scripted mulligan+concede. Neither could produce
        /// "AI matches over the wire" — the actual desync evidence. This drives the real AI on BOTH
        /// seats, to a natural decided end, over real <see cref="WebSocketTransport"/> sockets.
        ///
        /// <para>Two targets, one drive loop:
        /// <list type="bullet">
        ///   <item><c>liveai [N]</c> boots the REAL <see cref="ServerApp"/> Kestrel host on an ephemeral
        ///   127.0.0.1 port (the local twin of the live-droplet verify — same client stack, same server,
        ///   only NAT/TLS differ) and drives N matches against it. Self-contained: no deploy, so its
        ///   stdout IS the reproducible DONE-WHEN.</item>
        ///   <item><c>liveai &lt;wss-url&gt; [N] [--pace ms]</c> points the same loop at a deployed server.</item>
        /// </list>
        /// Both accept the EF-076 <c>--latency/--jitter/--loss/--disconnect</c> fault flags.</para>
        ///
        /// <para><b>Why the local box zeroes intent cost.</b> A greedy AI submits several intents per
        /// turn; the default token bucket (cap 10, refill 2/s) would reject the tail of a busy turn and
        /// that reject is the BUCKET, not a desync — the exact confound <see cref="SoakLoopback"/>
        /// documents. Rate limiting has its own tests; neutralising it here keeps the one signal this
        /// mode exists for — snapshot-pipeline integrity over the wire — unpolluted. Everything else is
        /// the shipped server. A deployed server runs its real bucket, so the url form paces intents
        /// (default 500ms ⇒ ≤2/s, under the refill) instead.</para>
        /// </summary>
        private static async Task<int> RunLiveAiAsync(string[] args)
        {
            bool external = args.Length >= 2 &&
                (args[1].StartsWith("ws://", StringComparison.Ordinal) || args[1].StartsWith("wss://", StringComparison.Ordinal));
            Uri? url = external ? new Uri(args[1]) : null;
            int matches = ParseCount(args, external ? 2 : 1, external ? 1 : 5);
            FaultProfile fault = FaultProfile.Parse(args);
            int paceMs = ParseIntFlag(args, "--pace", external ? 500 : 0);

            FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
            var validator = new CommandProcessor(db);

            WebApplication? app = null;
            string? replays = null;
            Uri wsUri;
            if (external)
            {
                wsUri = url!;
                Console.WriteLine($"[liveai] EXTERNAL target: {wsUri}  (pacing {paceMs}ms/intent to respect the prod token bucket)");
            }
            else
            {
                // The in-process server logs each /ws request to console at Information; quiet it to
                // Warning so this run's own report is the signal, not Kestrel's per-request lines. Read
                // by WebApplication.CreateBuilder as config; cosmetic and local-boot only.
                Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");

                // The REAL server, in-process, on an OS-assigned port. Intent cost zeroed (see remarks);
                // everything else — Kestrel, MatchGate/FIFO pairing, MatchSession, projection, the wire
                // codec and the client's RC-F8 per-snapshot hash verify — is exactly what ships.
                replays = Path.Combine(Path.GetTempPath(), "emberfall-liveai-" + Environment.ProcessId);
                app = ServerApp.Build(new ServerConfig
                {
                    Urls = "http://127.0.0.1:0",
                    CardsDir = FindCardData(),
                    ReplaysDir = replays,
                    Options = new ServerOptions { InTurnIntentCost = 0, OutOfTurnIntentCost = 0 },
                });
                await app.StartAsync();
                var baseUri = new Uri(app.Urls.First());
                wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                Console.WriteLine($"[liveai] LOCAL real server on {wsUri}  (replays → {replays})");
            }
            if (fault.IsActive) Console.WriteLine($"[liveai] FAULT INJECTION active — {fault}");
            Console.WriteLine($"[liveai] {matches} AI-vs-AI match(es), greedy AI on BOTH seats, over real sockets.");

            int integrityFailures = 0, misattributions = 0, undecided = 0, capped = 0, rejects = 0;
            int outcomeDisagreements = 0, totalTurns = 0, p0Wins = 0, p1Wins = 0, draws = 0, decided = 0;
            var rejectReasons = new List<string>();
            var mix = new AiStats();
            CardClass seat0Class = CardClass.NEUTRAL, seat1Class = CardClass.NEUTRAL;

            try
            {
                for (int i = 0; i < matches; i++)
                {
                    int localMisses = 0;
                    var opts = new ClientOptions { OnMisattribution = _ => Interlocked.Increment(ref localMisses) };
                    using var c0 = new NetworkedMatchSession(WireFor(0, i, fault), opts with { DeviceToken = SoakDevice(0) });
                    using var c1 = new NetworkedMatchSession(WireFor(1, i, fault), opts with { DeviceToken = SoakDevice(1) });

                    await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri)).WaitAsync(TimeSpan.FromSeconds(20));
                    if (c0.State != ConnectionState.Live || c1.State != ConnectionState.Live)
                        return Fail($"match {i}: did not reach Live (c0={c0.State}, c1={c1.State})");

                    // Fixed prebuilt matchup; read it off the live view for the report header.
                    seat0Class = c0.Current.Players[0].Hero.Class;
                    seat1Class = c0.Current.Players[1].Hero.Class;

                    var policies = new ISeatPolicy[]
                    {
                        new GreedySeatPolicy(validator, db),
                        new GreedySeatPolicy(validator, db),
                    };
                    AiMatchResult r = await DriveAiMatchAsync(c0, c1, policies, i, mix, paceMs);

                    integrityFailures += r.Resyncs;
                    misattributions += localMisses;
                    totalTurns += r.Turns;
                    if (r.Capped) capped++;
                    if (r.Rejected) { rejects++; if (r.RejectReason is not null) rejectReasons.Add(r.RejectReason); }
                    if (r.Outcome == MatchOutcome.InProgress) undecided++; else decided++;

                    switch (r.Outcome)
                    {
                        case MatchOutcome.Player0Win: p0Wins++; break;
                        case MatchOutcome.Player1Win: p1Wins++; break;
                        case MatchOutcome.Draw: draws++; break;
                    }

                    // Local only: the client's outcome must AGREE with the server's own replay footer —
                    // the over-the-wire analogue of loopback's peer.Replay check (the server persisted a
                    // decided outcome, and it is the same one both clients saw).
                    if (!external && r.Outcome != MatchOutcome.InProgress)
                    {
                        MatchOutcome? logged = await ReadReplayOutcomeAsync(Path.Combine(replays!, c0.Current.Id.Value + ".jsonl"));
                        if (logged is null || logged.Value != r.Outcome) outcomeDisagreements++;
                    }

                    Console.WriteLine($"[liveai] match {i}: outcome={r.Outcome} turns={r.Turns} intents={r.Intents} " +
                                      $"resyncs={r.Resyncs}{(r.Capped ? " CAPPED" : "")}{(r.Rejected ? " REJECT" : "")}");
                }
            }
            finally
            {
                if (app is not null) await app.StopAsync();
                if (replays is not null && Directory.Exists(replays))
                    try { Directory.Delete(replays, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
            }

            return ReportLiveAi(external, matches, decided, integrityFailures, rejects, rejectReasons, capped,
                                undecided, outcomeDisagreements, misattributions, totalTurns, mix,
                                seat0Class, seat1Class, p0Wins, p1Wins, draws);
        }

        /// <summary>One match's tally, returned by <see cref="DriveAiMatchAsync"/>.</summary>
        private readonly struct AiMatchResult
        {
            public AiMatchResult(MatchOutcome outcome, int intents, int turns, int resyncs, bool capped, bool rejected, string? rejectReason)
            {
                Outcome = outcome; Intents = intents; Turns = turns; Resyncs = resyncs;
                Capped = capped; Rejected = rejected; RejectReason = rejectReason;
            }
            public MatchOutcome Outcome { get; }
            public int Intents { get; }
            public int Turns { get; }
            public int Resyncs { get; }
            public bool Capped { get; }
            public bool Rejected { get; }
            public string? RejectReason { get; }
        }

        /// <summary>Intent-mix tallies (the same four verbs RunAiSoakAsync surfaces). A zero row means
        /// the AI never reached that verb over the wire — the same "silent skip" tell that invalidated
        /// the 2026-07-22 balance run. The drive loop is single-threaded, so no lock is needed.</summary>
        private sealed class AiStats
        {
            public int AimedPlays, BarePlays, MinionAttacks, HeroSwings, HeroPowers;
            public void Record(PlayerIntent intent, GameState s, PlayerId me)
            {
                switch (intent)
                {
                    case PlayCardIntent p: if (p.Targets.Count > 0) AimedPlays++; else BarePlays++; break;
                    case AttackIntent a: if (a.Attacker == s.PlayerOf(me).Hero.Id) HeroSwings++; else MinionAttacks++; break;
                    case HeroPowerIntent: HeroPowers++; break;
                }
            }
        }

        /// <summary>
        /// Drive two connected sessions through per-seat policies to a decided end, reacting to
        /// asynchronously-arriving snapshots — there is no manual relay over a real socket the way the
        /// loopback harness pumps <c>peer.RelayAsync()</c>. Exactly ONE intent is submitted per state
        /// version per seat: the version gate (<c>StateVersion &gt; lastActed</c>) is what stops a stale
        /// view from re-submitting a move the server has already applied and would then reject.
        /// </summary>
        private static async Task<AiMatchResult> DriveAiMatchAsync(
            NetworkedMatchSession c0, NetworkedMatchSession c1,
            IReadOnlyList<ISeatPolicy> policies, int matchIdx, AiStats mix, int paceMs)
        {
            const ulong Unset = ulong.MaxValue; // "no version handled yet" — the first snapshot is often v0.
            var clients = new[] { c0, c1 };
            var lastActed = new ulong[] { Unset, Unset };
            using var wake = new SemaphoreSlim(0);
            void OnSnap(GameState _) => wake.Release();
            c0.OnSnapshot += OnSnap;
            c1.OnSnapshot += OnSnap;
            try
            {
                int intents = 0, idleWaits = 0;
                while (true)
                {
                    GameState top = c0.Current;
                    if (top.Outcome != MatchOutcome.InProgress)
                        return new AiMatchResult(top.Outcome, intents, top.TurnNumber, Resyncs(), capped: false, rejected: false, null);
                    // MatchEnd can move one seat to Ended before the other seat's final snapshot is
                    // observed here. Ended is the successful lifecycle path; only Dead is a lost link.
                    if (c0.State == ConnectionState.Dead || c1.State == ConnectionState.Dead)
                        return new AiMatchResult(top.Outcome, intents, top.TurnNumber, Resyncs(), capped: true, rejected: false, "a socket left Live (disconnect/fault)");

                    bool acted = false;
                    for (int k = 0; k < 2; k++)
                    {
                        NetworkedMatchSession c = clients[k];
                        if (c.State != ConnectionState.Live) continue;
                        GameState s = c.Current;
                        if (s.Outcome != MatchOutcome.InProgress) continue;
                        if (lastActed[k] != Unset && s.StateVersion <= lastActed[k]) continue;   // nothing new for this seat

                        PlayerIntent? intent = await policies[k].ChooseAsync(s, c.Player);
                        lastActed[k] = s.StateVersion;                  // this version is handled either way
                        if (intent is null) continue;                   // not this seat's move / already mulliganed

                        mix.Record(intent, s, c.Player);
                        if (++intents > IntentCap)
                            return new AiMatchResult(MatchOutcome.InProgress, intents, s.TurnNumber, Resyncs(), capped: true, rejected: false, "intent cap");
                        if (paceMs > 0) await Task.Delay(paceMs);

                        CommandResult res = await c.SubmitAsync(intent);
                        if (!res.Accepted)
                        {
                            // A reject on a still-Live socket is the real finding — this client's validator
                            // passed the intent and the server refused it. If the socket has since left Live
                            // it is a disconnect (expected under --disconnect), not a disagreement.
                            bool live = c.State == ConnectionState.Live;
                            return new AiMatchResult(s.Outcome, intents, s.TurnNumber, Resyncs(),
                                capped: !live, rejected: live,
                                rejectReason: $"m{matchIdx} seat{c.Seat} {intent.GetType().Name} -> {res.Reason}");
                        }
                        acted = true;
                    }

                    if (acted) { idleWaits = 0; continue; }

                    // Nothing new to act on: wait for the next snapshot (the result of the last action, or
                    // the opponent's move). The wake fires per snapshot; the timeout is only the stall guard.
                    bool woke = await wake.WaitAsync(TimeSpan.FromMilliseconds(SnapshotWaitMs));
                    if (woke) { idleWaits = 0; continue; }
                    if (++idleWaits >= MaxIdleWaits)
                        return new AiMatchResult(c0.Current.Outcome, intents, c0.Current.TurnNumber, Resyncs(), capped: true, rejected: false, "no snapshot progress (stall)");
                }
            }
            finally
            {
                c0.OnSnapshot -= OnSnap;
                c1.OnSnapshot -= OnSnap;
            }

            int Resyncs() => c0.ResyncCount + c1.ResyncCount;
        }

        /// <summary>Parse the server replay footer (<c>{"outcome":"X"}</c>, JsonlReplayStore.Complete).
        /// Short retry: the footer lands when the server completes the replay, a beat after match_end
        /// reaches the client. Opened share-read so a still-open writer handle doesn't fault the read.</summary>
        private static async Task<MatchOutcome?> ReadReplayOutcomeAsync(string path)
        {
            const string key = "\"outcome\":\"";
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (File.Exists(path))
                {
                    string text = "";
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        text = sr.ReadToEnd();
                    }
                    catch (IOException) { /* writer holds it this instant — retry */ }

                    int idx = text.LastIndexOf(key, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        int start = idx + key.Length;
                        int end = text.IndexOf('"', start);
                        if (end > start && Enum.TryParse(text.Substring(start, end - start), out MatchOutcome oc))
                            return oc;
                    }
                }
                await Task.Delay(20);
            }
            return null;
        }

        private static int ReportLiveAi(
            bool external, int matches, int decided, int integrity, int rejects, List<string> rejectReasons,
            int capped, int undecided, int outcomeDisagreements, int misattr, int totalTurns, AiStats mix,
            CardClass seat0Class, CardClass seat1Class, int p0Wins, int p1Wins, int draws)
        {
            Console.WriteLine();
            Console.WriteLine($"=== NetSoak [liveai] — {matches} AI match(es) over {(external ? "an EXTERNAL wss server" : "the LOCAL real server")} ===");
            Console.WriteLine($"  integrity failures (resyncs) : {integrity}");
            Console.WriteLine($"  rejected intents             : {rejects}");
            for (int r = 0; r < rejectReasons.Count && r < 20; r++)
                Console.WriteLine($"      {rejectReasons[r]}");
            Console.WriteLine($"  capped / disconnected        : {capped}");
            Console.WriteLine($"  undecided outcomes           : {undecided}");
            Console.WriteLine(external
                ? "  replay-outcome disagreements : n/a (no server-side replay access over wss)"
                : $"  replay-outcome disagreements : {outcomeDisagreements}");
            Console.WriteLine($"  RC-F7 mis-attributions       : {misattr}");
            Console.WriteLine($"  draws                        : {draws}");
            Console.WriteLine($"  mean turns                   : {Tenths(totalTurns, matches)}");
            Console.WriteLine("  --- intent mix (a zero row = the AI never reached that verb over the wire) ---");
            Console.WriteLine($"  cards played, untargeted     : {mix.BarePlays}");
            Console.WriteLine($"  cards played, aimed          : {mix.AimedPlays}");
            Console.WriteLine($"  minion attacks               : {mix.MinionAttacks}");
            Console.WriteLine($"  hero swings (weapon)         : {mix.HeroSwings}");
            Console.WriteLine($"  hero powers                  : {mix.HeroPowers}");
            Console.WriteLine("  --- outcomes (fixed prebuilt matchup; seat, NOT a balance sample) ---");
            Console.WriteLine($"  seat 0 ({seat0Class,-10}) wins : {p0Wins,4} / {matches}   {Pct(p0Wins, matches)}");
            Console.WriteLine($"  seat 1 ({seat1Class,-10}) wins : {p1Wins,4} / {matches}   {Pct(p1Wins, matches)}");

            bool clean = integrity == 0 && undecided == 0 && capped == 0 && rejects == 0 && outcomeDisagreements == 0;
            Console.WriteLine(clean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            if (misattr > 0)
                Console.WriteLine("  NOTE: mis-attributions fired — record for G1 (appliedSeq evidence gate).");
            _ = decided;
            return clean ? 0 : 1;
        }

        // ── fault (deterministic self-test of the EF-076 injector — no server needed) ──

        /// <summary>
        /// Proves the three EF-076 injectors fire, each in isolation, against a scripted in-memory
        /// inner transport. Every count printed is a pure function of (seed, profile), so this stdout
        /// is the reproducible DONE-WHEN evidence — it needs no deployed server. Exit 0 = all three
        /// fault axes behaved as specified.
        /// </summary>
        private static async Task<int> RunFaultSelfTestAsync()
        {
            const int Frames = 60;
            const ulong Seed = 0xEF076FA017UL;
            Console.WriteLine($"=== NetSoak [fault] — EF-076 injector self-test (seed 0x{Seed:X}, {Frames} inbound frames/scenario) ===");

            var latency = await RunScenarioAsync("latency+jitter", new FaultProfile { LatencyMs = 5, JitterMs = 10 }, Frames, Seed);
            var loss = await RunScenarioAsync("loss", new FaultProfile { LossPermille = 300 }, Frames, Seed);
            var disc = await RunScenarioAsync("disconnect", new FaultProfile { DisconnectAfterFrames = 10 }, Frames, Seed);
            var combo = await RunScenarioAsync("combined", new FaultProfile { LatencyMs = 2, JitterMs = 4, LossPermille = 200, DisconnectAfterFrames = 40 }, Frames, Seed);

            bool latencyOk = latency.ft.LatencyScheduledMs > 0 && latency.ft.FramesDropped == 0
                             && !latency.ft.DisconnectFired && latency.delivered == Frames;
            bool lossOk = loss.ft.FramesDropped > 0 && !loss.ft.DisconnectFired
                          && loss.delivered == Frames - loss.ft.FramesDropped;
            bool disconnectOk = disc.ft.DisconnectFired && disc.closed == WireCloseReason.Fault
                                && disc.delivered == 10 - 1;

            Console.WriteLine();
            Console.WriteLine($"  latency axis    : {(latencyOk ? "PASS" : "FAIL")}  (scheduled {latency.ft.LatencyScheduledMs}ms over {latency.delivered} frames, 0 dropped)");
            Console.WriteLine($"  loss axis       : {(lossOk ? "PASS" : "FAIL")}  ({loss.ft.FramesDropped}/{Frames} dropped, {loss.delivered} delivered)");
            Console.WriteLine($"  disconnect axis : {(disconnectOk ? "PASS" : "FAIL")}  (fired after {disc.delivered} delivered, reason {disc.closed})");
            Console.WriteLine($"  combined        : {combo.delivered} delivered, {combo.ft.FramesDropped} dropped, disconnect={combo.ft.DisconnectFired}");

            bool clean = latencyOk && lossOk && disconnectOk;
            Console.WriteLine(clean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            return clean ? 0 : 1;
        }

        private static async Task<(int delivered, FaultTransport ft, WireCloseReason? closed)> RunScenarioAsync(
            string name, FaultProfile profile, int frames, ulong seed)
        {
            var inner = new ScriptedInner(frames);
            var ft = new FaultTransport(inner, profile, seed);
            WireCloseReason? closed = null;
            ft.OnClosed += (r, _) => closed ??= r;

            int got = 0;
            while (await ft.ReceiveAsync(CancellationToken.None) is not null) got++;

            Console.WriteLine($"  [{name,-14}] {profile}");
            Console.WriteLine($"        inner={frames}  delivered={ft.FramesDelivered}  dropped={ft.FramesDropped}  " +
                              $"latencyScheduled={ft.LatencyScheduledMs}ms  disconnect={ft.DisconnectFired}" +
                              (closed is not null ? $" ({closed})" : ""));
            return (got, ft, closed);
        }

        /// <summary>A minimal inner <see cref="IWireTransport"/> that yields <c>frames</c> distinct
        /// frames from <see cref="ReceiveAsync"/> and then reports peer-close (null) — just enough to
        /// drive the injector's receive path in <see cref="RunFaultSelfTestAsync"/>.</summary>
        private sealed class ScriptedInner : IWireTransport
        {
            private int _remaining;
            private int _seq;
            public ScriptedInner(int frames) => _remaining = frames;

            public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;
            public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]?> ReceiveAsync(CancellationToken ct)
            {
                if (_remaining-- <= 0) return Task.FromResult<byte[]?>(null);
                return Task.FromResult<byte[]?>(System.Text.Encoding.UTF8.GetBytes($"{{\"seq\":{_seq++}}}"));
            }
            public Task CloseAsync(WireCloseReason reason) => Task.CompletedTask;
            public event Action<WireCloseReason, string?>? OnClosed { add { } remove { } }
        }

        // ── probe (transport handshake vs receive isolation) ───────────────────────

        private static async Task<int> RunProbeAsync(Uri wsUri)
        {
            Console.WriteLine($"[probe] target: {wsUri}");
            var a = new WebSocketTransport();
            var b = new WebSocketTransport();
            try
            {
                Console.WriteLine("[probe] handshaking A then B SEQUENTIALLY (real-play pattern) …");
                await a.ConnectAsync(wsUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                await b.ConnectAsync(wsUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                // This probe drives the transport directly, below NetworkedMatchSession, so it has
                // to send its own hello — the gate seats nobody without one (C-G1-8).
                await SendHelloAsync(a, SoakDevice(0));
                await SendHelloAsync(b, SoakDevice(1));
                Console.WriteLine("[probe] both handshakes OK. Receiving on A AND B …");

                Task<byte[]?> ra = a.ReceiveAsync(CancellationToken.None);
                Task<byte[]?> rb = b.ReceiveAsync(CancellationToken.None);
                Task done = await Task.WhenAny(Task.WhenAll(ra, rb), Task.Delay(12000));
                string A = ra.IsCompletedSuccessfully ? $"{ra.Result?.Length ?? -1}B" : ra.Status.ToString();
                string B = rb.IsCompletedSuccessfully ? $"{rb.Result?.Length ?? -1}B" : rb.Status.ToString();
                Console.WriteLine($"[probe] A={A}  B={B}");
                bool ok = ra.IsCompletedSuccessfully && rb.IsCompletedSuccessfully
                          && ra.Result != null && rb.Result != null;
                Console.WriteLine(ok ? "[probe] GREEN — both received match_start." : "[probe] FAIL — at least one did not receive.");
                return ok ? 0 : 2;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[probe] FAIL — {ex.GetType().Name}: {ex.Message}"); return 2; }
            finally { await a.CloseAsync(WireCloseReason.Local); await b.CloseAsync(WireCloseReason.Local); }
        }

        // ── stall (silent-match-death repro: one long "think", nobody dies honestly) ──

        /// <summary>
        /// Repro for the 2026-07-19 blocker: the active client takes one human-length turn
        /// (default 40s of send-silence), the opponent waits its turn — exactly a real match.
        /// A healthy server keeps both seats alive (client heartbeats every HeartbeatIdleMs).
        /// The bug: with no client heartbeat, the server's 30s liveness check vacates the silent
        /// seat WITHOUT closing its socket, snapshots stop, the 60s grace concedes it, and the
        /// match-end broadcast reaches nobody. Exit 1 = bug reproduced, 0 = healthy.
        /// </summary>
        private static async Task<int> RunStallAsync(Uri wsUri, int thinkSeconds)
        {
            Console.WriteLine($"[stall] target: {wsUri} — active client will \"think\" for {thinkSeconds}s.");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var c0 = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = SoakDevice(0) });
            using var c1 = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = SoakDevice(1) });
            var clients = new[] { c0, c1 };
            long[] lastInbound = new long[2];
            ulong[] lastVersion = new ulong[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                clients[idx].OnSnapshot += s =>
                {
                    lastInbound[idx] = sw.ElapsedMilliseconds;
                    lastVersion[idx] = s.StateVersion;
                    Console.WriteLine($"[stall] t={sw.Elapsed.TotalSeconds:F1}s  client{idx} <- snapshot v{s.StateVersion}");
                };
            }

            await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri))
                .WaitAsync(TimeSpan.FromSeconds(20));
            Task ended0 = WaitForState(c0, ConnectionState.Ended);
            Task ended1 = WaitForState(c1, ConnectionState.Ended);
            await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            await c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));

            // Wait for Main phase, then find the active client.
            while (c0.Current.Phase == TurnPhase.Mulligan) await Task.Delay(100);
            NetworkedMatchSession active = Equals(c0.Current.ActivePlayer, c0.Player) ? c0 : c1;
            NetworkedMatchSession waiter = ReferenceEquals(active, c0) ? c1 : c0;
            int activeIdx = ReferenceEquals(active, c0) ? 0 : 1;
            Console.WriteLine($"[stall] t={sw.Elapsed.TotalSeconds:F1}s  client{activeIdx} is active; thinking {thinkSeconds}s (both clients send-silent) …");

            for (int s = 0; s < thinkSeconds; s += 5)
            {
                await Task.Delay(5000);
                Console.WriteLine($"[stall] t={sw.Elapsed.TotalSeconds:F1}s  states=({c0.State},{c1.State})  " +
                                  $"inbound-age=({AgeS(0)},{AgeS(1)})s  versions=({lastVersion[0]},{lastVersion[1]})");
            }

            Console.WriteLine($"[stall] t={sw.Elapsed.TotalSeconds:F1}s  client{activeIdx} -> EndTurn …");
            Task<CommandResult> endTurn = active.SubmitAsync(new EndTurnIntent(active.Player));
            Task first = await Task.WhenAny(endTurn, Task.Delay(10_000));
            bool endTurnAnswered = first == endTurn;
            Console.WriteLine(endTurnAnswered
                ? $"[stall] EndTurn resolved: {(endTurn.Result.Accepted ? "accepted" : endTurn.Result.Reason?.ToString())}"
                : "[stall] EndTurn UNRESOLVED after 10s — server no longer answering this seat (unbound frames drop silently).");

            Task both = Task.WhenAll(ended0, ended1);
            Task done;
            if (endTurnAnswered && endTurn.Result.Accepted)
            {
                // Healthy so far: end the match honestly and assert the ending is DELIVERED.
                Console.WriteLine($"[stall] t={sw.Elapsed.TotalSeconds:F1}s  waiter -> Concede; expecting match_end at BOTH clients …");
                _ = waiter.SubmitAsync(new ConcedeIntent(waiter.Player));
                done = await Task.WhenAny(both, Task.Delay(15_000));
            }
            else
            {
                // Bug path: ride out the disconnect-grace window (~90s from the seat's last send)
                // and show the concede-to-nobody.
                Console.WriteLine("[stall] waiting up to 100s for a match end to be DELIVERED …");
                done = await Task.WhenAny(both, Task.Delay(100_000));
            }
            for (int i = 0; i < 2; i++)
                Console.WriteLine($"[stall] client{i}: state={clients[i].State} lastVersion={lastVersion[i]} " +
                                  $"inbound-age={AgeS(i)}s outcome={clients[i].Current.Outcome}");

            bool endDelivered = done == both;
            bool starved = Math.Max(AgeS(0), AgeS(1)) > 35;
            if (!endDelivered)
            {
                Console.WriteLine("[stall] REPRODUCED — no match_end delivered; check the server replay journal for a decided outcome nobody was told about.");
                return 1;
            }
            Console.WriteLine(starved
                ? "[stall] PARTIAL — ends delivered, but a client starved >35s mid-match."
                : "[stall] HEALTHY — both clients kept alive through a human-length turn and saw the ending.");
            return starved ? 1 : 0;

            long AgeS(int i) => (sw.ElapsedMilliseconds - lastInbound[i]) / 1000;
        }

        // ── join (pass-bot opponent for an interactive peer) ───────────────────────

        private static async Task<int> RunJoinAsync(Uri wsUri)
        {
            Console.WriteLine($"[soak] join target: {wsUri} — connecting; pairing completes when the other client arrives.");
            using var c = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = SoakDevice(0) });
            await c.ConnectAsync(wsUri).WaitAsync(TimeSpan.FromMinutes(5));
            if (c.State != ConnectionState.Live)
                return Fail($"join: did not reach Live (state={c.State}, dead={c.DeadReason})");
            Console.WriteLine($"[soak] join LIVE — seat {c.Seat} as {c.Player}; pass-only until decided.");

            Task ended = WaitForState(c, ConnectionState.Ended);
            bool mulliganed = false;
            ulong actedAt = 0;

            while (!ended.IsCompleted && c.State == ConnectionState.Live)
            {
                GameState s = c.Current;
                if (s != null)
                {
                    if (!mulliganed && s.Phase == TurnPhase.Mulligan)
                    {
                        mulliganed = true;
                        await c.SubmitAsync(new MulliganIntent(c.Player, Array.Empty<InstanceId>()));
                        Console.WriteLine("[soak] join: mulligan confirmed (keep all).");
                    }
                    else if (s.Phase == TurnPhase.Main && s.ActivePlayer == c.Player
                             && s.StateVersion > actedAt)
                    {
                        actedAt = s.StateVersion;
                        CommandResult r = await c.SubmitAsync(new EndTurnIntent(c.Player));
                        Console.WriteLine($"[soak] join: v{actedAt} EndTurn → {(r.Accepted ? "ok" : r.Reason?.ToString())}");
                    }
                }
                await Task.Delay(150);
            }

            await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(5)));
            Console.WriteLine($"[soak] join: outcome={c.Current?.Outcome} resyncs={c.ResyncCount}");
            return 0;
        }

        private static Task WaitForState(NetworkedMatchSession s, ConnectionState target)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.OnConnectionStateChanged += st => { if (st == target) tcs.TrySetResult(true); };
            if (s.State == target) tcs.TrySetResult(true);
            return tcs.Task;
        }

        // ── reporting ──────────────────────────────────────────────────────────────

        private static int Report(string mode, int matches, int integrity, int misattr, int undecided, int outcomeDisagreements)
        {
            Console.WriteLine();
            Console.WriteLine($"=== NetSoak [{mode}] — {matches} match(es) ===");
            Console.WriteLine($"  integrity failures (resyncs) : {integrity}");
            Console.WriteLine($"  undecided outcomes           : {undecided}");
            Console.WriteLine($"  replay-outcome disagreements : {outcomeDisagreements}");
            Console.WriteLine($"  RC-F7 mis-attributions       : {misattr}   <- appliedSeq evidence gate (ruling 2)");
            bool clean = integrity == 0 && undecided == 0 && outcomeDisagreements == 0;
            Console.WriteLine(clean ? "  RESULT: CLEAN" : "  RESULT: FAIL");
            if (misattr > 0)
                Console.WriteLine("  NOTE: mis-attributions fired — record for G1; if this recurs in the full soak, appliedSeq returns.");
            return clean ? 0 : 1;
        }

        private static Task SendHelloAsync(IWireTransport t, string deviceToken)
            => t.SendAsync(
                ProtocolCodec.Encode(
                    new HelloMsg(deviceToken, ProtocolContract.Version, "netsoak", "netsoak"),
                    ProtocolCodec.PreMatch, 0),
                CancellationToken.None);

    }
}
