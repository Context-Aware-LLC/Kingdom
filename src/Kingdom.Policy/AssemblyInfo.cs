using System.Runtime.CompilerServices;

// DECLARED IN A .cs FILE, NOT THE .csproj. An `.asmdef` has no friend mechanism, so Unity silently
// drops `<InternalsVisibleTo>` items from a project file while `dotnet build` and CI stay green --
// the failure surfaces in the Editor as an unrelated assembly-resolution error. This assembly is
// net10.0 and not consumed by Unity, so the trap cannot fire here; the pattern is kept because the
// next person to copy this file may be in an assembly that IS consumed. See
// `src/Kingdom.Core.Contracts/AssemblyInfo.cs`, which carries the retirement trigger.
//
// The types here were internal to Kingdom.Tools.NetSoak until the policy layer was extracted so
// the SERVER could serve a learned bot. The grants follow the code rather than the code being
// widened to public for two callers' benefit.
[assembly: InternalsVisibleTo("Kingdom.Tools.NetSoak")]
// The checkpoint, search and league commands left NetSoak for the training half of the harness.
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingSoak")]
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingSoak.Tests")]
[assembly: InternalsVisibleTo("Kingdom.Net.Server")]
[assembly: InternalsVisibleTo("Kingdom.Net.Server.Tests")]
// The corpus tool hands DistinctPlayKey to the builder, which sits beneath this assembly and so
// cannot name the rule it has to check search labels against.
[assembly: InternalsVisibleTo("Kingdom.Tools.TrainingCorpus")]
[assembly: InternalsVisibleTo("Kingdom.Training.Corpus.Tests")]
