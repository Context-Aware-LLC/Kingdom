using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kingdom.Training.Corpus.Tests")]
[assembly: InternalsVisibleTo("Kingdom.Tools.NetSoak")]
// Kingdom.Tools.TrainingSoak(.Tests) lives in the private kingdom-training repo, which builds against this one as its game/ submodule.
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingSoak")]
// The checkpoint seat policy moved into Kingdom.Policy so the SERVER can serve a learned
// bot, and it reconstructs what a seat may know about its opponent's hand through
// KnownOpponentHandTracker. Without this grant that extraction fails to compile -- the good
// failure; the bad one would have been duplicating the tracker and letting the copies drift.
[assembly: InternalsVisibleTo("Kingdom.Policy")]
