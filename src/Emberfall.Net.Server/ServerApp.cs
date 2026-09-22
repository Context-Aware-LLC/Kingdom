using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Emberfall.Core.Collection;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Bots;
using Emberfall.Policy;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Matchmaking;
using Emberfall.Net.Server.Quests;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Replay;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Transport;
using Emberfall.Net.Server.Training;
using Emberfall.Training.Corpus;

namespace Emberfall.Net.Server
{
    /// <summary>Everything the composition root needs; env-var driven in prod, injected in tests.</summary>
    public sealed record ServerConfig
    {
        /// <summary>Kestrel binding, e.g. "http://+:8080". TLS is Caddy's job (design §11).</summary>
        public string Urls { get; init; } = "http://+:8080";
        /// <summary>Directory scanned for *.cards.json. Null → walk up to the repo's canonical dir (dev).</summary>
        public string? CardsDir { get; init; }
        public string ReplaysDir { get; init; } = "replays";
        /// <summary>Null disables live training capture. A configured directory receives only
        /// finalized replay/manifest pairs and may live on the same durable volume as replays.</summary>
        public string? TrainingInboxDir { get; init; }
        public ServerOptions Options { get; init; } = new ServerOptions();

        /// <summary>
        /// Where `match_complete` / `match_void` go (C-G1-8). Null → <see cref="NullTelemetrySink"/>:
        /// the emit points still run, nothing is recorded. Step 3 makes the durable
        /// <see cref="JsonlTelemetrySink"/> the default under <see cref="FromEnvironment"/>; tests
        /// still inject their own, and leaving it null remains an honest, visible n = 0.
        /// </summary>
        public ITelemetrySink? Telemetry { get; init; }

        /// <summary>
        /// Matchmaking policy seam. Null composes the launch FIFO/band-widening queue; tests and a
        /// later durable broker can replace it without moving socket or session ownership.
        /// </summary>
        public IMatchmakingQueue? MatchmakingQueue { get; init; }

        /// <summary>
        /// Trusted server-side rating lookup for queue tickets. Null preserves today's unrated
        /// guest behavior by using <see cref="MatchmakingQueueOptions.DefaultRating"/>; ratings
        /// never come from a client hello. Do not attach a persisted rating to the bearer-token
        /// <see cref="AccountId"/> until VULN-003's authentication requirement is satisfied.
        /// </summary>
        public Func<AccountId, int>? MatchmakingRatingForAccount { get; init; }

        /// <summary>
        /// Server-authoritative ranked ladder. Null composes an in-memory store for local/dev use;
        /// production binds the same Supabase Postgres connection as accounts.
        /// </summary>
        public IRatingStore? RatingStore { get; init; }

        /// <summary>
        /// Supabase JWT verifier (EF-074a/c). Null → auth tokens in HelloMsg are rejected with
        /// 1008 "authentication not configured". Populated from <c>EMBERFALL_SUPABASE_URL</c> in
        /// production; injected directly in tests. Absent tokens always take the guest path.
        /// </summary>
        public IIdentityTokenVerifier? TokenVerifier { get; init; }

        /// <summary>
        /// Account persistence seam (EF-072/c). Null → authenticated identity is derived directly
        /// from the subject hash without a store lookup; email is not persisted. Populated from
        /// <c>EMBERFALL_ACCOUNTS_DB</c> in production; injected directly in tests.
        /// </summary>
        public IAccountStore? AccountStore { get; init; }

        /// <summary>
        /// The characters-and-decks store (Task 2) that <c>GET /roster</c> reads. Null → the route
        /// answers <see cref="Accounts.RosterResultStatus.Unavailable"/> rather than an empty roster,
        /// same absence-is-visible posture as <see cref="AccountStore"/>. Populated from
        /// <c>EMBERFALL_ACCOUNTS_DB</c> in production — the same connection string as
        /// <see cref="AccountStore"/>, since <c>character</c> and <c>deck</c> are rows in that same
        /// database; injected directly in tests.
        /// </summary>
        public IPlayerDataStore? PlayerDataStore { get; init; }

        /// <summary>
        /// The append-only entitlement ledger (EF-072) — where card, pack and currency ownership
        /// actually lives. <b>Null → ownership stays UNKNOWN</b> and <see cref="AccountOwnedCounts"/>
        /// keeps handing L6 the "do not constrain" null for standard accounts, which is a normal and
        /// supported dev-box configuration. Populated from <c>EMBERFALL_ENTITLEMENTS_DB</c> in
        /// production; injected directly in tests.
        ///
        /// <para><b>It shares the accounts database by default, and should.</b> <c>account</c> and
        /// <c>entitlement_grant</c> are one canonical <c>schema.sql</c> and one
        /// <see cref="AccountsSchema.Ddl"/>, and the ledger is keyed on <c>Account.PlayerId</c> —
        /// pointing this at a different connection string than <see cref="AccountStore"/> separates an
        /// identity from its own entitlements, so <c>FromEnvironment</c> falls back to the accounts
        /// connection rather than silently leaving ownership unknown when only one variable is set.</para>
        /// </summary>
        public IEntitlementLedger? EntitlementLedger { get; init; }

        /// <summary>
        /// Where daily-quest progress is recorded. Null (default) uses
        /// <see cref="InMemoryQuestProgressStore"/>, so on a box with no Postgres a restart forgets
        /// today's progress — tolerable for dev, and the reason the durable adapter exists. Shares
        /// the accounts database in production; quest progress is keyed on the same
        /// <c>player_id</c> the ledger is, and completing a quest writes a row into that ledger.
        /// </summary>
        public IQuestProgressStore? QuestProgress { get; init; }

        /// <summary>
        /// What a finished match and a completed quest pay. Null (default) uses the shipping
        /// schedule in <see cref="GoldRewardPolicy"/>; a test injects its own so an assertion never
        /// depends on a live tuning number. It is a policy rather than a set of amounts on
        /// <see cref="ServerOptions"/> because <see cref="GoldFaucet"/> refuses to take an amount
        /// from a caller at all — reward quantities have exactly one owner.
        /// </summary>
        public IGoldRewardPolicy? GoldRewards { get; init; }

