// The B1 match server (b1-match-server-design.md). Composition lives in ServerApp.Build so the
// loopback integration test boots the identical app; this file only reads the environment.
using Emberfall.Net.Server;

ServerApp.Build(ServerConfig.FromEnvironment()).Run();
