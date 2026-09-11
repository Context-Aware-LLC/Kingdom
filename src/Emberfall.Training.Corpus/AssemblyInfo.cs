using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Emberfall.Training.Corpus.Tests")]
[assembly: InternalsVisibleTo("Emberfall.Tools.NetSoak")]
// The checkpoint seat policy moved into Emberfall.Policy so the SERVER can serve a learned
// bot, and it reconstructs what a seat may know about its opponent's hand through
// KnownOpponentHandTracker. Without this grant that extraction fails to compile -- the good
// failure; the bad one would have been duplicating the tracker and letting the copies drift.
[assembly: InternalsVisibleTo("Emberfall.Policy")]