        /// <summary>
        /// Where a pack open gets its seed. Null (default) reads
        /// <see cref="HmacPackSeedSource.SecretEnvVar"/> from the environment, and if that is unset
        /// too the pack line is DISABLED rather than seeded with a default — see the composition root
        /// for why a default secret is a published one. Injected directly in tests, where
        /// <see cref="FixedPackSeedSource"/> makes an open reproducible.
        /// </summary>
        public IPackSeedSource? PackSeedSource { get; init; }

        /// <summary>
        /// Override for booster pity counters. Null uses the durable ledger-derived projection;
        /// an injected in-memory store is useful for isolated tests.
        /// </summary>
        public IPackPityStore? PackPityStore { get; init; }

        /// <summary>
        /// The off-box drain that ships the JSONL buffer into durable Postgres (C-G1-8; step 5). Null
        /// unless <c>EMBERFALL_TELEMETRY_DB</c> is set — the JSONL buffer is written either way, so a box
        /// with no DB is a visible "buffered, not shipped", never a silent drop. <see cref="ServerApp"/>
        /// registers a non-null drain as a hosted service so it lives on the host's lifecycle.
        /// </summary>
        public TelemetryDrain? TelemetryDrain { get; init; }

        /// <summary>
        /// Path to the bot roster JSON (design doc §Bot roster & provisioning). Null (default) disables
        /// bot-fill entirely — matchmaking behaves exactly as it does today. Requires
        /// <see cref="AccountStore"/> to also be set; if it is not, bot-fill is skipped with a logged
        /// warning rather than a startup failure, since a dev box without an account store is a normal,
        /// supported configuration.
        /// </summary>
        public string? BotRosterPath { get; init; }

        /// <summary>
        /// A trained checkpoint for bot seats to play, and the RL export whose feature spec it was
        /// trained against. BOTH or NEITHER: a checkpoint without its export cannot be encoded for,
        /// and an export without a checkpoint has nothing to run. Unset means bot seats play
        /// <see cref="GreedyAi"/>, which is what every existing deploy does today.
        /// </summary>
        public string? BotCheckpointPath { get; init; }
        public string? BotRlExportPath { get; init; }

        public static ServerConfig FromEnvironment()
        {
            string telemetryDir = Environment.GetEnvironmentVariable("EMBERFALL_TELEMETRY") ?? "telemetry";
            var sink = new JsonlTelemetrySink(telemetryDir, ResolveEnvLabel());

            // The durability boundary is the DB, not the volume; with no connection string the drain is
            // simply absent and the buffer accumulates locally (decision 2026-07-22).
            string? dbConn = Environment.GetEnvironmentVariable("EMBERFALL_TELEMETRY_DB");
            TelemetryDrain? drain = string.IsNullOrWhiteSpace(dbConn)
                ? null
                : new TelemetryDrain(sink.Path, dbConn!);

            // EF-074c: Supabase JWT verifier (optional — absent means auth tokens are rejected;
            // guest connects are always accepted). Mirrors the telemetry-drain pattern.
            string? supabaseUrl = Environment.GetEnvironmentVariable("EMBERFALL_SUPABASE_URL");
            IIdentityTokenVerifier? tokenVerifier = string.IsNullOrWhiteSpace(supabaseUrl)
                ? null
                : new SupabaseTokenVerifier(supabaseUrl);

            // EF-072/074c: durable account store (optional — absent means email is not persisted
            // and authenticated identity is derived from the subject hash alone).
            string? accountsDb = Environment.GetEnvironmentVariable("EMBERFALL_ACCOUNTS_DB");
            IAccountStore? accountStore = string.IsNullOrWhiteSpace(accountsDb)
                ? null
                : new PostgresAccountStore(accountsDb!, new SystemTimeSource());
            // GET /roster's store — same connection as accounts, since character/deck are rows in
            // that same database. Null-when-unconfigured mirrors accountStore exactly.
            IPlayerDataStore? playerDataStore = string.IsNullOrWhiteSpace(accountsDb)
                ? null
                : new PostgresPlayerDataStore(accountsDb!);
            IRatingStore? ratingStore = string.IsNullOrWhiteSpace(accountsDb)
                ? null
                : new PostgresRatingStore(accountsDb!);

            // The entitlement ledger — the write path that makes an unowned card a real thing. It
            // falls back to the accounts connection because the ledger is keyed on Account.PlayerId
            // and both tables come from one schema.sql: a box that configured accounts and not this
            // would otherwise run authenticated players with their ownership permanently unknown,
            // which looks exactly like owning everything. The separate variable stays accepted so a
            // deployment CAN split them deliberately.
            string? entitlementsDb = Environment.GetEnvironmentVariable("EMBERFALL_ENTITLEMENTS_DB");
            if (string.IsNullOrWhiteSpace(entitlementsDb)) entitlementsDb = accountsDb;
            IEntitlementLedger? entitlementLedger = string.IsNullOrWhiteSpace(entitlementsDb)
                ? null
                : new PostgresEntitlementLedger(entitlementsDb!);

            // Quest progress rides the same connection as the ledger it pays into — a quest
            // completing writes a row there, and splitting the two would let a box record progress
            // it cannot pay for. Without one the in-memory store stands in, which means a restart
            // forgets today's progress: fine for dev, and the reason this line exists at all.
            IQuestProgressStore? questProgress = string.IsNullOrWhiteSpace(entitlementsDb)
                ? null
                : new PostgresQuestProgressStore(entitlementsDb!);

            return new ServerConfig
            {
                Urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8080",
                CardsDir = Environment.GetEnvironmentVariable("EMBERFALL_CARDS"),
                ReplaysDir = Environment.GetEnvironmentVariable("EMBERFALL_REPLAYS") ?? "replays",
                TrainingInboxDir = Environment.GetEnvironmentVariable("EMBERFALL_TRAINING_INBOX"),
                Telemetry = sink,
                TelemetryDrain = drain,
                TokenVerifier = tokenVerifier,
                AccountStore = accountStore,
                PlayerDataStore = playerDataStore,
                EntitlementLedger = entitlementLedger,
                QuestProgress = questProgress,
                RatingStore = ratingStore,
                // Bot-fill (design doc §Bot roster & provisioning) is otherwise unreachable on a real
                // deployment — null here (unset) is what keeps every existing deploy's matchmaking
                // behavior unchanged, same as every other optional seam above.
                BotRosterPath = Environment.GetEnvironmentVariable("EMBERFALL_BOT_ROSTER"),
                BotCheckpointPath = Environment.GetEnvironmentVariable("EMBERFALL_BOT_CHECKPOINT"),
                BotRlExportPath = Environment.GetEnvironmentVariable("EMBERFALL_BOT_RL_EXPORT"),
            };
        }

