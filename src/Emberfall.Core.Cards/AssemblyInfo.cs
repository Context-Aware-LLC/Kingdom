// FRIEND ASSEMBLIES LIVE HERE, IN CODE — NOT IN THE .csproj.
//
// This project is compiled by two toolchains and only one of them reads the .csproj: dotnet
// honours <InternalsVisibleTo> items, Unity builds from Emberfall.Core.Cards.asmdef and never
// opens the .csproj at all. An .asmdef has no friend mechanism, so a friend declared only in
// the .csproj is invisible to Unity while dotnet and CI stay green. The full account of the
// session that cost is in src/Emberfall.Core.Contracts/AssemblyInfo.cs.
//
// The friend below happens to be a test assembly Unity never builds, so the .csproj form would
// work today. It is written here anyway: which form is correct would otherwise depend on who
// consumes the internal, and that is not a fact anyone re-checks before adding the next one.
//
// RETIREMENT TRIGGER: if src/ ever stops being consumed by Unity as a local package (see
// CLAUDE.md, "The core is netstandard2.1, NOT .NET 10"), this file may fold back into the
// .csproj alongside the sibling declarations in Contracts and Core.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Emberfall.Core.Tests")]
