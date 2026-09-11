using System.Runtime.CompilerServices;

// DECLARED IN A .cs FILE, NOT THE .csproj. An `.asmdef` has no friend mechanism, so Unity silently
// drops `<InternalsVisibleTo>` items from a project file while `dotnet build` and CI stay green --
// the failure surfaces in the Editor as an unrelated assembly-resolution error. This assembly is
// net10.0 and not consumed by Unity, so the trap cannot fire here; the pattern is kept because the
// next person to copy this file may be in an assembly that IS consumed. See
// `src/Emberfall.Core.Contracts/AssemblyInfo.cs`, which carries the retirement trigger.
//
// The types here were internal to Emberfall.Tools.NetSoak until the policy layer was extracted so
// the SERVER could serve a learned bot. The grants follow the code rather than the code being
// widened to public for two callers' benefit.
[assembly: InternalsVisibleTo("Emberfall.Tools.NetSoak")]
[assembly: InternalsVisibleTo("Emberfall.Net.Server")]
[assembly: InternalsVisibleTo("Emberfall.Net.Server.Tests")]