        /// <summary>
        /// Provenance for the `env` column, resolved so that the common case cannot be forgotten.
        ///
        /// <para>The balance sample is ground locally and the durability proof runs on Fly
        /// (decision 2026-07-22), so a merged file holds rows from two machines and `source` — which
        /// only separates ai_soak from ladder — cannot tell them apart. `FLY_APP_NAME` is injected
        /// by Fly into every machine, so the deployed case self-labels without a deploy-time step
        /// anyone has to remember. `EMBERFALL_ENV` still wins when set, for the odd third host.</para>
        /// </summary>
        private static string ResolveEnvLabel()
        {
            string? explicitLabel = Environment.GetEnvironmentVariable("EMBERFALL_ENV");
            if (!string.IsNullOrWhiteSpace(explicitLabel)) return explicitLabel;

            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLY_APP_NAME"))
                ? "local"
                : "fly";
        }
    }

    /// <summary>
    /// The B1 Kestrel host (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §11): `/ws` (fresh pairing; `?reconnect=1`
    /// skips the queue and must open with a `reconnect` envelope; `?deck=1` owes a `submit_deck` frame
    /// after the hello, revalidated server-side — EF-071), `/healthz`, nothing else.
    /// Split from Program.cs so the loopback integration test can boot the REAL app on an
    /// ephemeral port with test paths.
    /// </summary>
    public static class ServerApp
    {
        /// <summary>
        /// The seat-policy factory for bot-filled matches: the configured checkpoint, or Greedy.
        ///
        /// A CONFIGURED-BUT-UNUSABLE CHECKPOINT IS FATAL, on purpose. Starting anyway with a silent
        /// Greedy fallback would mean an operator who deliberately asked for the learned bot never
        /// finds out they did not get it -- the failure would surface weeks later as "the bot feels
        /// the same", with nothing in the logs. This is the same shape as the collection-sync
        /// degrade recorded in CLAUDE.md: absorbing an error moves it somewhere less legible, so
        /// distinguish NEVER CONFIGURED (run Greedy, say nothing) from CONFIGURED AND BROKEN (refuse
        /// to start).
        ///
        /// The scorer process is shared across every bot seat. It is a single Python subprocess and
        /// starting one per seat would multiply model loads for no benefit; the protocol is
        /// request/response over stdio and the policy serialises its own calls.
        /// </summary>
        private static Func<SeatDeck, ISeatPolicy>? BuildBotPolicyFactory(ServerConfig cfg, ICardDatabase db)
        {
            bool hasCheckpoint = !string.IsNullOrWhiteSpace(cfg.BotCheckpointPath);
            bool hasExport = !string.IsNullOrWhiteSpace(cfg.BotRlExportPath);
            if (!hasCheckpoint && !hasExport) return null;
            if (hasCheckpoint != hasExport)
                throw new InvalidOperationException(
                    "EMBERFALL_BOT_CHECKPOINT and EMBERFALL_BOT_RL_EXPORT must be set together: a "
                    + "checkpoint cannot be encoded for without the export's feature spec.");

            CheckpointScorerProcess scorer;
            try
            {
                scorer = CheckpointScorerProcess.Start(
                    cfg.BotRlExportPath!, cfg.BotCheckpointPath!, "auto", null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"EMBERFALL_BOT_CHECKPOINT is set to '{cfg.BotCheckpointPath}' but the scorer "
                    + $"could not start: {ex.GetType().Name}: {ex.Message}. Refusing to start with "
                    + "bot seats silently falling back to Greedy.", ex);
            }

            Console.Error.WriteLine(
                $"[bots] learned bot seats enabled: checkpoint sha256={scorer.CheckpointSha256}");
            return deck =>
            {
                var validator = new CommandProcessor(db);
                var learned = new CheckpointSeatPolicy(
                    scorer, db, validator, deck.Deck, CheckpointSelection.Argmax);
                return new LearnedBotSeatPolicy(
                    learned, new GreedySeatPolicy(validator, db));
            };
        }

        public static WebApplication Build(ServerConfig cfg) => BuildWithGate(cfg).App;

