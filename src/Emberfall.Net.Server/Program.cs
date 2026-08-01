// The B1 match server (dev-docs/architecture/system-overview.md:18). Composition lives in ServerApp.Build so the
// loopback integration test boots the identical app; this file only reads the environment.
using Emberfall.Net.Server;

ServerApp.Build(ServerConfig.FromEnvironment()).Run();
