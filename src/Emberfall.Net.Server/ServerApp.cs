using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Replay;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Transport;

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
        public ServerOptions Options { get; init; } = new ServerOptions();

        /// <summary>
        /// Where `match_complete` / `match_void` go (C-G1-8). Null → <see cref="NullTelemetrySink"/>:
        /// the emit points still run, nothing is recorded. Step 3 makes the durable
        /// <see cref="JsonlTelemetrySink"/> the default under <see cref="FromEnvironment"/>; tests
        /// still inject their own, and leaving it null remains an honest, visible n = 0.
        /// </summary>
        public ITelemetrySink? Telemetry { get; init; }

        /// <summary>
        /// The off-box drain that ships the JSONL buffer into durable Postgres (C-G1-8; step 5). Null
        /// unless <c>EMBERFALL_TELEMETRY_DB</c> is set — the JSONL buffer is written either way, so a box
        /// with no DB is a visible "buffered, not shipped", never a silent drop. <see cref="ServerApp"/>
        /// registers a non-null drain as a hosted service so it lives on the host's lifecycle.
        /// </summary>
        public TelemetryDrain? TelemetryDrain { get; init; }

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

            return new ServerConfig
            {
                Urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8080",
                CardsDir = Environment.GetEnvironmentVariable("EMBERFALL_CARDS"),
                ReplaysDir = Environment.GetEnvironmentVariable("EMBERFALL_REPLAYS") ?? "replays",
                Telemetry = sink,
                TelemetryDrain = drain,
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
    /// The B1 Kestrel host (b1-match-server-design.md §11): `/ws` (fresh pairing; `?reconnect=1`
    /// skips the queue and must open with a `reconnect` envelope), `/healthz`, nothing else.
    /// Split from Program.cs so the loopback integration test can boot the REAL app on an
    /// ephemeral port with test paths.
    /// </summary>
    public static class ServerApp
    {
        public static WebApplication Build(ServerConfig cfg) => BuildWithGate(cfg).App;

        /// <summary>Same app, with the gate handed back so a test can read per-match attribution
        /// (C-G1-8) without a global.</summary>
        public static (WebApplication App, MatchGate Gate) BuildWithGate(ServerConfig cfg)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(cfg.Urls);

            // The drain rides the host lifecycle: it starts after the app is up and gets a graceful
            // StopAsync on shutdown. Absent unless EMBERFALL_TELEMETRY_DB is configured.
            if (cfg.TelemetryDrain is not null)
                builder.Services.AddSingleton<IHostedService>(cfg.TelemetryDrain);

            string cardsDir = cfg.CardsDir ?? FindRepoCardData();
            var gate = new MatchGate(
                cfg.Options,
                new SystemTimeSource(),
                FileCardDatabase.LoadDirectory(cardsDir),
                new PrebuiltDeckProvider(),
                new JsonlReplayStore(cfg.ReplaysDir),
                cfg.Telemetry);

            var app = builder.Build();
            app.UseWebSockets();

            app.MapGet("/", () => "Emberfall match server. Connect a WebSocket to /ws.");
            app.MapGet("/healthz", () => Results.Json(new
            {
                status = "ok",
                sessions = gate.LiveSessions,
                cap = gate.MaxSessions,
                contract = cfg.Options.EngineContractVersion,
            }));

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                bool reconnect = context.Request.Query.ContainsKey("reconnect");
                if (reconnect)
                    await gate.HandleReconnectAsync(socket, context.RequestAborted);
                else
                    await gate.HandleFreshAsync(socket, context.RequestAborted);
            });

            return (app, gate);
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