        /// <summary>Same app, with the gate handed back so a test can read per-match attribution
        /// (C-G1-8) without a global. <paramref name="botPoolOverride"/> lets a test inject a
        /// <see cref="BotPool"/> it has already seeded via <c>MarkReady</c>, bypassing the real
        /// (async, file-reading) provisioning path for a fast, deterministic test.</summary>
        public static (WebApplication App, MatchGate Gate) BuildWithGate(ServerConfig cfg, IBotPool? botPoolOverride = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(cfg.Urls);

            // The drain rides the host lifecycle: it starts after the app is up and gets a graceful
            // StopAsync on shutdown. Absent unless EMBERFALL_TELEMETRY_DB is configured.
            if (cfg.TelemetryDrain is not null)
                builder.Services.AddSingleton<IHostedService>(cfg.TelemetryDrain);

            // NOR-254: the account store's schema is never applied on its own — mirror the drain's
            // startup-schema pattern so EMBERFALL_ACCOUNTS_DB is safe to set without a manual DDL step.
            //
            // ONE initializer, not one per store: both durable adapters run the identical
            // AccountsSchema.Ddl, because `account` and `entitlement_grant` are a single canonical
            // schema.sql. Registering both would execute the same idempotent DDL twice for nothing.
            // Fully qualified: this file does not import System.Threading.Tasks and an unqualified
            // `Task` binds to something else in scope here.
            Func<CancellationToken, System.Threading.Tasks.Task>? ensureAccountsSchema =
                cfg.AccountStore is PostgresAccountStore postgresAccountStore
                    ? postgresAccountStore.EnsureSchemaAsync
                    : cfg.EntitlementLedger is PostgresEntitlementLedger postgresLedger
                        ? postgresLedger.EnsureSchemaAsync
                        : null;
            if (ensureAccountsSchema is not null)
                builder.Services.AddSingleton<IHostedService>(new AccountSchemaInitializer(ensureAccountsSchema));

            string cardsDir = cfg.CardsDir ?? FindRepoCardData();
            var db = FileCardDatabase.LoadDirectory(cardsDir);
            // Computed ONCE, next to the load, over the same directory: the digest has to describe
            // the corpus this process is actually serving, and a later recompute could read a
            // directory that has moved underneath it. Reported on /healthz so a deployment that is
            // behind the working tree can be SEEN — see CorpusDigest for why that was not possible.
            string corpusDigest = CorpusDigest.Compute(cardsDir);
            var time = new SystemTimeSource();
            var matchmaking = cfg.MatchmakingQueue ??
                new BandWideningMatchmakingQueue(time, cfg.Options.Matchmaking);
            var ratingForAccount = cfg.MatchmakingRatingForAccount ??
                (_ => cfg.Options.Matchmaking.DefaultRating);
            IRatingStore ratingStore = cfg.RatingStore ??
                new InMemoryRatingStore(cfg.Options.Matchmaking.DefaultRating);
            var ratingWorker = new RatingWorker(ratingStore);
            builder.Services.AddSingleton<IHostedService>(ratingWorker);

            // Bot-fill (design doc §Bot roster & provisioning): a roster path with no account store
            // is a normal dev-box configuration, not a startup failure — logged and skipped.
            IBotPool? botPool = botPoolOverride;
            // A CHECKPOINT NOBODY WILL EVER ASK FOR IS ALSO A MISCONFIGURATION. The factory below
            // only runs when bot-fill is fully enabled, so an operator who sets a checkpoint and
            // forgets the roster (or has no account store) would get a silent Greedy server and no
            // error at all -- the exact failure the fatal-startup rule exists to prevent, arriving
            // through the one door it did not cover. There are THREE cases, not two: never
            // configured, configured and broken, and configured but unreachable.
            if (botPool is null && !string.IsNullOrWhiteSpace(cfg.BotCheckpointPath)
                && (string.IsNullOrWhiteSpace(cfg.BotRosterPath) || cfg.AccountStore is null))
            {
                throw new InvalidOperationException(
                    "EMBERFALL_BOT_CHECKPOINT is set but bot-fill is not enabled: it needs "
                    + "EMBERFALL_BOT_ROSTER and a configured AccountStore. Refusing to start as a "
                    + "Greedy server while configured for a learned one.");
            }

            if (botPool is null && !string.IsNullOrWhiteSpace(cfg.BotRosterPath))
            {
                if (cfg.AccountStore is null)
                {
                    Console.Error.WriteLine("[bots] BotRosterPath is set but no AccountStore is configured — bot-fill disabled.");
                }
                else
                {
                    var roster = BotRoster.LoadFromFile(cfg.BotRosterPath!, db);
                    var pool = new BotPool(db, BuildBotPolicyFactory(cfg, db));
                    builder.Services.AddSingleton<IHostedService>(
                        new BotAccountProvisioner(cfg.AccountStore, roster, pool));
                    botPool = pool;
                }
            }

            var replayStore = new JsonlReplayStore(cfg.ReplaysDir);
            ITrainingEpisodeStore? trainingEpisodes = string.IsNullOrWhiteSpace(cfg.TrainingInboxDir)
                ? null
                : new JsonlTrainingEpisodeStore(
                    cfg.TrainingInboxDir!,
                    replayStore.PathFor,
                    CorpusFiles.Sha256CardData(cardsDir),
                    ResolveBuildRevision(),
                    cfg.Options.WireContractVersion);

            // The onboarding grant needs a ledger to write to; without one there is no economy on this
            // box and nothing to grant into, so it stays null and every connect behaves as it did.
            OnboardingGrantService? onboarding = cfg.EntitlementLedger is null
                ? null
                : new OnboardingGrantService(cfg.EntitlementLedger, db, time);

            // The collection and shop routes share one economy projection and the same ledger.
            EconomyService? economy = cfg.EntitlementLedger is null
                ? null
                : new EconomyService(cfg.EntitlementLedger, time);
            CraftingService? crafting = cfg.EntitlementLedger is null
                ? null
                : new CraftingService(cfg.EntitlementLedger, db, time);

            // The match Gold faucet. It needs BOTH a ledger to pay into and an account store to
            // resolve a seat's player_id through, and either absence is an ordinary dev-box
            // configuration rather than a failure: no faucet is registered, the ranked sink stays
            // the rating worker alone, and matches finish exactly as they did. On a box that has
            // both, every eligible ladder match and the human seat of bot fill pay — see
            // MatchRewardWorker for why this rides the ranked sink rather than a seam of its own.
            IRankedMatchSink matchSink = ratingWorker;
            IGoldRewardPolicy goldRewards = cfg.GoldRewards ?? new GoldRewardPolicy();
            Func<AccountId, bool>? isBotAccount = botPool is null ? null : botPool.IsBotAccount;
            if (cfg.EntitlementLedger is not null && cfg.AccountStore is not null)
            {
                var rewardWorker = new MatchRewardWorker(
                    new GoldFaucet(cfg.EntitlementLedger, time, goldRewards),
                    cfg.AccountStore,
                    isBotAccount);
                builder.Services.AddSingleton<IHostedService>(rewardWorker);
                matchSink = new CompositeRankedMatchSink(ratingWorker, rewardWorker);
            }
            else
            {
                Console.Error.WriteLine(
                    "[rewards] no {0} configured — match Gold rewards are DISABLED and every finished " +
                    "match pays nothing.",
                    cfg.EntitlementLedger is null ? "entitlement ledger" : "account store");
            }

            // Daily quests (dev-docs/specs/daily-quests.md). Same two prerequisites as the reward
            // worker and the same posture on their absence: no quest service, GET /quests answers
            // 503 "unavailable", and the telemetry sink is left exactly as configured.
            //
            // The sink is DECORATED rather than replaced — quest progress is a second reader of
            // events that already exist, and it must not be able to change what the telemetry
            // sample records. Everything downstream keeps seeing every event.
            QuestProgressService? questService = null;
            ITelemetrySink? telemetry = cfg.Telemetry;
            if (cfg.EntitlementLedger is not null && cfg.AccountStore is not null)
            {
                questService = new QuestProgressService(
                    cfg.QuestProgress ?? new InMemoryQuestProgressStore(),
                    cfg.AccountStore,
                    new GoldFaucet(cfg.EntitlementLedger, time, goldRewards),
                    isBotAccount);

                var questSink = new QuestProgressSink(
                    telemetry ?? NullTelemetrySink.Instance, questService, db);
                builder.Services.AddSingleton<IHostedService>(questSink);
                telemetry = questSink;
            }

            // The pack line. Two things have to be present for a pack to be openable at all, and
            // BOTH absences are configuration rather than failure — the route answers 503 and the
            // rest of the server is unaffected:
            //
            //   a ledger        — there is nothing to debit a pack from;
            //   a seed secret   — HmacPackSeedSource.FromEnvironment() returns null when
            //                     EMBERFALL_PACK_SEED_SECRET is unset, ON PURPOSE. A default secret
            //                     is a published secret, and a predictable seed lets a client
            //                     compute its own pack contents before opening them.
            //
            // Logged loudly rather than silently skipped: an unset secret in production is a
            // deployment that grants five starter packs and cannot open one, which on the client
            // looks like the gift screen being broken (#346) rather than like a missing env var.
            IPackSeedSource? packSeeds = cfg.PackSeedSource ?? HmacPackSeedSource.FromEnvironment();
            BoosterPackGrantService? packOpener = null;
            if (cfg.EntitlementLedger is not null)
            {
                if (packSeeds is null)
                {
                    Console.Error.WriteLine(
                        "[packs] {0} is unset — POST /packs/open is DISABLED and every granted pack " +
                        "is unopenable. Set it to enable the pack line.",
                        HmacPackSeedSource.SecretEnvVar);
                }
                else
                {
                    // Pity comes from completed open audit rows in the durable entitlement ledger.
                    // A process restart or a crash between completion and the old cursor save
                    // cannot reset a paid pack's epic/legendary streak.
                    packOpener = new BoosterPackGrantService(
                        cfg.EntitlementLedger,
                        cfg.PackPityStore ?? new LedgerPackPityStore(cfg.EntitlementLedger, db),
                        packSeeds, db, time);
                }
            }

            var gate = new MatchGate(
                cfg.Options,
                time,
                db,
                new PrebuiltDeckProvider(),
                replayStore,
                matchmaking,
                ratingForAccount,
                telemetry,
                cfg.TokenVerifier,
                cfg.AccountStore,
                botPool,
                cfg.MatchmakingRatingForAccount is null ? ratingStore : null,
                matchSink,
                trainingEpisodes,
                onboarding,
                cfg.EntitlementLedger);

            var app = builder.Build();
            app.UseWebSockets();

            app.MapGet("/", () => "Emberfall match server. Connect a WebSocket to /ws.");
            app.MapGet("/healthz", () => Results.Json(new
            {
                status = "ok",
                sessions = gate.LiveSessions,
                cap = gate.MaxSessions,
                contract = cfg.Options.WireContractVersion,
                corpus = corpusDigest,
                cards = db.Count,
                trainingCapture = trainingEpisodes is not null,
            }));
            app.MapGet("/leaderboard", async (HttpContext context) =>
            {
                int limit = 50;
                if (int.TryParse(context.Request.Query["limit"], out int requested))
                    limit = Math.Clamp(requested, 1, 100);

                var entries = await ratingStore.GetLeaderboardAsync(limit, context.RequestAborted);
                return Results.Json(new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    ratingSystem = "elo",
                    entries,
                });
            });

