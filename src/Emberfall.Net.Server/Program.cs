// B1 commit 1 ships the session core only (b1-match-server-design.md §14.1).
// The Kestrel composition root — /ws upgrade → MatchGate, /healthz, the channel
// pump around MatchSession.Dispatch, heartbeat driver — lands in commit 2.
System.Console.WriteLine("Emberfall.Net.Server — transport host arrives in B1 commit 2.");
