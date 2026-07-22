using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Replay;
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

        public static ServerConfig FromEnvironment() => new ServerConfig
        {
            Urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8080",
            CardsDir = Environment.GetEnvironmentVariable("EMBERFALL_CARDS"),
            ReplaysDir = Environment.GetEnvironmentVariable("EMBERFALL_REPLAYS") ?? "replays",
        };
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

            string cardsDir = cfg.CardsDir ?? FindRepoCardData();
            var gate = new MatchGate(
                cfg.Options,
                new SystemTimeSource(),
                FileCardDatabase.LoadDirectory(cardsDir),
                new PrebuiltDeckProvider(),
                new JsonlReplayStore(cfg.ReplaysDir));

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