            // GET /quests — the player's active quests and their progress
            // (dev-docs/specs/daily-quests.md §8). There is deliberately no verb that moves
            // progress: the server is the only thing that may, so the surface is read-only by
            // construction rather than by validation.
            app.MapGet("/quests", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                QuestReadResult result = await QuestsEndpoint.ReadAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, questService,
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(time.NowMs).UtcDateTime),
                    context.RequestAborted);

                context.Response.Headers["Cache-Control"] = "no-store";

                // An ARRAY OF FLAT OBJECTS, every value a scalar. AuthJson.TryReadObjectArray reads
                // exactly this and skips nested structure whole — a `reward: { gold: 60 }` would
                // decode to nothing, silently, the same trap that made /collection's `cards` an
                // object rather than an array of pairs.
                var quests = new List<object>(result.Quests.Count);
                foreach (QuestView quest in result.Quests)
                {
                    quests.Add(new
                    {
                        id = quest.Id,
                        title = quest.Title,
                        goal = quest.Goal,
                        progress = quest.Progress,
                        rewardGold = goldRewards.DailyQuestGold(quest.Id),
                        day = quest.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        state = quest.Complete ? "complete" : "active",
                    });
                }

                return Results.Json(new
                {
                    // "known" | "unauthenticated" | "unavailable". The client branches on this and
                    // never on the quest count: an empty list and a failed read must not be the
                    // same answer, or an outage renders as a finished day.
                    status = result.Status.ToString().ToLowerInvariant(),
                    playerId = result.PlayerId,
                    quests,
                }, statusCode: result.HttpStatus);
            });

            // GET /session — the login handshake the client boot blocks on. Turns a locally-stored
            // token into a verified account identity plus the onboarded flag. See SessionEndpoint
            // for why the login gate's own name does not already guarantee this.
            //
            // Cache-Control: no-store. A session is per-identity and its onboarded flag changes
            // underneath it; a proxy that cached one player's would serve it to the next bearer.
            app.MapGet("/session", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                SessionResult result = await SessionEndpoint.ReadAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, onboarding, context.RequestAborted);

                context.Response.Headers["Cache-Control"] = "no-store";

                return Results.Json(new
                {
                    // "ready" | "unauthenticated" | "unavailable". The client branches on this
                    // string and on nothing else — onboarded is only meaningful under "ready".
                    status = result.Status.ToString().ToLowerInvariant(),
                    playerId = result.PlayerId,
                    email = result.Email,
                    onboarded = result.Onboarded,
                }, statusCode: result.HttpStatus);
            });

            // GET /roster — the characters and decks read down after the handshake. Same auth
            // pairing as /session; does NOT fire the onboarding grant, which /session already owns.
            //
            // Cache-Control: no-store. A roster is per-identity and changes on every character or
            // deck save; a proxy that cached one player's would serve it to the next bearer.
            app.MapGet("/roster", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                RosterResult result = await RosterEndpoint.ReadAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, cfg.PlayerDataStore, context.RequestAborted);

                context.Response.Headers["Cache-Control"] = "no-store";

                // "known" | "unauthenticated" | "unavailable". The client branches on this string,
                // never on the character/deck count — see RosterResultStatus.Known.
                string status = result.Status.ToString().ToLowerInvariant();

                // decks is OMITTED on anything but "known". RosterResult falls back
                // to PlayerRoster.Empty as a placeholder, and serialising that placeholder would ship
                // a well-formed empty roster alongside a 401 or a 503 — "I could not find out" made
                // readable as "this player has nothing", which is the exact confusion this whole
                // handshake exists to prevent. An absent field cannot be misread as an empty one.
                if (result.Status != RosterResultStatus.Known)
                    return Results.Json(new { status }, statusCode: result.HttpStatus);

                return Results.Json(new
                {
                    status,
                    decks = CollectionJson.WriteDecks(result.Roster.Decks),
                }, statusCode: result.HttpStatus);
            });

            // POST /characters and DELETE /characters/{id} stood here until 2026-09-04 and went
            // with characters (ADR-0009). Nothing replaces them: a deck is created by PUT
            // /decks/{id} like any other deck, and deleted by DELETE /decks/{id}. The delete
            // route's cascade — a character delete destroying its decks — has nothing left to
            // cascade to, because deleting a deck now destroys exactly the thing named.

            // PUT /decks/{id} — upsert a deck. Same auth pairing as /session and /roster. DECKS ARE
            // STORED, NEVER REFUSED for their CONTENTS — see DeckWriteEndpoint for why an illegal
            // deck is still saved. What IS refused is a write that cannot be ATTRIBUTED: a body that
            // will not parse (400, Malformed), an empty/whitespace deck id (400, InvalidId), or an
            // id another player already owns (409, DuplicateId).
            //
            // AND ONE REFUSAL ABOUT THE ACCOUNT, NOT THE DECK: a NEW deck on an account already
            // holding DeckCreation.MaxPerAccount of them (409, AtCapacity). That is the server half
            // of ADR-0009's cap, and it is the half that makes it a cap — the client's disabled "+"
            // plate is the courtesy. Editing a deck the account already holds is never refused.
            app.MapPut("/decks/{id}", async (HttpContext context, string id) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                // AUTH BEFORE READ. Reading the whole body before authenticating lets an
                // unauthenticated caller make the server buffer an unbounded body for free. POST
                // /characters carried the same guard for the same reason until ADR-0009 deleted it.
                if (!await DeckWriteEndpoint.IsAuthenticatedAsync(
                        bearer, cfg.TokenVerifier, context.RequestAborted))
                {
                    return Results.Json(
                        new { status = DeckWriteStatus.Unauthenticated.ToString().ToLowerInvariant() },
                        statusCode: new DeckWriteResult(DeckWriteStatus.Unauthenticated).HttpStatus);
                }

                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();

                DeckWriteResult result = await DeckWriteEndpoint.SaveAsync(
                    bearer, id, body, cfg.TokenVerifier, cfg.AccountStore, cfg.PlayerDataStore,
                    context.RequestAborted, cfg.EntitlementLedger);

                return Results.Json(new
                {
                    // "saved" | "unauthenticated" | "unavailable" | "malformed" | "invalidid" |
                    // "duplicateid" | "atcapacity".
                    status = result.Status.ToString().ToLowerInvariant(),
                }, statusCode: result.HttpStatus);
            });

            // DELETE /decks/{id} — remove a deck. Same auth pairing as /session and /roster. One
            // record, so delete means delete — see PlayerRoster and DeckWriteEndpoint.
            app.MapDelete("/decks/{id}", async (HttpContext context, string id) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                DeckWriteResult result = await DeckWriteEndpoint.DeleteAsync(
                    bearer, id, cfg.TokenVerifier, cfg.AccountStore, cfg.PlayerDataStore,
                    context.RequestAborted);

                return Results.Json(new
                {
                    // "deleted" | "notfound" | "unauthenticated" | "unavailable" | "invalidid".
                    status = result.Status.ToString().ToLowerInvariant(),
                }, statusCode: result.HttpStatus);
            });

            // POST /onboarding/complete — the terminal write of the onboarding flow. The ONLY route
            // that sets onboarded; runs at the end, after the entitlement grant has landed. Same
            // auth pairing as /session and /roster. Idempotent — a retried call after a dropped
            // response still answers 200, never an error.
            app.MapPost("/onboarding/complete", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                OnboardingCompleteResult result = await OnboardingCompleteEndpoint.CompleteAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, context.RequestAborted);

                return Results.Json(new
                {
                    // "completed" | "unauthenticated" | "unavailable".
                    status = result.Status.ToString().ToLowerInvariant(),
                }, statusCode: result.HttpStatus);
            });

            // GET /collection — what the signed-in player owns, read down from the entitlement
            // ledger. This is the wire that makes a reinstall recoverable: the ledger has been the
            // authority since 2026-09-01 and the client had no way to read it, so a player who signed
            // back into the right identity still landed on an empty local scope. See
            // CollectionEndpoint for the auth pairing and why it fires the onboarding grant.
            //
            // Cache-Control: no-store. A collection is per-identity and changes on every pack open;
            // a proxy that cached one player's would serve it to the next bearer.
            app.MapGet("/collection", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                CollectionResult result = await CollectionEndpoint.ReadAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, cfg.EntitlementLedger,
                    economy, onboarding, context.RequestAborted);

                context.Response.Headers["Cache-Control"] = "no-store";

                // The card list is an OBJECT keyed by card id, not an array of pairs: the client's
                // reader (AuthJson.TryReadNestedObject) reads a flat object of scalars and skips
                // nested structure whole, so an array of {id, copies} would decode to nothing.
                var cards = new Dictionary<string, long>(result.Cards.Count);
                foreach (var row in result.Cards)
                    cards[row.Key.ToString(CultureInfo.InvariantCulture)] = row.Value;

                // Unopened packs, by line — an OBJECT keyed by pack id for exactly the reason above,
                // and NOT the array of {id, count} the spec draft drew: this client reader cannot see
                // inside an array. The spec was corrected to match rather than the other way round.
                //
                // Always present under "known" and "unconstrained", including when empty. The client
                // treats a missing `packs` as Malformed, because a body that has lost the field and a
                // player who has opened everything must not be the same answer — the first is a
                // captive portal, the second is a finished gift.
                var packs = new Dictionary<string, long>(result.Packs.Count);
                foreach (var row in result.Packs)
                    packs[row.Key] = row.Value;

                return Results.Json(new
                {
                    // "known" | "unconstrained" | "unauthenticated" | "unavailable" — the client
                    // branches on this string, never on the card count, because an empty collection
                    // and an unknown one are different answers that would otherwise look identical.
                    ownership = result.Status.ToString().ToLowerInvariant(),
                    playerId = result.PlayerId,
                    cardBacks = result.CardBacks,
                    cards,
                    packs,
                    wallet = new
                    {
                        gold = result.Wallet.Gold,
                        gems = result.Wallet.Gems,
                        shards = result.Wallet.Shards,
                    },
                }, statusCode: result.HttpStatus);
            });

            // Shop prices and wallet come from the server. A purchase only grants an unopened pack;
            // the player chooses when to open it in Card Vault. Both routes are authenticated and
            // uncached, and purchases are unavailable if this box cannot open packs afterward.
            var shopOffers = new List<object>();
            foreach (BoosterPack retail in BoosterPack.All())
                shopOffers.Add(new
                {
                    packId = retail.Id,
                    displayName = retail.Expansion + " Pack",
                    goldPrice = EconomyService.PackPrice,
                    gemPrice = EconomyService.PackPrice,
                });

            app.MapGet("/shop", async (HttpContext context) =>
            {
                string? bearer = BearerFrom(context);
                ShopEndpointResult result = await ShopEndpoint.ReadAsync(
                    bearer, cfg.TokenVerifier, cfg.AccountStore, economy, packOpener,
                    onboarding, context.RequestAborted);
                context.Response.Headers["Cache-Control"] = "no-store";
                return Results.Json(new
                {
                    status = result.Status.ToString().ToLowerInvariant(),
                    wallet = new
                    {
                        gold = result.Wallet.Gold,
                        gems = result.Wallet.Gems,
                        shards = result.Wallet.Shards,
                    },
                    packs = shopOffers,
                }, statusCode: result.HttpStatus);
            });

            app.MapPost("/shop/purchase", async (HttpContext context) =>
            {
                string? packId = null, currency = null, requestId = null;
                try
                {
                    using var body = await JsonDocument.ParseAsync(
                        context.Request.Body, cancellationToken: context.RequestAborted);
                    if (body.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (body.RootElement.TryGetProperty("packId", out var p) &&
                            p.ValueKind == JsonValueKind.String) packId = p.GetString();
                        if (body.RootElement.TryGetProperty("currency", out var c) &&
                            c.ValueKind == JsonValueKind.String) currency = c.GetString();
                        if (body.RootElement.TryGetProperty("requestId", out var r) &&
                            r.ValueKind == JsonValueKind.String) requestId = r.GetString();
                    }
                }
                catch (JsonException) { /* Malformed body becomes InvalidRequest below. */ }

                ShopEndpointResult result = await ShopEndpoint.PurchaseAsync(
                    BearerFrom(context), packId, currency, requestId, cfg.TokenVerifier,
                    cfg.AccountStore, cfg.EntitlementLedger, economy, packOpener,
                    onboarding, context.RequestAborted);
                context.Response.Headers["Cache-Control"] = "no-store";
                return Results.Json(new
                {
                    status = result.Status.ToString().ToLowerInvariant(),
                    packId = result.PackId,
                    unopenedCount = result.UnopenedCount,
                    wallet = new
                    {
                        gold = result.Wallet.Gold,
                        gems = result.Wallet.Gems,
                        shards = result.Wallet.Shards,
                    },
                    packs = shopOffers,
                }, statusCode: result.HttpStatus);
            });

            // POST /packs/open — the verb the ledger has been missing. Body is {"packId": "..."} and
            // NOTHING ELSE: the instance id that anchors the open is minted server-side, and a client
            // that could choose it could replay one pack into unbounded opens. See PackOpenEndpoint.
            //
            // Cache-Control: no-store, for the same reason /collection sets it, and more so — this
            // response is a one-time reveal of cards that are now spent.
            app.MapPost("/packs/open", async (HttpContext context) =>
            {
                string? bearer = null;
                string? header = context.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(header) &&
                    header!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = header.Substring("Bearer ".Length).Trim();

                // Read by hand rather than through a bound DTO, because a DTO is precisely how a
                // `packInstanceId` property gets re-added "for convenience" later. There is exactly
                // one field, and anything else in the body is ignored by construction.
                string? packId = null;
                try
                {
                    using var body = await System.Text.Json.JsonDocument
                        .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                        .ConfigureAwait(false);
                    if (body.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        body.RootElement.TryGetProperty("packId", out var packIdElement) &&
                        packIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        packId = packIdElement.GetString();
                }
                catch (System.Text.Json.JsonException)
                {
                    // An unparseable body names no pack line, which is the same answer as naming an
                    // unknown one. Left to the endpoint so there is one place that decides it.
                }

                PackOpenEndpointResult result = await PackOpenEndpoint.OpenAsync(
                    bearer, packId, cfg.TokenVerifier, cfg.AccountStore, cfg.EntitlementLedger,
                    packOpener, onboarding, context.RequestAborted);

                context.Response.Headers["Cache-Control"] = "no-store";

                var awards = new List<object>(result.Awards.Count);
                foreach (PackSlotAward award in result.Awards)
                    awards.Add(new
                    {
                        cardId = award.CardId,
                        rarity = award.Rarity.ToString(),
                        // "none" | "rarefloor" | "epicpity" | "legendarypity" — reveal chrome, and on
                        // an AlreadyOpened replay always "none": which guarantee fired is not an
                        // entitlement and the ledger deliberately does not record it.
                        upgrade = award.Upgrade.ToString().ToLowerInvariant(),
                    });

                return Results.Json(new
                {
                    // "opened" | "alreadyopened" | "unknownpack" | "noneowned" | "conflict" |
                    // "unauthenticated" | "unavailable".
                    status = result.Status.ToString().ToLowerInvariant(),
                    packId = result.PackId,
                    packInstanceId = result.PackInstanceId,
                    awards,
                }, statusCode: result.HttpStatus);
            });

            async Task<IResult> HandleCraft(HttpContext context, int cardId, string? action)
            {
                string? header = context.Request.Headers["Authorization"];
                string? bearer = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                    ? header.Substring("Bearer ".Length).Trim() : null;
                CraftingResult result = await CraftingEndpoint.ExecuteAsync(
                    bearer, cardId, action, cfg.TokenVerifier, cfg.AccountStore,
                    crafting, db, context.RequestAborted);
                context.Response.Headers["Cache-Control"] = "no-store";
                return Results.Json(new
                {
                    status = result.Status,
                    cardId = result.Craft?.CardId,
                    copiesOwned = result.Craft?.CopiesOwned,
                    shards = result.Craft?.Shards,
                }, statusCode: result.HttpStatus);
            }

            app.MapGet("/crafting/{cardId:int}", (HttpContext context, int cardId) => HandleCraft(context, cardId, null));
            app.MapPost("/crafting/{cardId:int}/render", (HttpContext context, int cardId) => HandleCraft(context, cardId, "render"));
            app.MapPost("/crafting/{cardId:int}/reduce", (HttpContext context, int cardId) => HandleCraft(context, cardId, "reduce"));

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                bool reconnect = context.Request.Query.ContainsKey("reconnect");
                try
                {
                    if (reconnect)
                        await gate.HandleReconnectAsync(socket, context.RequestAborted);
                    else
                        // EF-071: ?deck=1 opts the fresh socket into client-supplied decks — it owes a
                        // SubmitDeckMsg as its second handshake frame, revalidated server-side. Absent the
                        // flag the socket is dealt the prebuilt fallback (additive; existing clients unchanged).
                        await gate.HandleFreshAsync(socket, context.RequestAborted,
                            expectDeck: context.Request.Query.ContainsKey("deck"));
                }
                catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
                {
                    // A gate fault used to escape into Kestrel's generic handler, which drops the
                    // socket with no close frame at all. The client cannot distinguish that from an
                    // opponent walking out, so it reports DeadReason.Forfeited and the player is told
                    // the match could not be found — a server-side database fault presenting as a
                    // matchmaking failure (2026-08-18, an Npgsql pool gone stale across a Fly
                    // resume-from-suspend; PostgresAccountStore.WithConnectionAsync is the fix for
                    // that particular cause, this is the fix for every future one).
                    //
                    // Close DELIBERATELY instead: 1011 is "internal error" and the reason string is
                    // what the next person reads. The client still collapses this to Forfeited today
                    // — it discards the close status in WebSocketTransport's 0x8 arm — so the honest
                    // signal here is the server log; wiring the status through is separate work.
                    Console.Error.WriteLine("[ws] handler faulted ({0}): {1}",
                        reconnect ? "reconnect" : "fresh", ex);
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                            await socket.CloseAsync(WebSocketCloseStatus.InternalServerError,
                                "server error", CancellationToken.None);
                    }
                    catch (Exception closeEx)
                    {
                        Console.Error.WriteLine("[ws] close after fault also failed: {0}", closeEx.Message);
                    }
                }
            });

            return (app, gate);
        }

        private static string? BearerFrom(HttpContext context)
        {
            string? header = context.Request.Headers["Authorization"];
            return !string.IsNullOrEmpty(header) &&
                   header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header.Substring("Bearer ".Length).Trim()
                : null;
        }

        private static string ResolveBuildRevision()
        {
            string? configured = Environment.GetEnvironmentVariable("EMBERFALL_BUILD_REVISION");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            string? image = Environment.GetEnvironmentVariable("FLY_IMAGE_REF");
            if (!string.IsNullOrWhiteSpace(image)) return image.Trim();

            return typeof(ServerApp).Assembly.GetName().Version?.ToString() ?? "unknown-build";
        }

        /// <summary>Dev convenience: locate unity/Emberfall/Assets/CardData by walking up from the
        /// binary (same convention as the test harnesses). Containers set EMBERFALL_CARDS instead.</summary>
        private static string FindRepoCardData()
        {
            string? path = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Emberfall", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException(
                "Card data not found. Set EMBERFALL_CARDS to a directory containing *.cards.json.");
        }
    }
}
